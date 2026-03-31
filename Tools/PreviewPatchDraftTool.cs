using RAM.Models;

namespace RAM.Tools;

public sealed class PreviewPatchDraftTool
{
    public string Format(
        PatchDraftRecord draft,
        ArtifactRecord artifact,
        CSharpPatchWorkContractRecord? patchContract = null,
        ArtifactRecord? contractArtifact = null,
        CSharpPatchPlanRecord? patchPlan = null,
        ArtifactRecord? planArtifact = null)
    {
        var lines = new List<string>
        {
            "Patch draft:",
            $"Target file: {DisplayValue(draft.TargetFilePath)}",
            $"Lines: {DisplayRange(draft.StartLine, draft.EndLine)}",
            $"Modification intent: {DisplayValue(draft.ModificationIntent)}",
            $"Target surface type: {DisplayValue(draft.TargetSurfaceType)}",
            $"Draft kind: {draft.DraftKind}",
            $"Can apply locally: {(draft.CanApplyLocally ? "yes" : "no")}",
            $"Requires model: {(draft.RequiresModel ? "yes" : "no")}",
            $"Confidence: {DisplayValue(draft.Confidence)}",
            $"Proposal summary: {DisplayValue(draft.ProposalSummary)}",
            $"Failure summary: {DisplayValue(draft.FailureSummary)}",
            $"Rationale: {DisplayValue(draft.RationaleSummary)}"
        };

        lines.Add("Before:");
        lines.Add(string.IsNullOrWhiteSpace(draft.OriginalExcerpt) ? "(none)" : draft.OriginalExcerpt);

        lines.Add("After:");
        lines.Add(string.IsNullOrWhiteSpace(draft.ReplacementText)
            ? string.Equals(draft.DraftKind, "rebuild_symbol_recovery", StringComparison.OrdinalIgnoreCase)
                ? "(no file edit required; rebuild-first symbol reconciliation)"
                : "(no deterministic local replacement available)"
            : draft.ReplacementText);

        if (draft.RequiresModel && !string.IsNullOrWhiteSpace(draft.ModelBrief))
        {
            lines.Add("Model brief:");
            lines.Add(draft.ModelBrief);
        }

        if (draft.RetrievalHitCount > 0 || !string.IsNullOrWhiteSpace(draft.RetrievalContextPacketArtifactRelativePath))
        {
            lines.Add("Retrieval context:");
            lines.Add($"Backend: {DisplayValue(draft.RetrievalBackend)}");
            lines.Add($"Embedder: {DisplayValue(draft.RetrievalEmbedderModel)}");
            lines.Add($"Query kind: {DisplayValue(draft.RetrievalQueryKind)}");
            lines.Add($"Hit count: {draft.RetrievalHitCount}");
            if (draft.RetrievalSourceKinds.Count > 0)
                lines.Add($"Source kinds: {string.Join(", ", draft.RetrievalSourceKinds)}");
            if (!string.IsNullOrWhiteSpace(draft.RetrievalContextPacketArtifactRelativePath))
                lines.Add($"Context packet: {draft.RetrievalContextPacketArtifactRelativePath}");
        }

        if (patchContract is not null)
        {
            lines.Add("C# patch contract:");
            lines.Add($"Modification intent: {DisplayValue(patchContract.ModificationIntent)}");
            lines.Add($"Target surface type: {DisplayValue(patchContract.TargetSurfaceType)}");
            lines.Add($"Target project: {DisplayValue(patchContract.TargetProject)}");
            lines.Add($"Mutation family: {DisplayValue(patchContract.MutationFamily)}");
            lines.Add($"Allowed edit scope: {DisplayValue(patchContract.AllowedEditScope)}");
            lines.Add($"Edit scope: {DisplayValue(patchContract.EditScope)}");
            lines.Add($"Follow-through mode: {DisplayValue(patchContract.FollowThroughMode)}");
            lines.Add($"Warning policy: {DisplayValue(patchContract.WarningPolicyMode)}");
            lines.Add($"Scope approved: {(patchContract.ScopeApproved ? "yes" : "no")}");
            if (!string.IsNullOrWhiteSpace(patchContract.IntentResolutionVersion))
                lines.Add($"Intent resolver: {patchContract.IntentResolutionVersion}");
            if (!string.IsNullOrWhiteSpace(patchContract.EditSurfacePlannerVersion))
                lines.Add($"Edit-surface planner: {patchContract.EditSurfacePlannerVersion}");
            if (patchContract.IntentClassificationReasons.Count > 0)
                lines.Add($"Intent reasons: {string.Join(", ", patchContract.IntentClassificationReasons)}");
            if (patchContract.TargetSymbols.Count > 0)
                lines.Add($"Target symbols: {string.Join(", ", patchContract.TargetSymbols)}");
            if (patchContract.TargetFiles.Count > 0)
                lines.Add($"Target files: {string.Join(", ", patchContract.TargetFiles)}");
            if (patchContract.SupportingFiles.Count > 0)
                lines.Add($"Supporting files: {string.Join(", ", patchContract.SupportingFiles)}");
            if (patchContract.VerificationRequirements.Count > 0)
                lines.Add($"Verification requirements: {string.Join(", ", patchContract.VerificationRequirements)}");
            if (patchContract.VerificationSurfaces.Count > 0)
                lines.Add($"Verification surfaces: {string.Join(", ", patchContract.VerificationSurfaces)}");
            if (patchContract.OutOfScopeSurfaces.Count > 0)
                lines.Add($"Out-of-scope surfaces: {string.Join(", ", patchContract.OutOfScopeSurfaces)}");
            if (patchContract.PlanningReasons.Count > 0)
                lines.Add($"Planning reasons: {string.Join(", ", patchContract.PlanningReasons)}");
        }

        if (patchPlan is not null)
        {
            lines.Add("C# patch plan:");
            lines.Add($"Modification intent: {DisplayValue(patchPlan.ModificationIntent)}");
            lines.Add($"Target surface type: {DisplayValue(patchPlan.TargetSurfaceType)}");
            lines.Add($"Target project: {DisplayValue(patchPlan.TargetProject)}");
            lines.Add($"Follow-through mode: {DisplayValue(patchPlan.FollowThroughMode)}");
            if (!string.IsNullOrWhiteSpace(patchPlan.IntentResolutionVersion))
                lines.Add($"Intent resolver: {patchPlan.IntentResolutionVersion}");
            if (!string.IsNullOrWhiteSpace(patchPlan.EditSurfacePlannerVersion))
                lines.Add($"Edit-surface planner: {patchPlan.EditSurfacePlannerVersion}");
            if (patchPlan.IntentClassificationReasons.Count > 0)
                lines.Add($"Intent reasons: {string.Join(", ", patchPlan.IntentClassificationReasons)}");
            lines.Add($"Validation steps: {string.Join(", ", patchPlan.ValidationSteps)}");
            if (patchPlan.VerificationRequirements.Count > 0)
                lines.Add($"Verification requirements: {string.Join(", ", patchPlan.VerificationRequirements)}");
            if (patchPlan.VerificationSurfaces.Count > 0)
                lines.Add($"Verification surfaces: {string.Join(", ", patchPlan.VerificationSurfaces)}");
            if (patchPlan.OutOfScopeSurfaces.Count > 0)
                lines.Add($"Out-of-scope surfaces: {string.Join(", ", patchPlan.OutOfScopeSurfaces)}");
            if (patchPlan.PlanningReasons.Count > 0)
                lines.Add($"Planning reasons: {string.Join(", ", patchPlan.PlanningReasons)}");
            lines.Add($"Rerun requirements: {string.Join(", ", patchPlan.RerunRequirements)}");
            lines.Add($"Planned edit count: {patchPlan.PlannedEdits.Count}");
            lines.Add($"Plan summary: {DisplayValue(patchPlan.Summary)}");
        }

        lines.Add($"Artifact synced: {artifact.RelativePath}");
        if (contractArtifact is not null)
            lines.Add($"Artifact synced: {contractArtifact.RelativePath}");
        if (planArtifact is not null)
            lines.Add($"Artifact synced: {planArtifact.RelativePath}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayRange(int startLine, int endLine)
    {
        if (startLine <= 0 && endLine <= 0)
            return "(none)";

        return startLine == endLine
            ? startLine.ToString()
            : $"{startLine}-{endLine}";
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
