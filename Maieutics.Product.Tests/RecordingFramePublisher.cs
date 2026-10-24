using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Frontend;

namespace Maieutics.Product.Tests;

/// <summary>Records presentation frames and signals each publication, so a test can await
/// the frame it needs instead of sleeping long enough to "probably" have it. The presenter
/// publishes synchronously for the input.request frame and after one completed router hop
/// for a form announcement; a signal makes both paths deterministic.</summary>
internal sealed class RecordingFramePublisher(bool publishable = true) : IFrontendSessionFramePublisher
{
    private readonly Lock _gate = new();
    private readonly List<(AgentSessionId SessionId, string Type, JsonElement Data)> _published = [];
    private TaskCompletionSource _publishedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<(AgentSessionId SessionId, string Type, JsonElement Data)> Published
    {
        get
        {
            lock (_gate) return [.. _published];
        }
    }

    public bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data)
    {
        if (!publishable) return false;
        TaskCompletionSource changed;
        lock (_gate)
        {
            _published.Add((sessionId, type, data.Clone()));
            changed = _publishedChanged;
            _publishedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
        return true;
    }

    /// <summary>Waits until at least <paramref name="count"/> frames are recorded. A reader
    /// that races a publication sees the already-completed edge instead of waiting for the
    /// next frame, which may never come.</summary>
    internal async Task WaitForCountAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_published.Count >= count) return;
                changed = _publishedChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal (AgentSessionId SessionId, string Type, JsonElement Data) Single(string type)
    {
        lock (_gate)
        {
            return _published.Single(frame => frame.Type == type);
        }
    }
}
