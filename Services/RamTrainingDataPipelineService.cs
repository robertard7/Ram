using System.IO;
using System.Text;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class RamTrainingDataPipelineService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly RamDbService _ramDbService;
    private readonly RamTrainingDataContractsService _contractsService;
    private readonly RamTrainingDataExtractionService _extractionService;
    private readonly RamTrainingDataTransformationService _transformationService;
    private readonly RamTrainingDataValidationService _validationService;
    private readonly RamTrainingWorkspaceDiscoveryService _workspaceDiscoveryService;

    public RamTrainingDataPipelineService(
        RamDbService? ramDbService = null,
        RamTrainingDataContractsService? contractsService = null,
        RamTrainingDataExtractionService? extractionService = null,
        RamTrainingDataTransformationService? transformationService = null,
        RamTrainingDataValidationService? validationService = null,
        RamTrainingWorkspaceDiscoveryService? workspaceDiscoveryService = null)
    {
        _ramDbService = ramDbService ?? new RamDbService();
        _contractsService = contractsService ?? new RamTrainingDataContractsService();
        _extractionService = extractionService ?? new RamTrainingDataExtractionService();
        _transformationService = transformationService ?? new RamTrainingDataTransformationService();
        _validationService = validationService ?? new RamTrainingDataValidationService();
        _workspaceDiscoveryService = workspaceDiscoveryService ?? new RamTrainingWorkspaceDiscoveryService();
    }

    public RamTrainingExportBundleRecord Export(
        string workspaceRoot,
        string? outputRoot = null,
        string? sinceUtc = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        outputRoot ??= Path.Combine(workspaceRoot, ".ram", "training-data");
        sinceUtc ??= "2000-01-01T00:00:00.0000000Z";

        var contractsRoot = Path.Combine(outputRoot, "contracts");
        var intermediateRoot = Path.Combine(outputRoot, "intermediate");
        var finalRoot = Path.Combine(outputRoot, "final");
        var reviewRoot = Path.Combine(outputRoot, "review");
        var reportsRoot = Path.Combine(outputRoot, "reports");

        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(contractsRoot);
        Directory.CreateDirectory(intermediateRoot);
        Directory.CreateDirectory(finalRoot);
        Directory.CreateDirectory(reviewRoot);
        Directory.CreateDirectory(reportsRoot);

        var sourceWorkspaceRoots = _workspaceDiscoveryService.DiscoverWorkspaceRoots(workspaceRoot);
        var artifacts = new List<ArtifactRecord>();
        var fileTouches = new List<RamFileTouchRecord>();
        var skipRecords = new List<TaskboardSkipDecisionRecord>();
        var memorySummaries = new List<MemorySummaryRecord>();

        foreach (var sourceWorkspaceRoot in sourceWorkspaceRoots)
        {
            artifacts.AddRange(_ramDbService.LoadArtifactsSince(sourceWorkspaceRoot, sinceUtc, 50000));
            fileTouches.AddRange(_ramDbService.LoadFileTouchRecordsSince(sourceWorkspaceRoot, sinceUtc, 50000));
            skipRecords.AddRange(_ramDbService.LoadTaskboardSkipRecordsSince(sourceWorkspaceRoot, sinceUtc, 50000));
            memorySummaries.AddRange(_ramDbService.LoadMemorySummariesSince(sourceWorkspaceRoot, sinceUtc, 10000));
        }

        var sourceInventory = _contractsService.BuildSourceInventory(workspaceRoot, sourceWorkspaceRoots, artifacts, fileTouches, skipRecords, memorySummaries);
        var datasetContracts = sourceInventory.DatasetContracts;
        var intermediateRows = _extractionService.Extract(workspaceRoot, artifacts, fileTouches, skipRecords);
        var candidateExamples = _transformationService.Transform(intermediateRows, datasetContracts);
        var validation = _validationService.Validate(workspaceRoot, intermediateRows, candidateExamples, datasetContracts);

        var sourceInventoryPath = Path.Combine(contractsRoot, "source_inventory.json");
        var datasetContractsPath = Path.Combine(contractsRoot, "dataset_contracts.json");
        var intakeIntermediatePath = Path.Combine(intermediateRoot, "intake_rows.jsonl");
        var coderIntermediatePath = Path.Combine(intermediateRoot, "coder_rows.jsonl");
        var validationReportPath = Path.Combine(reportsRoot, "validation_report.json");
        var exportBundlePath = Path.Combine(reportsRoot, "export_bundle.json");

        WriteJson(sourceInventoryPath, sourceInventory);
        WriteJson(datasetContractsPath, datasetContracts
            .OrderBy(current => current.Track)
            .ThenBy(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase)
            .ToList());
        WriteJsonLines(intakeIntermediatePath, intermediateRows.Where(current => current.TrackHint == RamTrainingDatasetTrack.Intake));
        WriteJsonLines(coderIntermediatePath, intermediateRows.Where(current => current.TrackHint == RamTrainingDatasetTrack.Coder));

        var finalDatasetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in datasetContracts.OrderBy(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase))
        {
            var datasetPath = Path.Combine(finalRoot, contract.FileName);
            var accepted = validation.AcceptedExamples
                .Where(current => string.Equals(current.DatasetFamily, contract.DatasetFamily, StringComparison.OrdinalIgnoreCase))
                .OrderBy(current => current.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .ThenBy(current => current.ExampleId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            WriteJsonLines(datasetPath, accepted);
            finalDatasetPaths[contract.DatasetFamily] = datasetPath;
        }

        var reviewQueuePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reviewQueue in validation.ReviewQueues.OrderBy(current => current.Key, StringComparer.OrdinalIgnoreCase))
        {
            var reviewPath = Path.Combine(reviewRoot, $"{NormalizeLabel(reviewQueue.Key)}.jsonl");
            WriteJsonLines(reviewPath, reviewQueue.Value);
            reviewQueuePaths[reviewQueue.Key] = reviewPath;
        }

        WriteJson(validationReportPath, validation.Report);

        var exportBundle = new RamTrainingExportBundleRecord
        {
            ExportId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            OutputRoot = outputRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourceWorkspaceRoots = sourceWorkspaceRoots,
            SourceInventoryPath = sourceInventoryPath,
            DatasetContractsPath = datasetContractsPath,
            IntakeIntermediatePath = intakeIntermediatePath,
            CoderIntermediatePath = coderIntermediatePath,
            FinalDatasetPaths = finalDatasetPaths,
            ReviewQueuePaths = reviewQueuePaths,
            ValidationReportPath = validationReportPath,
            ExportBundlePath = exportBundlePath
        };

        WriteJson(exportBundlePath, exportBundle);
        return exportBundle;
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8);
    }

    private static void WriteJsonLines<T>(string path, IEnumerable<T> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        foreach (var value in values)
            writer.WriteLine(JsonSerializer.Serialize(value));
    }

    private static string NormalizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        var normalized = builder.ToString();
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        return normalized.Trim('_');
    }
}
