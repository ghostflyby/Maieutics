namespace Maieutics.Commands;

/// <summary>Represents an expected Maieutics command failure with a stable code that
/// frontends surface verbatim.</summary>
internal sealed class MaieuticsCommandException(string code, string message) : Exception(message)
{
    /// <summary>Gets the stable machine-readable failure code.</summary>
    public string Code { get; } = code;

    /// <summary>The code used for unknown commands and invalid arguments.</summary>
    internal const string CommandError = "command_error";

    /// <summary>The code used when the command's owning subsystem is unavailable.</summary>
    internal const string Unavailable = "command_unavailable";
}
