using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace RAM.Services;

public sealed class OllamaAgentRuntimeClient : IAgentRuntimeClient
{
    private readonly OllamaClient _ollamaClient;

    public OllamaAgentRuntimeClient(OllamaClient? ollamaClient = null)
    {
        _ollamaClient = ollamaClient ?? new OllamaClient();
    }

    public async Task<AgentInvocationResult> InvokeAsync(
        string endpoint,
        AgentRequestEnvelope request,
        AgentInvocationOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new AgentInvocationResult
        {
            TraceId = Guid.NewGuid().ToString("N"),
            RequestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId,
            Request = request,
            ResultCategory = "runtime_exception"
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);

        try
        {
            var prompt = BuildPrompt(request);
            var rawText = await _ollamaClient.GenerateAsync(endpoint, options.ModelName, prompt, timeoutCts.Token);
            rawText ??= "";

            if (rawText.Length > options.MaxResponseCharacters)
                rawText = rawText[..options.MaxResponseCharacters];

            stopwatch.Stop();

            result.Success = !string.IsNullOrWhiteSpace(rawText);
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.RawModelText = rawText;
            result.ResultCategory = result.Success ? "runtime_ok" : "empty_response";
            result.RejectionReason = result.Success ? AgentRejectionReason.None : AgentRejectionReason.EmptyResponse;
            result.Response = new AgentResponseEnvelope
            {
                RequestId = result.RequestId,
                AgentRole = request.AgentRole,
                SchemaName = request.SchemaName,
                SchemaVersion = request.SchemaVersion,
                ReceivedUtc = DateTime.UtcNow.ToString("O"),
                RawModelText = rawText
            };

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.ResultCategory = "timeout";
            result.RejectionReason = AgentRejectionReason.Timeout;
            return result;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.ResultCategory = "transport_failure";
            result.RejectionReason = AgentRejectionReason.TransportFailure;
            result.RawModelText = ex.Message;
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.ResultCategory = "runtime_exception";
            result.RejectionReason = AgentRejectionReason.RuntimeException;
            result.RawModelText = ex.Message;
            return result;
        }
    }

    private static string BuildPrompt(AgentRequestEnvelope request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are RAM advisory agent role `{request.AgentRole}`.");
        sb.AppendLine("Return one JSON object only.");
        sb.AppendLine("Do not use markdown fences. Do not explain your work. Do not request tools.");
        sb.AppendLine($"Schema name: {request.SchemaName}");
        sb.AppendLine($"Schema version: {request.SchemaVersion}");
        sb.AppendLine("Constraints:");

        foreach (var constraint in request.Constraints)
            sb.AppendLine($"- {constraint}");

        sb.AppendLine("Input payload JSON:");
        sb.AppendLine(request.InputPayloadJson);
        sb.AppendLine("Return exactly one JSON object matching the schema.");
        return sb.ToString();
    }
}
