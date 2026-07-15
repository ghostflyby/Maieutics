using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public sealed class JupyterRequestException : Exception
{
    internal JupyterRequestException(
        string errorName,
        string errorValue,
        IReadOnlyList<string> traceback,
        JupyterMessage rawReply)
        : base($"Jupyter request '{rawReply.ParentHeader?.MessageType ?? "unknown"}' failed: {errorName}: {errorValue}")
    {
        ErrorName = errorName;
        ErrorValue = errorValue;
        Traceback = traceback;
        RawReply = rawReply;
    }

    public JupyterMessageId RequestId => RawReply.ParentHeader?.MessageId ?? default;

    public string RequestMessageType => RawReply.ParentHeader?.MessageType ?? string.Empty;

    public string ReplyMessageType => RawReply.MessageType;

    public string ErrorName { get; }

    public string ErrorValue { get; }

    public IReadOnlyList<string> Traceback { get; }

    public JupyterMessage RawReply { get; }
}