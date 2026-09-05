using System.Text;
using System.Text.Json;

namespace Maieutics.DenoRepl;

/// <summary>
///     Stores immutable binary display payloads and returns the relative URL path under
///     which the frontend fetches the native bytes. The path is content-addressed: one
///     stored payload is one URL forever, so clients can cache aggressively and share the
///     cached representation across displays, runs, and notebooks. Binary never travels as
///     base64 through the display bundle (invariant 26).
/// </summary>
internal interface IReplDisplayObjectStore
{
    /// <summary>Stores the payload and returns its relative URL path (starting with "/").</summary>
    string Store(ReadOnlySpan<byte> content, string mime);
}

internal sealed class DenoReplExecutionCollector
{
    private const int ModelItemOverheadBytes = 64;
    private const int DigestOverheadBytes = 64;
    /// <summary>How long the collector keeps draining the output stream after the eval terminal
    /// arrives. The TS client sends all output frames before the terminal, but the frames travel a
    /// separate WebSocket, so this window absorbs the cross-socket race between the last frame and
    /// the terminal at the host.</summary>
    internal static readonly TimeSpan OutputTailDrainWindow = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<string, int> digestIndexByDisplayId = new(StringComparer.Ordinal);
    private readonly List<DenoReplDisplayDigest> digests = [];
    private readonly Dictionary<string, ReplDisplayId> displayIds;
    private readonly string executionId;
    private readonly int generation;
    private readonly DenoReplOptions options;
    private readonly List<DenoReplOutputItem> outputs = [];
    private readonly IDenoReplPresentationSink presentation;
    private readonly ReplOutputRateLimiter rateLimiter;
    private readonly string sessionId;
    private int bundleSkippedCount;
    private int clearCount;
    private int digestBytes;
    private bool digestTruncated;
    private int displayCount;
    private int displaySkippedCount;
    private int modelBytes;
    private int omittedBytes;
    private int presentationEvents;
    private int presentationTextBytes;
    private bool presentationTextTruncated;
    private int rateSkippedCount;
    private bool truncated;
    private int updateCount;

    internal DenoReplExecutionCollector(
        string sessionId,
        int generation,
        DenoReplOptions options,
        IDenoReplPresentationSink presentation,
        Dictionary<string, ReplDisplayId> displayIds,
        string executionId,
        ReplOutputRateLimiter? rateLimiter = null,
        IReplDisplayObjectStore? displayObjectStore = null)
    {
        this.sessionId = sessionId;
        this.generation = generation;
        this.options = options;
        this.presentation = presentation;
        this.displayIds = displayIds;
        this.executionId = executionId;
        this.rateLimiter = rateLimiter ?? new ReplOutputRateLimiter(options);
        this.displayObjectStore = displayObjectStore;
    }

    private readonly IReplDisplayObjectStore? displayObjectStore;

    internal async Task<DenoReplExecutionResult> ConsumeAsync(
        IDenoReplConnection connection,
        ReplEvalExecution execution,
        IAsyncEnumerable<ReplOutputFrame> outputEvents,
        CancellationToken cancellationToken)
    {
        var eval = ConsumeEvalAsync(connection, execution, cancellationToken);
        var output = ConsumeOutputAsync(execution, outputEvents, cancellationToken);
        await Task.WhenAll(eval, output).ConfigureAwait(false);

        var terminal = await execution.Completion.ConfigureAwait(false);
        return await CreateResultAsync(
            terminal,
            cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
    }

    private async Task ConsumeEvalAsync(
        IDenoReplConnection connection,
        ReplEvalExecution execution,
        CancellationToken cancellationToken)
    {
        await foreach (var replEvent in execution.Events.ConfigureAwait(false))
            try
            {
                await ObserveAsync(connection, replEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation is sent on the eval channel; keep draining ordered events to its terminal.
            }
    }

    /// <summary>
    ///     Consumes binary output frames until the eval terminal arrives. The TS client sends every
    ///     output frame over the output WebSocket before the terminal envelope over the eval channel
    ///     (it awaits each frame send before sending the terminal), so the frames for this execution
    ///     are in flight by the time the terminal is observable. The two WebSocket connections are
    ///     independent, so the terminal can still beat the last frames into the host: after the
    ///     terminal completes, a short drain window keeps reading so the buffered tail is consumed
    ///     before the collector stops. The output connection is a session-lifetime stream, so the
    ///     window expiry also ends the read instead of waiting for the next execution's frames.
    /// </summary>
    private async Task ConsumeOutputAsync(
        ReplEvalExecution execution,
        IAsyncEnumerable<ReplOutputFrame> outputEvents,
        CancellationToken cancellationToken)
    {
        using var drain = new CancellationTokenSource();
        _ = execution.Completion.ContinueWith(
            static (_, state) =>
            {
                try
                {
                    ((CancellationTokenSource)state!).CancelAfter(OutputTailDrainWindow);
                }
                catch (ObjectDisposedException)
                {
                    // The drain ended before the terminal; there is nothing to cancel.
                }
            },
            drain,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var frame in outputEvents.WithCancellation(drain.Token).ConfigureAwait(false))
            {
                try
                {
                    await ObserveAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation targets the execution; keep presenting ordered output until the
                    // eval terminal orders the tail.
                }
            }
        }
        catch (OperationCanceledException) when (drain.IsCancellationRequested)
        {
            // The eval terminal arrived and the drain window elapsed: the frames that preceded
            // it have been consumed and there is nothing left to drain.
        }
    }

    private async ValueTask ObserveAsync(
        IDenoReplConnection connection,
        ReplEvalEvent replEvent,
        CancellationToken cancellationToken)
    {
        switch (replEvent)
        {
            // The eval channel is the control plane only (AGENTS.md phase 2): console, display,
            // updateDisplay, and clearOutput events travel over the dedicated binary output
            // endpoint and are observed through the ReplOutputFrame overload. Input requests
            // remain on the eval channel.
            case ReplEvalInputRequestEvent input:
                var value = await presentation.RequestInputAsync(
                    input.Prompt,
                    input.Password,
                    cancellationToken).ConfigureAwait(false);
                await connection.ReplyInputAsync(input, value, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Observes one binary output frame from the dedicated output endpoint. Frames for
    /// other executions (leftover tail of a previous execution or the next execution racing ahead)
    /// are ignored: the eval terminal orders the boundary between executions.</summary>
    internal async ValueTask ObserveAsync(
        ReplOutputFrame frame,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(frame.ExecutionId, executionId, StringComparison.Ordinal)) return;

        switch (frame)
        {
            case ReplOutputConsoleFrame { Stream: "stdout" } stdout:
                AddText("stdout", stdout.Text);
                break;
            case ReplOutputConsoleFrame { Stream: "stderr" } stderr:
                AddText("stderr", stderr.Text);
                await PresentStderrAsync(stderr.Text, cancellationToken).ConfigureAwait(false);
                break;
            case ReplOutputDisplayFrame display:
                if (!rateLimiter.TryReserve(display))
                {
                    // The display exceeded the sliding display rate budget (jupyter_server iopub
                    // rate-limit semantics): it is dropped from both the notebook presentation and
                    // the model digest. It is still counted so the model sees an actionable
                    // signal that its display was rate-limited and can retry with a smaller
                    // payload. Dropping the event does not disturb stream order: the output
                    // stream is still consumed in order and later frames present normally.
                    rateSkippedCount++;
                    return;
                }
                await PresentDisplayAsync(display, cancellationToken).ConfigureAwait(false);
                break;
            case ReplOutputClearOutputFrame clear:
                if (TryReservePresentationEvent())
                {
                    await presentation.ClearOutputAsync(clear.Wait, cancellationToken).ConfigureAwait(false);
                    clearCount++;
                }
                break;
        }
    }

    private async Task<DenoReplExecutionResult> CreateResultAsync(
        ReplEvalTerminal terminal,
        CancellationToken cancellationToken)
    {
        var status = terminal switch
        {
            ReplEvalResultTerminal result => AddResult(result.Value),
            ReplEvalErrorTerminal error => await AddTerminalErrorAsync(error, cancellationToken).ConfigureAwait(false),
            ReplEvalCancelledTerminal => "abort",
            _ => throw new InvalidOperationException($"Unknown REPL eval terminal '{terminal.GetType().Name}'.")
        };
        return new DenoReplExecutionResult(
            sessionId,
            generation,
            null,
            status,
            outputs,
            new DenoReplPresentationResult(
                displayCount,
                updateCount,
                clearCount,
                RateSkippedCount: rateSkippedCount,
                bundleSkippedCount,
                displaySkippedCount,
                digestTruncated,
                digests),
            truncated,
            omittedBytes);
    }

    private string AddResult(JsonElement? value)
    {
        if (value is not { } result || result.ValueKind == JsonValueKind.Undefined) return "ok";
        var bytes = Encoding.UTF8.GetByteCount(result.GetRawText());
        if (TryReserveModelBytes(bytes))
        {
            outputs.Add(new DenoReplOutputItem(
                "result",
                MediaType: "application/json",
                Value: result.Clone()));
            return "ok";
        }

        var text = result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? string.Empty
            : result.GetRawText();
        AddTextResult(text);
        return "ok";
    }

    private async Task<string> AddTerminalErrorAsync(
        ReplEvalErrorTerminal error,
        CancellationToken cancellationToken)
    {
        AddError(error.Code, error.Message, []);
        await PresentErrorAsync(error.Code, error.Message, [], cancellationToken).ConfigureAwait(false);
        return "error";
    }

    private async ValueTask PresentDisplayAsync(
        ReplOutputDisplayFrame display,
        CancellationToken cancellationToken)
    {
        var bundle = new ReplDisplayBundle(display.Data);
        var metadata = display.Metadata;

        // An update that cannot target a previous display is skipped from presentation and has no
        // digest entry to fold into.
        if (display.IsUpdate &&
            (display.DisplayId is null || !displayIds.ContainsKey(display.DisplayId)))
        {
            displaySkippedCount++;
            return;
        }

        if (!CanPresentBundle(bundle, metadata))
        {
            // The bundle exceeds the notebook presentation budget, but the model still learns the
            // display was attempted through its digest entry.
            AddDisplayDigest(display, bundle);
            return;
        }

        // Rebuild the binary MIME placeholders (`{"$buffer": index}`) into their native byte
        // arrays and base64 them into the bundle. The output endpoint itself stays native
        // (invariant 26); base64 is the presentation encoding the current sink targets accept.
        // The frontend sink strips binary mimes back out before its own wire (invariant 26).
        var reconstructed = display.Buffers.Count > 0
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : null;
        if (reconstructed is not null)
        {
            foreach (var (mime, value) in bundle.Data)
            {
                var element = value.Clone();
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("$buffer", out var placeholder) &&
                    placeholder.ValueKind == JsonValueKind.Number &&
                    placeholder.TryGetInt32(out var index))
                {
                    var bytes = display.ResolveBuffer(index);
                    if (displayObjectStore is { } objects)
                    {
                        // Binary payloads become immutable content-addressed objects: the
                        // bundle carries a URL the frontend fetches natively over HTTP —
                        // never base64 through the bundle (invariant 26).
                        var url = objects.Store(bytes, mime);
                        element = JsonSerializer.SerializeToElement(
                            new ReplDisplayObjectReference(url, bytes.Length),
                            ReplJsonContext.Default.ReplDisplayObjectReference);
                    }
                    else
                    {
                        // No display object store configured (tests): the payload cannot be
                        // represented without base64, so the mime is dropped rather than
                        // violating invariant 26.
                        continue;
                    }
                }

                reconstructed[mime] = element;
            }

            bundle = new ReplDisplayBundle(reconstructed);
        }

        await PresentDisplayAsync(display.IsUpdate, display.DisplayId, bundle, metadata, cancellationToken)
            .ConfigureAwait(false);
        AddDisplayDigest(display, bundle);
    }

    private async ValueTask PresentDisplayAsync(
        bool isUpdate,
        string? innerDisplayId,
        ReplDisplayBundle bundle,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        if (isUpdate)
        {
            if (innerDisplayId is null || !displayIds.TryGetValue(innerDisplayId, out var displayId))
            {
                displaySkippedCount++;
                return;
            }

            await presentation.UpdateDisplayAsync(displayId, bundle, metadata, cancellationToken).ConfigureAwait(false);
            updateCount++;
            return;
        }

        if (innerDisplayId is { } tracked)
        {
            if (!displayIds.TryGetValue(tracked, out var outerDisplayId))
            {
                outerDisplayId = new ReplDisplayId(Guid.NewGuid().ToString("N"));
                displayIds.Add(tracked, outerDisplayId);
            }

            await presentation.DisplayTrackedAsync(
                bundle,
                outerDisplayId,
                metadata,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await presentation.DisplayAsync(bundle, metadata, cancellationToken).ConfigureAwait(false);
        }

        displayCount++;
    }

    /// <summary>Adds one bounded model digest for a display or update. Updates fold into the
    /// entry of their display id instead of creating a new one; displays without an id are not
    /// deduplicated. When the digest budget is exhausted, <see cref="digestTruncated" /> is set and
    /// later displays are counted (they still present) but not digested.</summary>
    private void AddDisplayDigest(ReplOutputDisplayFrame display, ReplDisplayBundle bundle)
    {
        var displayId = display.DisplayId;
        if (display.IsUpdate && (displayId is null || !digestIndexByDisplayId.ContainsKey(displayId)))
            return; // targeting failure already counted by PresentDisplayAsync

        var mediaTypes = bundle.Data.Keys.ToArray();
        var preview = SelectPreview(bundle);

        if (displayId is { } id && digestIndexByDisplayId.TryGetValue(id, out var index))
        {
            var existing = digests[index];
            var updated = existing with
            {
                MediaTypes = mediaTypes,
                Preview = preview ?? existing.Preview,
                IsUpdate = existing.IsUpdate || display.IsUpdate
            };
            var updatedCost = DigestCost(updated);
            if (digestBytes - DigestCost(existing) + updatedCost <= options.MaxModelDisplayDigestBytes)
            {
                digests[index] = updated;
                digestBytes = digestBytes - DigestCost(existing) + updatedCost;
            }
            else
            {
                digestTruncated = true;
            }

            return;
        }

        if (digestTruncated) return;

        var entry = new DenoReplDisplayDigest(mediaTypes, preview, displayId, display.IsUpdate);
        var cost = DigestCost(entry);
        if (digestBytes + cost > options.MaxModelDisplayDigestBytes)
        {
            digestTruncated = true;
            return;
        }

        digestBytes += cost;
        digests.Add(entry);
        if (displayId is { } tracked) digestIndexByDisplayId[tracked] = digests.Count - 1;
    }

    /// <summary>Picks the digest preview: text/plain first, then the first string <c>text/*</c> or
    /// <c>*+json</c> mime. Binary mimes (image/*, application/pdf, video/*, audio/*) are listed in
    /// <see cref="DenoReplDisplayDigest.MediaTypes" /> but never produce a preview.</summary>
    private string? SelectPreview(ReplDisplayBundle bundle)
    {
        if (bundle.Data.TryGetValue("text/plain", out var plain) && plain.ValueKind == JsonValueKind.String)
            return TruncatePreview(plain.GetString());

        foreach (var (mime, value) in bundle.Data)
        {
            if (IsBinaryMime(mime) || value.ValueKind != JsonValueKind.String) continue;
            if (mime.StartsWith("text/", StringComparison.Ordinal) ||
                mime.EndsWith("+json", StringComparison.Ordinal))
                return TruncatePreview(value.GetString());
        }

        return null;
    }

    private string? TruncatePreview(string? value)
    {
        if (value is null || value.Length == 0) return value;
        var bytes = Encoding.UTF8.GetByteCount(value);
        return bytes <= options.MaxDisplayDigestPreviewBytes
            ? value
            : TruncateUtf8(value, options.MaxDisplayDigestPreviewBytes, out _);
    }

    private static bool IsBinaryMime(string mime)
    {
        return mime.StartsWith("image/", StringComparison.Ordinal) ||
               mime.StartsWith("video/", StringComparison.Ordinal) ||
               mime.StartsWith("audio/", StringComparison.Ordinal) ||
               string.Equals(mime, "application/pdf", StringComparison.Ordinal);
    }

    private static int DigestCost(DenoReplDisplayDigest digest)
    {
        var cost = DigestOverheadBytes;
        foreach (var mime in digest.MediaTypes)
            cost = checked(cost + Encoding.UTF8.GetByteCount(mime));
        return checked(cost + Encoding.UTF8.GetByteCount(digest.Preview ?? string.Empty));
    }

    private async ValueTask PresentStderrAsync(string text, CancellationToken cancellationToken)
    {
        if (!TryReservePresentationEvent()) return;
        var available = options.MaxPresentationTextBytes - presentationTextBytes;
        if (available <= 0)
        {
            displaySkippedCount++;
            return;
        }

        var fullBytes = Encoding.UTF8.GetByteCount(text);
        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out _);
        presentationTextBytes += Encoding.UTF8.GetByteCount(selected);
        if (selected.Length > 0) await presentation.WriteStderrAsync(selected, cancellationToken).ConfigureAwait(false);
        if (fullBytes > available && !presentationTextTruncated)
        {
            presentationTextTruncated = true;
            displaySkippedCount++;
        }
    }

    private async ValueTask PresentErrorAsync(
        string name,
        string value,
        IReadOnlyList<string> traceback,
        CancellationToken cancellationToken)
    {
        if (!TryReservePresentationEvent()) return;
        var available = options.MaxPresentationTextBytes - presentationTextBytes;
        if (available <= 0)
        {
            displaySkippedCount++;
            return;
        }

        var selected = TruncateUtf8(value, available, out var selectedBytes);
        presentationTextBytes += selectedBytes;
        if (selectedBytes < Encoding.UTF8.GetByteCount(value)) displaySkippedCount++;
        await presentation.PublishErrorAsync(name, selected, traceback, cancellationToken).ConfigureAwait(false);
    }

    private bool CanPresentBundle(ReplDisplayBundle bundle, IReadOnlyDictionary<string, JsonElement> metadata)
    {
        if (!TryReservePresentationEvent()) return false;
        var bytes = CountJsonBytes(bundle.Data) + CountJsonBytes(metadata);
        if (bytes <= options.MaxPresentationBundleBytes) return true;
        bundleSkippedCount++;
        return false;
    }

    private bool TryReservePresentationEvent()
    {
        if (presentationEvents < options.MaxPresentationEventsPerExecution)
        {
            presentationEvents++;
            return true;
        }

        // The presentation event cap drops arbitrary presentation events (displays, clears,
        // stderr writes). It is counted under the display skip counter: the per-category breakdown
        // distinguishes bundle-size skips (BundleSkippedCount) from every other presentation skip
        // (DisplaySkippedCount); display rate-limit drops are counted separately (RateSkippedCount).
        displaySkippedCount++;
        return false;
    }

    private void AddText(string kind, string text)
    {
        var available = options.MaxModelOutputBytes - modelBytes - ModelItemOverheadBytes;
        var fullBytes = Encoding.UTF8.GetByteCount(text);
        if (available <= 0)
        {
            MarkOmitted(fullBytes);
            return;
        }

        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out _);
        var selectedBytes = Encoding.UTF8.GetByteCount(selected);
        outputs.Add(new DenoReplOutputItem(kind, selected));
        modelBytes += ModelItemOverheadBytes + selectedBytes;
        if (selectedBytes < fullBytes) MarkOmitted(fullBytes - selectedBytes);
    }

    private void AddTextResult(string text)
    {
        var available = options.MaxModelOutputBytes - modelBytes - ModelItemOverheadBytes;
        var fullBytes = Encoding.UTF8.GetByteCount(text);
        if (available <= 0)
        {
            MarkOmitted(fullBytes);
            return;
        }

        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out _);
        var selectedBytes = Encoding.UTF8.GetByteCount(selected);
        outputs.Add(new DenoReplOutputItem("result", selected, "text/plain"));
        modelBytes += ModelItemOverheadBytes + selectedBytes;
        if (selectedBytes < fullBytes) MarkOmitted(fullBytes - selectedBytes);
    }

    private void AddError(string name, string value, IReadOnlyList<string> traceback)
    {
        var bytes = Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value) +
                    traceback.Sum(Encoding.UTF8.GetByteCount);
        if (TryReserveModelBytes(bytes))
        {
            outputs.Add(new DenoReplOutputItem(
                "error",
                value,
                Name: name,
                Traceback: traceback.ToArray()));
            return;
        }

        var available = Math.Max(0, options.MaxModelOutputBytes - modelBytes - ModelItemOverheadBytes);
        var selected = TruncateUtf8(value, available, out var selectedBytes);
        if (selected.Length > 0)
        {
            outputs.Add(new DenoReplOutputItem("error", selected, Name: name));
            modelBytes += ModelItemOverheadBytes + selectedBytes;
        }
        MarkOmitted(Math.Max(0, bytes - selectedBytes));
    }

    private bool TryReserveModelBytes(int contentBytes)
    {
        if (contentBytes > options.MaxModelOutputBytes - modelBytes - ModelItemOverheadBytes) return false;
        modelBytes += ModelItemOverheadBytes + contentBytes;
        return true;
    }

    private void MarkOmitted(int bytes)
    {
        truncated = true;
        omittedBytes = checked(omittedBytes + bytes);
    }

    internal static int CountJsonBytes(IReadOnlyDictionary<string, JsonElement> values)
    {
        var result = 0;
        foreach (var (name, value) in values)
            result = checked(result + Encoding.UTF8.GetByteCount(name) +
                             Encoding.UTF8.GetByteCount(value.GetRawText()));
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
            return new Dictionary<string, JsonElement>();
        return value.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static string TruncateUtf8(string value, int maximumBytes, out int selectedBytes)
    {
        if (maximumBytes <= 0 || value.Length == 0)
        {
            selectedBytes = 0;
            return string.Empty;
        }

        var characters = 0;
        selectedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (selectedBytes + rune.Utf8SequenceLength > maximumBytes) break;
            selectedBytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }
        return value[..characters];
    }
}
