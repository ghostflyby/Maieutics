using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Jupyter.Shared;

public sealed record JupyterEmptyContent;

public sealed record JupyterKernelInfo(
    [property: JsonPropertyName("protocol_version")]
    string ProtocolVersion,
    [property: JsonPropertyName("implementation")]
    string Implementation,
    [property: JsonPropertyName("implementation_version")]
    string ImplementationVersion,
    [property: JsonPropertyName("language_info")]
    JupyterLanguageInfo LanguageInfo,
    [property: JsonPropertyName("banner")] string Banner = "",
    [property: JsonPropertyName("help_links")]
    IReadOnlyList<JupyterHelpLink>? HelpLinks = null);

public sealed record JupyterLanguageInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")]
    string Version,
    [property: JsonPropertyName("mimetype")]
    string? MimeType = null,
    [property: JsonPropertyName("file_extension")]
    string? FileExtension = null,
    [property: JsonPropertyName("pygments_lexer")]
    string? PygmentsLexer = null,
    [property: JsonPropertyName("codemirror_mode")]
    JsonElement? CodeMirrorMode = null,
    [property: JsonPropertyName("nbconvert_exporter")]
    string? NbConvertExporter = null);

public sealed record JupyterHelpLink(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("url")] string Url);

public sealed record JupyterExecuteRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("silent")] bool Silent = false,
    [property: JsonPropertyName("store_history")]
    bool StoreHistory = true,
    [property: JsonPropertyName("user_expressions")]
    IReadOnlyDictionary<string, string>? UserExpressions = null,
    [property: JsonPropertyName("allow_stdin")]
    bool AllowStdin = false,
    [property: JsonPropertyName("stop_on_error")]
    bool StopOnError = true);

public sealed record JupyterExecuteReply(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("execution_count")]
    int? ExecutionCount = null,
    [property: JsonPropertyName("user_expressions")]
    JsonElement? UserExpressions = null,
    [property: JsonPropertyName("payload")]
    JsonElement? Payload = null,
    [property: JsonPropertyName("ename")] string? ErrorName = null,
    [property: JsonPropertyName("evalue")] string? ErrorValue = null,
    [property: JsonPropertyName("traceback")]
    IReadOnlyList<string>? Traceback = null);

public sealed record JupyterStatus(
    [property: JsonPropertyName("execution_state")]
    string ExecutionState);

public sealed record JupyterStream(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("text")] string Text);

public sealed record JupyterDisplayData(
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, JsonElement> Data,
    [property: JsonPropertyName("metadata")]
    IReadOnlyDictionary<string, JsonElement> Metadata,
    [property: JsonPropertyName("transient")]
    IReadOnlyDictionary<string, JsonElement>? Transient = null);

public sealed record JupyterExecuteResultData(
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, JsonElement> Data,
    [property: JsonPropertyName("metadata")]
    IReadOnlyDictionary<string, JsonElement> Metadata,
    [property: JsonPropertyName("execution_count")]
    int ExecutionCount);

public sealed record JupyterError(
    [property: JsonPropertyName("ename")] string Name,
    [property: JsonPropertyName("evalue")] string Value,
    [property: JsonPropertyName("traceback")]
    IReadOnlyList<string> Traceback);

public sealed record JupyterExecuteInput(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("execution_count")]
    int ExecutionCount);

public sealed record JupyterInputRequestContent(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("password")]
    bool Password);

public sealed record JupyterInputReply(
    [property: JsonPropertyName("value")] string Value);

public sealed record JupyterInterruptReply(
    [property: JsonPropertyName("status")] string Status);

public sealed record JupyterShutdownRequest(
    [property: JsonPropertyName("restart")]
    bool Restart);

public sealed record JupyterShutdownReply(
    [property: JsonPropertyName("restart")]
    bool Restart);

public sealed record JupyterIopubWelcome(
    [property: JsonPropertyName("subscription")]
    string Subscription);