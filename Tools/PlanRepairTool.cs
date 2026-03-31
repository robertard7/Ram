using RAM.Models;

namespace RAM.Tools;

public sealed class PlanRepairTool
{
    public string Format(
        RepairPlanInput input,
        RepairProposalRecord proposal,
        ArtifactRecord artifact,
        CSharpPatchWorkContractRecord? patchContract = null,
        ArtifactRecord? contractArtifact = null)
    {
        var lines = new List<string>
        {
            "Repair proposal:",
            $"Title: {proposal.Title}",
            $"Failure kind: {proposal.FailureKind}",
            $"Failure summary: {DisplayValue(proposal.FailureSummary)}",
            $"Target file: {DisplayValue(proposal.TargetFilePath)}",
            $"Target line: {(proposal.TargetLineNumber > 0 ? proposal.TargetLineNumber.ToString() : "(none)")}",
            $"Action type: {proposal.ProposedActionType}",
            $"Confidence: {proposal.Confidence}",
            $"Requires model: {(proposal.RequiresModel ? "yes" : "no")}",
            $"Rationale: {proposal.Rationale}"
        };

        if (!string.IsNullOrWhiteSpace(proposal.TargetProjectPath))
            lines.Add($"Target project: {proposal.TargetProjectPath}");

        if (proposal.HasAmbiguity && !string.IsNullOrWhiteSpace(proposal.AmbiguitySummary))
            lines.Add($"Ambiguity: {proposal.AmbiguitySummary}");

        if (proposal.HasAmbiguity && proposal.CandidatePaths.Count > 0)
        {
            lines.Add("Candidate files:");
            foreach (var candidate in proposal.CandidatePaths.Take(5))
                lines.Add($"- {candidate}");
        }

        if (proposal.Steps.Count > 0)
        {
            lines.Add("Steps:");
            foreach (var step in proposal.Steps.OrderBy(step => step.Order))
                lines.Add($"{step.Order}. {step.Instruction}");
        }

        if (!string.IsNullOrWhiteSpace(input.FileExcerpt))
        {
            lines.Add("Bounded excerpt:");
            lines.Add(input.FileExcerpt);
        }

        if (proposal.RequiresModel && !string.IsNullOrWhiteSpace(proposal.ModelBrief))
        {
            lines.Add("Model brief:");
            lines.Add(proposal.ModelBrief);
        }

        if (proposal.RetrievalHitCount > 0 || !string.IsNullOrWhiteSpace(proposal.RetrievalContextPacketArtifactRelativePath))
        {
            lines.Add("Retrieval context:");
            lines.Add($"Backend: {DisplayValue(proposal.RetrievalBackend)}");
            lines.Add($"Embedder: {DisplayValue(proposal.RetrievalEmbedderModel)}");
            lines.Add($"Query kind: {DisplayValue(proposal.RetrievalQueryKind)}");
            lines.Add($"Hit count: {proposal.RetrievalHitCount}");
            if (proposal.RetrievalSourceKinds.Count > 0)
                lines.Add($"Source kinds: {string.Join(", ", proposal.RetrievalSourceKinds)}");
            if (!string.IsNullOrWhiteSpace(proposal.RetrievalContextPacketArtifactRelativePath))
                lines.Add($"Context packet: {proposal.RetrievalContextPacketArtifactRelativePath}");
        }

        if (patchContract is not null)
        {
            lines.Add("C# patch contract:");
            lines.Add($"Modification intent: {DisplayValue(patchContract.ModificationIntent)}");
            lines.Add($"Target surface type: {DisplayValue(patchContract.TargetSurfaceType)}");
            lines.Add($"Mutation family: {DisplayValue(patchContract.MutationFamily)}");
            lines.Add($"Allowed edit scope: {DisplayValue(patchContract.AllowedEditScope)}");
            lines.Add($"Edit scope: {DisplayValue(patchContract.EditScope)}");
            lines.Add($"Scope approved: {(patchContract.ScopeApproved ? "yes" : "no")}");
            lines.Add($"Warning policy: {DisplayValue(patchContract.WarningPolicyMode)}");
            lines.Add($"Follow-through mode: {DisplayValue(patchContract.FollowThroughMode)}");
            lines.Add($"Scope summary: {DisplayValue(patchContract.ScopeSummary)}");
            if (patchContract.TargetSymbols.Count > 0)
                lines.Add($"Target symbols: {string.Join(", ", patchContract.TargetSymbols)}");
            if (patchContract.TargetFiles.Count > 0)
                lines.Add($"Target files: {string.Join(", ", patchContract.TargetFiles)}");
            if (patchContract.SupportingFiles.Count > 0)
                lines.Add($"Supporting files: {string.Join(", ", patchContract.SupportingFiles)}");
            if (patchContract.VerificationRequirements.Count > 0)
                lines.Add($"Verification requirements: {string.Join(", ", patchContract.VerificationRequirements)}");
        }

        lines.Add($"Artifact synced: {artifact.RelativePath}");
        if (contractArtifact is not null)
            lines.Add($"Artifact synced: {contractArtifact.RelativePath}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
