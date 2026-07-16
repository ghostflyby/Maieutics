using System.Text.Json;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public sealed class JupyterExecutionContext
{
    private readonly Func<string, string, CancellationToken, ValueTask> writeStream;
    private readonly Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> display;

    private readonly Func<MimeBundle, JupyterDisplayId?, IReadOnlyDictionary<string, JsonElement>, CancellationToken,
        ValueTask<JupyterDisplayId>> displayTracked;

    private readonly Func<JupyterDisplayId, MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken,
        ValueTask> updateDisplay;

    private readonly Func<bool, CancellationToken, ValueTask> clearOutput;

    private readonly Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask>
        publishResult;

    private readonly Func<string, bool, CancellationToken, Task<string>> requestInput;

    internal JupyterExecutionContext(
        JupyterMessageId requestId,
        int executionCount,
        Func<string, string, CancellationToken, ValueTask> writeStream,
        Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> display,
        Func<MimeBundle, JupyterDisplayId?, IReadOnlyDictionary<string, JsonElement>, CancellationToken,
            ValueTask<JupyterDisplayId>> displayTracked,
        Func<JupyterDisplayId, MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask>
            updateDisplay,
        Func<bool, CancellationToken, ValueTask> clearOutput,
        Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> publishResult,
        Func<string, bool, CancellationToken, Task<string>> requestInput)
    {
        RequestId = requestId;
        ExecutionCount = executionCount;
        this.writeStream = writeStream;
        this.display = display;
        this.displayTracked = displayTracked;
        this.updateDisplay = updateDisplay;
        this.clearOutput = clearOutput;
        this.publishResult = publishResult;
        this.requestInput = requestInput;
    }

    public JupyterMessageId RequestId { get; }

    public int ExecutionCount { get; }

    public ValueTask WriteStdoutAsync(string text, CancellationToken cancellationToken = default) =>
        writeStream("stdout", text, cancellationToken);

    public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken = default) =>
        writeStream("stderr", text, cancellationToken);

    public ValueTask DisplayAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default) =>
        display(data, metadata ?? new Dictionary<string, JsonElement>(), cancellationToken);

    public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
        MimeBundle data,
        JupyterDisplayId? displayId = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default) =>
        displayTracked(data, displayId, metadata ?? new Dictionary<string, JsonElement>(), cancellationToken);

    public ValueTask UpdateDisplayAsync(
        JupyterDisplayId displayId,
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default) =>
        updateDisplay(displayId, data, metadata ?? new Dictionary<string, JsonElement>(), cancellationToken);

    public ValueTask ClearOutputAsync(
        bool wait = false,
        CancellationToken cancellationToken = default) =>
        clearOutput(wait, cancellationToken);

    public ValueTask PublishResultAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default) =>
        publishResult(data, metadata ?? new Dictionary<string, JsonElement>(), cancellationToken);

    public Task<string> RequestInputAsync(
        string prompt,
        bool password = false,
        CancellationToken cancellationToken = default) =>
        requestInput(prompt, password, cancellationToken);
}