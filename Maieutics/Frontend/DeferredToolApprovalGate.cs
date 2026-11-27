using Maieutics.Agent;
using Maieutics.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace Maieutics.Frontend;

/// <summary>
///     Deferred composition wrapper for the tool-approval gate: the runtime configuration needs
///     an <see cref="IAgentToolApprovalGate" /> when it builds each run profile, but the real
///     presenter depends on <see cref="FrontendSessionService" />, which depends back on the
///     runtime configuration. Deferring resolution to the first approval breaks that cycle;
///     every call resolves lazily, long after composition has settled.
/// </summary>
internal sealed class DeferredToolApprovalGate(IServiceProvider services) : IAgentToolApprovalGate, IProcessExecutionApproval
{
    public ValueTask<AgentToolApprovalDecision> RequestApprovalAsync(
        AgentToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        return services.GetRequiredService<FrontendToolApprovalPresenter>()
            .RequestApprovalAsync(request, cancellationToken);
    }

    public ValueTask<AgentToolApprovalDecision> RequestAsync(
        ProcessExecutionApprovalRequest request, CancellationToken cancellationToken) =>
        services.GetRequiredService<FrontendToolApprovalPresenter>().RequestAsync(request, cancellationToken);
}
