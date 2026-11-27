using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Maieutics.Control;

/// <summary>
///     Maps bearer credentials to control identities (REPL session ids or plugin host ids) on
///     platforms where peer process identity is not available on the control channel (Windows
///     loopback TCP). A credential is issued once during bootstrap, stays valid for the identity
///     lifetime, and is removed when the identity goes away.
/// </summary>
internal sealed class ReplControlCredentialRegistry
{
    private readonly ConcurrentDictionary<string, Credential> _credentials = new(StringComparer.Ordinal);

    /// <summary>Issues a fresh random credential bound to the control identity.</summary>
    public string Issue(string identity, int processId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _credentials[token] = new Credential(identity, processId);
        return token;
    }

    /// <summary>Resolves a credential to its owning control identity.</summary>
    public bool TryResolve(string credential, out string identity) =>
        TryResolve(credential, out identity, out _);

    internal bool TryResolve(string credential, out string identity, out int processId)
    {
        if (_credentials.TryGetValue(credential, out var value))
        {
            identity = value.Identity;
            processId = value.ProcessId;
            return true;
        }

        identity = string.Empty;
        processId = 0;
        return false;
    }

    /// <summary>Revokes every credential issued to the control identity.</summary>
    public void Remove(string identity)
    {
        foreach (var pair in _credentials)
            if (string.Equals(pair.Value.Identity, identity, StringComparison.Ordinal))
                _credentials.TryRemove(pair.Key, out _);
    }
    private sealed record Credential(string Identity, int ProcessId);
}
