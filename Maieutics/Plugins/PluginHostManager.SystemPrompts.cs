using System.Text.Json;
using Maieutics.Control;
using Maieutics.Plugins.Contributions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

internal sealed partial class PluginHostManager
{
    /// <summary>Collects approved declarations and live hooks once for a run. No cached last-good
    /// prompt survives revocation, removal or hook failure; running profiles retain their own text.</summary>
    public async Task<IReadOnlyList<SystemPromptContribution>> GetSystemPromptsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (PluginDescriptor Descriptor, PluginRegistration[] Exports)[] sources;
        CancellationToken generation;
        lock (_lifecycleGate)
        {
            if (_externallyStopped || _stopping is not null) return [];
            generation = _lifetime.Token;
        }
        lock (_gate)
        {
            sources = _descriptors.Where(descriptor => !IsApprovalBlockedLock(descriptor.Id))
                .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                .Select(descriptor => (descriptor, descriptor.Extensions
                    .Where(entry => entry.Kind == ReplExtensionPointName.SystemPrompt &&
                        entry.Data.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String)
                    .Select(entry => new PluginRegistration(descriptor.Id,
                        entry.Data.GetProperty("provider").GetString() ?? string.Empty, ReplExtensionPointName.SystemPrompt))
                    .Distinct().OrderBy(registration => registration.ExportName, StringComparer.Ordinal).ToArray()))
                .ToArray();
        }
        using var deadline = new CancellationTokenSource(SystemPromptContributionKind.CollectionWaitBudget, _timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation, deadline.Token);
        var result = new List<(PluginDescriptor Descriptor, IReadOnlyList<SystemPromptContribution> Entries)>();
        var contributionCharacters = 0;
        var request = JsonSerializer.SerializeToElement(new SystemPromptInvokePayload(workspaceRootAccessor?.Invoke()),
            ReplControlJsonContext.Default.SystemPromptInvokePayload);
        foreach (var (descriptor, exports) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation.IsCancellationRequested) break;
            var entries = new List<SystemPromptContribution>();
            var valid = true;
            var declarations = descriptor.Extensions.Where(entry => entry.Kind == ReplExtensionPointName.SystemPrompt).ToArray();
            if (declarations.Length > SystemPromptContributionKind.MaximumEntries)
            {
                _logger.LogWarning("Plugin '{PluginId}' declares too many SystemPrompt entries.", descriptor.Id);
                continue;
            }
            foreach (var declaration in declarations)
            {
                if (declaration.Data.TryGetProperty("provider", out var provider))
                {
                    if (provider.ValueKind == JsonValueKind.String && provider.GetString() is { Length: > 0 and <= 256 } export &&
                        descriptor.Workers.Any(worker => worker.ExportName == export) &&
                        !declaration.Data.TryGetProperty("name", out _) && !declaration.Data.TryGetProperty("content", out _)) continue;
                    _logger.LogWarning("Plugin '{PluginId}' declares an invalid SystemPrompt provider export.", descriptor.Id);
                    valid = false;
                    break;
                }
                using var declared = JsonDocument.Parse($"[{declaration.Data.GetRawText()}]");
                if (!SystemPromptContributionKind.TryRead(declared.RootElement, descriptor.Id, out var parsed, out var error))
                {
                    _logger.LogWarning("Plugin '{PluginId}' SystemPrompt declaration rejected: {Reason}", descriptor.Id, error);
                    valid = false;
                    break;
                }
                entries.AddRange(parsed);
            }
            if (!valid) continue;
            foreach (var registration in exports)
            {
                if (budget.IsCancellationRequested) break;
                var outcome = await InvokeSystemPromptAsync(descriptor, registration, request, budget.Token, cancellationToken).ConfigureAwait(false);
                if (outcome?.Value is not { } value) continue;
                if (SystemPromptContributionKind.TryRead(value, descriptor.Id, out var parsed, out var error))
                    entries.AddRange(parsed);
                else
                    _logger.LogWarning("Plugin '{PluginId}' SystemPrompt hook rejected: {Reason}", descriptor.Id, error);
            }
            if (entries.Count > SystemPromptContributionKind.MaximumEntries ||
                entries.Sum(entry => (long)entry.Content.Length) > SystemPromptContributionKind.MaximumSourceCharacters ||
                entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            {
                _logger.LogWarning("Plugin '{PluginId}' SystemPrompt entries exceed the source budget or repeat a name.", descriptor.Id);
                continue;
            }
            contributionCharacters += entries.Sum(entry => entry.Content.Length);
            if (contributionCharacters > SystemPromptContributionKind.MaximumTotalCharacters)
                throw new InvalidOperationException("SystemPrompt contributions exceed the per-run character budget.");
            result.Add((descriptor, entries.ToArray()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Linearize acquisition against approval, descriptor reload/removal and host stop.
        // No lock is held while invoking workers. A replaced descriptor cannot publish stale text.
        lock (_lifecycleGate)
        {
            if (generation.IsCancellationRequested || _externallyStopped || _stopping is not null) return [];
            lock (_gate)
                return result.Where(source => !IsApprovalBlockedLock(source.Descriptor.Id) &&
                        _descriptors.Any(current => ReferenceEquals(current, source.Descriptor)))
                    .SelectMany(source => source.Entries).ToArray();
        }
    }

    private async Task<ExtensionCallOutcome?> InvokeSystemPromptAsync(PluginDescriptor descriptor, PluginRegistration registration,
        JsonElement request, CancellationToken budget, CancellationToken caller)
    {
        try
        {
            Task connected;
            lock (_gate)
                connected = Socket is { State: System.Net.WebSockets.WebSocketState.Open }
                    ? Task.CompletedTask : _hostConnectionAttached.Task;
            // Provider declarations are the authority: do not lose the first run's prompt
            // because a cold worker has not published its registry frame yet.
            await connected.WaitAsync(budget).ConfigureAwait(false);
            lock (_gate)
                if (IsApprovalBlockedLock(descriptor.Id) || !_descriptors.Any(current => ReferenceEquals(current, descriptor)))
                    return null;
            var outcome = await InvokeExtensionPointAsync(registration.PluginId, registration.ExportName,
                ReplExtensionPointName.SystemPrompt, request, budget, SystemPromptContributionKind.CollectionWaitBudget).ConfigureAwait(false);
            if (!outcome.IsError && outcome.Value is not null) return outcome;
            if (!outcome.IsError)
            {
                _logger.LogWarning("Plugin '{PluginId}' SystemPrompt hook returned no instruction array.", registration.PluginId);
                return null;
            }
            _logger.LogWarning("Plugin '{PluginId}' SystemPrompt hook unavailable ({Code}).", registration.PluginId, outcome.Code);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            _logger.LogWarning("Plugin '{PluginId}' SystemPrompt collection stopped or exceeded its total budget.", registration.PluginId);
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning("Plugin '{PluginId}' SystemPrompt hook has no connected host.", registration.PluginId);
        }
        return null;
    }
}
