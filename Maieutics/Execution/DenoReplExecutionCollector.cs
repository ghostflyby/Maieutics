using System.Text;
using System.Text.Json;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Execution;

internal sealed class DenoReplExecutionCollector
{
    private const int ModelItemOverheadBytes = 64;
    private readonly Func<JupyterDisplayId, JupyterDisplayId> getOrCreateDisplayId;
    private readonly Func<JupyterDisplayId, JupyterDisplayId?> getDisplayId;
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
        Func<JupyterDisplayId, JupyterDisplayId> getOrCreateDisplayId,
        Func<JupyterDisplayId, JupyterDisplayId?> getDisplayId)
    {
        this.sessionId = sessionId;
        this.generation = generation;
        this.options = options;
        this.presentation = presentation;
        this.getOrCreateDisplayId = getOrCreateDisplayId;
        this.getDisplayId = getDisplayId;
    }

    internal async Task<DenoReplExecutionResult> ConsumeAsync(
        IJupyterExecution execution,
        CancellationToken inputCancellationToken)
    {
        await foreach (var output in execution.Outputs.ConfigureAwait(false).WithCancellation(inputCancellationToken))
        {
            await ObserveAsync(execution, output, inputCancellationToken).ConfigureAwait(false);
        }

        var completion = await execution.Completion.ConfigureAwait(false);
        if (string.Equals(completion.Reply.Status, "error", StringComparison.Ordinal) &&
            outputs.All(static output => output.Kind != "error"))
        {
            AddError(
                completion.Reply.ErrorName ?? "DenoExecutionError",
                completion.Reply.ErrorValue ?? "Deno execution failed.",
                completion.Reply.Traceback ?? []);
        }

        return new DenoReplExecutionResult(
            sessionId,
            generation,
            completion.Reply.ExecutionCount,
            completion.Reply.Status,
            outputs,
            new DenoReplPresentationResult(displayCount, updateCount, clearCount, skippedCount),
            truncated,
            omittedBytes);
    }

    private async ValueTask ObserveAsync(
        IJupyterExecution execution,
        JupyterOutput output,
        CancellationToken inputCancellationToken)
    {
        switch (output)
        {
            case JupyterStdout stdout:
                AddText("stdout", stdout.Text);
                break;
            case JupyterStderr stderr:
                AddText("stderr", stderr.Text);
                await PresentStderrAsync(stderr.Text, CancellationToken.None).ConfigureAwait(false);
                break;
            case JupyterExecuteResultOutput result:
                AddResult(result.Data);
                break;
            case JupyterExecutionError error:
                AddError(error.Name, error.Value, error.Traceback);
                await PresentErrorAsync(error, CancellationToken.None).ConfigureAwait(false);

                break;
            case JupyterDisplayOutput display:
                await PresentDisplayAsync(display, CancellationToken.None).ConfigureAwait(false);
                break;
            case JupyterDisplayUpdateOutput update:
                await PresentUpdateAsync(update, CancellationToken.None).ConfigureAwait(false);
                break;
            case JupyterMalformedOutput { MessageType: "update_display_data" }:
                skippedCount++;
                break;
            case JupyterClearOutput clear:
                if (TryReservePresentationEvent())
                {
                    await presentation.ClearOutputAsync(clear.Wait, CancellationToken.None).ConfigureAwait(false);
                    clearCount++;
                }

                break;
            case JupyterInputRequest input:
                try
                {
                    var value = await presentation.RequestInputAsync(
                        input.Prompt,
                        input.Password,
                        inputCancellationToken).ConfigureAwait(false);
                    await execution.ReplyInputAsync(input, value, inputCancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (inputCancellationToken.IsCancellationRequested)
                {
                    // The owning execution sends an explicit interrupt and continues draining this output stream.
                }

                break;
        }
    }

    private async ValueTask PresentDisplayAsync(
        JupyterDisplayOutput display,
        CancellationToken cancellationToken)
    {
        if (!CanPresentBundle(display.Data, display.Metadata))
        {
            return;
        }

        if (display.DisplayId is { } innerDisplayId)
        {
            var outerDisplayId = getOrCreateDisplayId(innerDisplayId);
            await presentation.DisplayTrackedAsync(
                display.Data,
                outerDisplayId,
                display.Metadata,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await presentation.DisplayAsync(display.Data, display.Metadata, cancellationToken).ConfigureAwait(false);
        }

        displayCount++;
    }

    private async ValueTask PresentUpdateAsync(
        JupyterDisplayUpdateOutput update,
        CancellationToken cancellationToken)
    {
        if (!CanPresentBundle(update.Data, update.Metadata))
        {
            return;
        }

        var displayId = getDisplayId(update.DisplayId);
        if (displayId is null)
        {
            skippedCount++;
            return;
        }

        await presentation.UpdateDisplayAsync(
            displayId.Value,
            update.Data,
            update.Metadata,
            cancellationToken).ConfigureAwait(false);
        updateCount++;
    }

    private async ValueTask PresentStderrAsync(string text, CancellationToken cancellationToken)
    {
        if (!TryReservePresentationEvent())
        {
            return;
        }

        var available = options.MaxPresentationTextBytes - presentationTextBytes;
        if (available <= 0)
        {
            skippedCount++;
            return;
        }

        var fullBytes = Encoding.UTF8.GetByteCount(text);
        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out _);
        presentationTextBytes += Encoding.UTF8.GetByteCount(selected);
        if (selected.Length > 0)
        {
            await presentation.WriteStderrAsync(selected, cancellationToken).ConfigureAwait(false);
        }

        if (fullBytes > available && !presentationTextTruncated)
        {
            presentationTextTruncated = true;
            skippedCount++;
        }
    }

    private async ValueTask PresentErrorAsync(
        JupyterExecutionError error,
        CancellationToken cancellationToken)
    {
        if (!TryReservePresentationEvent())
        {
            return;
        }

        var available = options.MaxPresentationTextBytes - presentationTextBytes;
        if (available <= 0)
        {
            skippedCount++;
            return;
        }

        var value = TruncateUtf8(error.Value, available, out var valueBytes);
        available -= valueBytes;
        var traceback = new List<string>();
        var fullTracebackBytes = 0;
        var selectedTracebackBytes = 0;
        foreach (var line in error.Traceback)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            fullTracebackBytes = checked(fullTracebackBytes + lineBytes);
            if (available <= 0)
            {
                continue;
            }

            var selected = lineBytes <= available
                ? line
                : TruncateUtf8(line, available, out lineBytes);
            traceback.Add(selected);
            selectedTracebackBytes += lineBytes;
            available -= lineBytes;
        }

        presentationTextBytes += valueBytes + selectedTracebackBytes;
        if (valueBytes < Encoding.UTF8.GetByteCount(error.Value) || selectedTracebackBytes < fullTracebackBytes)
        {
            skippedCount++;
        }

        await presentation.PublishErrorAsync(
            error.Name,
            value,
            traceback,
            cancellationToken).ConfigureAwait(false);
    }

    private bool CanPresentBundle(
        MimeBundle bundle,
        IReadOnlyDictionary<string, JsonElement> metadata)
    {
        if (!TryReservePresentationEvent())
        {
            return false;
        }

        var bytes = CountJsonBytes(bundle.Data) + CountJsonBytes(metadata);
        if (bytes <= options.MaxPresentationBundleBytes)
        {
            return true;
        }

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

        var selectedBytes = fullBytes;
        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out selectedBytes);
        if (fullBytes <= available)
        {
            selectedBytes = fullBytes;
        }

        outputs.Add(new DenoReplOutputItem(kind, Text: selected));
        modelBytes += ModelItemOverheadBytes + selectedBytes;
        if (selectedBytes < fullBytes)
        {
            MarkOmitted(fullBytes - selectedBytes);
        }
    }

    private void AddResult(MimeBundle bundle)
    {
        if (bundle.Data.TryGetValue("application/json", out var json))
        {
            var bytes = Encoding.UTF8.GetByteCount(json.GetRawText());
            if (TryReserveModelBytes(bytes))
            {
                outputs.Add(new DenoReplOutputItem(
                    "result",
                    MediaType: "application/json",
                    Value: json.Clone()));
                return;
            }
        }

        if (bundle.Data.TryGetValue("text/plain", out var text))
        {
            AddTextResult(text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : text.GetRawText());
            return;
        }

        var mediaTypes = bundle.Data.Keys.Order(StringComparer.Ordinal).ToArray();
        var mediaBytes = mediaTypes.Sum(Encoding.UTF8.GetByteCount);
        if (TryReserveModelBytes(mediaBytes))
        {
            outputs.Add(new DenoReplOutputItem("result", MediaTypes: mediaTypes));
        }
        else
        {
            MarkOmitted(mediaBytes);
        }
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

        var selectedBytes = fullBytes;
        var selected = fullBytes <= available ? text : TruncateUtf8(text, available, out selectedBytes);
        if (fullBytes <= available)
        {
            selectedBytes = fullBytes;
        }

        outputs.Add(new DenoReplOutputItem("result", Text: selected, MediaType: "text/plain"));
        modelBytes += ModelItemOverheadBytes + selectedBytes;
        if (selectedBytes < fullBytes)
        {
            MarkOmitted(fullBytes - selectedBytes);
        }
    }

    private void AddError(string name, string value, IReadOnlyList<string> traceback)
    {
        var bytes = Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value) +
                    traceback.Sum(Encoding.UTF8.GetByteCount);
        if (TryReserveModelBytes(bytes))
        {
            outputs.Add(new DenoReplOutputItem(
                "error",
                Text: value,
                Name: name,
                Traceback: traceback.ToArray()));
            return;
        }

        var available = Math.Max(0, options.MaxModelOutputBytes - modelBytes - ModelItemOverheadBytes);
        var selected = TruncateUtf8(value, available, out var selectedBytes);
        if (selected.Length > 0)
        {
            outputs.Add(new DenoReplOutputItem("error", Text: selected, Name: name));
            modelBytes += ModelItemOverheadBytes + selectedBytes;
        }

        MarkOmitted(Math.Max(0, bytes - selectedBytes));
    }

    private bool TryReserveModelBytes(int contentBytes)
    {
        if (contentBytes > options.MaxModelOutputBytes - modelBytes - ModelItemOverheadBytes)
        {
            return false;
        }

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
        {
            result = checked(result + Encoding.UTF8.GetByteCount(name) +
                             Encoding.UTF8.GetByteCount(value.GetRawText()));
        }

        return result;
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
            if (selectedBytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            selectedBytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        return value[..characters];
    }
}