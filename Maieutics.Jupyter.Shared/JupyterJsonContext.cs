using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Jupyter.Shared;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JupyterMessageHeader))]
[JsonSerializable(typeof(JupyterEmptyContent))]
[JsonSerializable(typeof(JupyterKernelInfo))]
[JsonSerializable(typeof(JupyterLanguageInfo))]
[JsonSerializable(typeof(JupyterHelpLink))]
[JsonSerializable(typeof(JupyterExecuteRequest))]
[JsonSerializable(typeof(JupyterExecuteReply))]
[JsonSerializable(typeof(JupyterStatus))]
[JsonSerializable(typeof(JupyterStream))]
[JsonSerializable(typeof(JupyterDisplayData))]
[JsonSerializable(typeof(JupyterUpdateDisplayData))]
[JsonSerializable(typeof(JupyterClearOutputContent))]
[JsonSerializable(typeof(JupyterExecuteResultData))]
[JsonSerializable(typeof(JupyterError))]
[JsonSerializable(typeof(JupyterExecuteInput))]
[JsonSerializable(typeof(JupyterInputRequestContent))]
[JsonSerializable(typeof(JupyterInputReply))]
[JsonSerializable(typeof(JupyterInterruptReply))]
[JsonSerializable(typeof(JupyterShutdownRequest))]
[JsonSerializable(typeof(JupyterShutdownReply))]
[JsonSerializable(typeof(JupyterCompleteRequest))]
[JsonSerializable(typeof(JupyterCompleteReply))]
[JsonSerializable(typeof(JupyterInspectRequest))]
[JsonSerializable(typeof(JupyterInspectReply))]
[JsonSerializable(typeof(JupyterIsCompleteRequest))]
[JsonSerializable(typeof(JupyterIsCompleteReply))]
[JsonSerializable(typeof(JupyterIopubWelcome))]
[JsonSerializable(typeof(JupyterCommOpenContent))]
[JsonSerializable(typeof(JupyterCommMsgContent))]
[JsonSerializable(typeof(JupyterCommCloseContent))]
public partial class JupyterJsonContext : JsonSerializerContext;