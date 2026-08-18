using System.Text;
using System.Text.Json;
using Maieutics.Jupyter.Shared;

namespace Maieutics.DenoRepl;

internal sealed class DenoReplExecutionCollector
{
    private const int ModelItemOverheadBytes = 64;
    private readonly Dictionary<string, JupyterDisplayId> displayIds;
    private readonly int generation;
    private readonly DenoReplOptions options;
    private readonly List<DenoReplOutputItem> outputs = [];
    private readonly IDenoReplPresentationSink presentation;
    private readonly string sessionId;
    private int clearCount;
    private int displayCount;
    private int modelBytes;
    private int omittedBytes;
    private int presentationEvents;
    private int presentationTextBytes;
    private bool presentationTextTruncated;
    private int skippedCount;
    private bool truncated;
    private int updateCount;

    internal DenoReplExecutionCollector(
        string sessionId,
        int generation,
        DenoReplOptions options,
        IDenoReplPresentationSink presentation,
        Dictionary<string, JupyterDisplayId> displayIds)
    {
        this.sessionId = sessionId;
        this.generation = generation;
        this.options = options;
        this.presentation = presentation;
        this.displayIds = displayIds;
    }

    internal async Task<DenoReplExecutionResult> ConsumeAsync(
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

        var terminal = await execution.Completion.ConfigureAwait(false);
        return await CreateResultAsync(
            terminal,
            cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ObserveAsync(
        IDenoReplConnection connection,
        ReplEvalEvent replEvent,
        CancellationToken cancellationToken)
    {
        switch (replEvent)
        {
            case ReplEvalConsoleEvent { Stream: "stdout" } stdout:
                AddText("stdout", stdout.Text);
                break;
            case ReplEvalConsoleEvent { Stream: "stderr" } stderr:
                AddText("stderr", stderr.Text);
                await PresentStderrAsync(stderr.Text, cancellationToken).ConfigureAwait(false);
                break;
            case ReplEvalDisplayEvent display:
                await PresentDisplayAsync(display, cancellationToken).ConfigureAwait(false);
                break;
            case ReplEvalClearOutputEvent clear:
                if (TryReservePresentationEvent())
                {
                    await presentation.ClearOutputAsync(clear.Wait, cancellationToken).ConfigureAwait(false);
                    clearCount++;
                }
                break;
            case ReplEvalInputRequestEvent input:
                var value = await presentation.RequestInputAsync(
                    input.Prompt,
                    input.Password,
                    cancellationToken).ConfigureAwait(false);
                await connection.ReplyInputAsync(input, value, cancellationToken).ConfigureAwait(false);
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
            new DenoReplPresentationResult(displayCount, updateCount, clearCount, skippedCount),
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
        ReplEvalDisplayEvent display,
        CancellationToken cancellationToken)
    {
        var bundle = ToMimeBundle(display.Data);
        var metadata = ToDictionary(display.Metadata);
        if (!CanPresentBundle(bundle, metadata)) return;

        if (display.IsUpdate)
        {
            if (display.DisplayId is null || !displayIds.TryGetValue(display.DisplayId, out var displayId))
            {
                skippedCount++;
                return;
            }

            await presentation.UpdateDisplayAsync(displayId, bundle, metadata, cancellationToken).ConfigureAwait(false);
            updateCount++;
            return;
        }

        if (display.DisplayId is { } innerDisplayId)
        {
            if (!displayIds.TryGetValue(innerDisplayId, out var outerDisplayId))
            {
                outerDisplayId = new JupyterDisplayId(Guid.NewGuid().ToString("N"));
                displayIds.Add(innerDisplayId, outerDisplayId);
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

    private async ValueTask PresentStderrAsync(string text, CancellationToken cancellationToken)
    {
        if (!TryReservePresentationEvent()) return;
        var available = options.MaxPresentationTextBytes - presentationTextBytes;
        if (available <= 0)
        {
            skippedCount++;
            return;
        }

        var fullBytes = Encoding.UTF8.GetByteCount(text);
        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out _);
        presentationTextBytes += Encoding.UTF8.GetByteCount(selected);
        if (selected.Length > 0) await presentation.WriteStderrAsync(selected, cancellationToken).ConfigureAwait(false);
        if (fullBytes > available && !presentationTextTruncated)
        {
            presentationTextTruncated = true;
            skippedCount++;
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
            skippedCount++;
            return;
        }

        var selected = TruncateUtf8(value, available, out var selectedBytes);
        presentationTextBytes += selectedBytes;
        if (selectedBytes < Encoding.UTF8.GetByteCount(value)) skippedCount++;
        await presentation.PublishErrorAsync(name, selected, traceback, cancellationToken).ConfigureAwait(false);
    }

    private bool CanPresentBundle(MimeBundle bundle, IReadOnlyDictionary<string, JsonElement> metadata)
    {
        if (!TryReservePresentationEvent()) return false;
        var bytes = CountJsonBytes(bundle.Data) + CountJsonBytes(metadata);
        if (bytes <= options.MaxPresentationBundleBytes) return true;
        skippedCount++;
        return false;
    }

    private bool TryReservePresentationEvent()
    {
        if (presentationEvents < options.MaxPresentationEventsPerExecution)
        {
            presentationEvents++;
            return true;
        }

        skippedCount++;
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

    private static MimeBundle ToMimeBundle(JsonElement element)
    {
        return new MimeBundle(ToDictionary(element));
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
