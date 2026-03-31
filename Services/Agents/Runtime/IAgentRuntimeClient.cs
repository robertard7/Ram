namespace RAM.Services;

public interface IAgentRuntimeClient
{
    Task<AgentInvocationResult> InvokeAsync(
        string endpoint,
        AgentRequestEnvelope request,
        AgentInvocationOptions options,
        CancellationToken cancellationToken = default);
}
