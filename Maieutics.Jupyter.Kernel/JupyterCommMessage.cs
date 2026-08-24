using System.Text.Json;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

/// <summary>
///     A Jupyter comm message received on the shell channel. Comm carries frontend↔kernel widget
///     traffic; the message body is the JSON content parsed from the wire message plus the raw
///     binary buffers that traveled with it.
/// </summary>
/// <param name="Kind">The comm message kind: open, message, or close.</param>
/// <param name="CommId">The comm channel identifier.</param>
/// <param name="TargetName">The comm target name; present only on open.</param>
/// <param name="Data">The JSON data payload, or null when the message carried none.</param>
/// <param name="Buffers">The binary buffers that traveled with the wire message.</param>
/// <param name="WireMessage">The originating wire message, preserved for identity and routing.</param>
public sealed record JupyterCommMessage(
    JupyterCommKind Kind,
    string CommId,
    string? TargetName,
    JsonElement? Data,
    IReadOnlyList<byte[]> Buffers,
    JupyterWireMessage WireMessage);

public enum JupyterCommKind
{
    Open,
    Message,
    Close
}
