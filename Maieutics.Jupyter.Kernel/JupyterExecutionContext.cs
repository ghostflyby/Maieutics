using System.Text.Json;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public sealed class JupyterExecutionContext
{
    private readonly Func<string, string, CancellationToken, ValueTask> writeStream;
    private readonly Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> display;

    private readonly Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask>
        publishResult;

    private readonly Func<string, bool, CancellationToken, Task<string>> requestInput;

    internal JupyterExecutionContext(
        JupyterMessageId requestId,
        int executionCount,
        Func<string, string, CancellationToken, ValueTask> writeStream,
        Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> display,
        Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> publishResult,
        Func<string, bool, CancellationToken, Task<string>> requestInput)
    {
        RequestId = requestId;
        ExecutionCount = executionCount;
        this.writeStream = writeStream;
        this.display = display;
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