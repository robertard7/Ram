using RAM.Models;

namespace RAM.Services;

public sealed class AgentCallPolicyService
{
    public AgentCallDecision Decide(
        AgentRole role,
        AppSettings settings,
        string selectedModel,
        AgentCallContext context)
    {
        if (!settings.EnableAdvisoryAgents)
            return Skip("Advisory agents are disabled in settings.");

        if (!context.HasCompleteInputs)
            return Skip("Required advisory inputs were incomplete.");

        if (!context.DeterministicFallbackAvailable)
            return Skip("Deterministic fallback is not available for this agent role.");

        return role switch
        {
            AgentRole.Summary => DecideSummary(settings, selectedModel, context),
            AgentRole.Suggestions => DecideSuggestions(settings, selectedModel, context),
            AgentRole.PhraseFamily => DecidePhraseFamily(settings, selectedModel, context),
            AgentRole.TemplateSelector => DecideTemplateSelector(settings, selectedModel, context),
            AgentRole.BuildProfile => DecideBuildProfile(settings, selectedModel),
            AgentRole.Forensics => DecideForensics(settings, selectedModel, context),
            _ => Skip("This advisory role is not enabled in Phase 18.")
        };
    }

    private static AgentCallDecision DecideSummary(AppSettings settings, string selectedModel, AgentCallContext context)
    {
        if (!settings.EnableSummaryAgent)
            return Skip("Summary Agent is disabled in settings.");

        if (!context.ModelSummaryRequested)
            return Skip("This controlled chain did not request advisory model formatting.");

        return Allow(ResolveModel(settings.SummaryAgentModel, selectedModel), settings.AgentTimeoutSeconds);
    }

    private static AgentCallDecision DecideSuggestions(AppSettings settings, string selectedModel, AgentCallContext context)
    {
        if (!context.ModelSummaryRequested)
            return Skip("This controlled chain did not request advisory model formatting.");

        if (!settings.EnableSuggestionAgent)
            return Skip("Suggestion Agent is disabled in settings.");

        if (context.CandidateCount == 0)
            return Skip("No suggestion candidates were available.");

        if (context.CandidateCount == 1)
            return Skip("Deterministic suggestion ordering is already sufficient for a single candidate.");

        return Allow(ResolveModel(settings.SuggestionAgentModel, selectedModel), settings.AgentTimeoutSeconds);
    }

    private static AgentCallDecision DecideBuildProfile(AppSettings settings, string selectedModel)
    {
        if (!settings.EnableBuildProfileAgent)
            return Skip("Build Profile Agent is disabled in settings.");

        return Allow(ResolveModel(settings.BuildProfileAgentModel, selectedModel), settings.AgentTimeoutSeconds);
    }

    private static AgentCallDecision DecidePhraseFamily(AppSettings settings, string selectedModel, AgentCallContext context)
    {
        if (!settings.EnablePhraseFamilyAgent)
            return Skip("Phrase Family Agent is disabled in settings.");

        if (context.CandidateCount <= 0)
            return Skip("No bounded phrase-family candidates were available.");

        return Allow(ResolveModel(settings.PhraseFamilyAgentModel, selectedModel), settings.AgentTimeoutSeconds);
    }

    private static AgentCallDecision DecideTemplateSelector(AppSettings settings, string selectedModel, AgentCallContext context)
    {
        if (!settings.EnableTemplateSelectorAgent)
            return Skip("Template Selector Agent is disabled in settings.");

        if (context.CandidateCount <= 1)
            return Skip("Deterministic template selection is already sufficient for zero or one candidate.");

        return Allow(ResolveModel(settings.TemplateSelectorAgentModel, selectedModel), settings.AgentTimeoutSeconds);
    }

    private static AgentCallDecision DecideForensics(AppSettings settings, string selectedModel, AgentCallContext context)
    {
        if (!settings.EnableForensicsAgent)
            return Skip("Forensics Agent is disabled in settings.");

        return Allow(ResolveModel(settings.ForensicsAgentModel, selectedModel), settings.AgentTimeoutSeconds);
    }

    private static AgentCallDecision Allow(string modelName, int timeoutSeconds)
    {
        var clampedTimeout = Math.Clamp(timeoutSeconds <= 0 ? 10 : timeoutSeconds, 3, 30);
        return new AgentCallDecision
        {
            ShouldCall = true,
            Reason = "Advisory agent call allowed.",
            ModelName = modelName,
            Timeout = TimeSpan.FromSeconds(clampedTimeout)
        };
    }

    private static AgentCallDecision Skip(string reason)
    {
        return new AgentCallDecision
        {
            ShouldCall = false,
            Reason = reason
        };
    }

    private static string ResolveModel(string preferredModel, string selectedModel)
    {
        return string.IsNullOrWhiteSpace(preferredModel) ? selectedModel : preferredModel.Trim();
    }
}
