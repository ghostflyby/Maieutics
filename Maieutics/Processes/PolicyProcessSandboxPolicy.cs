using System.Runtime.InteropServices;
using System.Text;
using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>
///     The default <see cref="IProcessSandboxPolicy"/>: one <see cref="EffectivePolicy"/>
///     snapshot answers for the executable gate, the filesystem and network scope, and the
///     environment — the same single source the Deno renderer and the broker consume (ADR
///     0018 §12, ADR 0045). The platform renders that scope as a sandbox wrapper: seatbelt
///     (<c>sandbox-exec</c>) on macOS and bubblewrap (<c>bwrap</c>) on Linux, each carrying the
///     requested argv unchanged after the wrapper's own arguments. Without a launcher the mode
///     decides: <see cref="ProcessSandboxMode.Prefer"/> launches unwrapped behind the same run
///     gate and environment allowlist (loudly, with a warning flag on the plan);
///     <see cref="ProcessSandboxMode.Require"/> refuses.
/// </summary>
/// <remarks>
///     Known limits, deliberate and documented (ADR 0045): the seatbelt profile binds paths
///     exactly as the policy spells them (no symlink resolution — same verbatim stance as the
///     broker matcher); the network switch is coarse (any net grant enables networking; the
///     policy's host allowlists are not expressible in a launch-time profile); and the
///     per-executable classifier currently derives everything from the policy — the signature
///     keeps the executable and arguments so per-tool profiles can refine without touching the
///     seam.
/// </remarks>
internal sealed class PolicyProcessSandboxPolicy : IProcessSandboxPolicy
{
    private readonly ProcessSandboxMode _mode;
    private readonly Func<string?> _locateSeatbelt;
    private readonly Func<string?> _locateBubblewrap;

    internal PolicyProcessSandboxPolicy(
        ProcessSandboxMode mode,
        Func<string?>? locateSeatbelt = null,
        Func<string?>? locateBubblewrap = null)
    {
        this._mode = mode;
        this._locateSeatbelt = locateSeatbelt ?? LocateSeatbelt;
        this._locateBubblewrap = locateBubblewrap ?? LocateBubblewrap;
    }

    /// <summary>The mode the composition root binds from
    /// <c>Maieutics:ProcessSandbox:Mode</c>; exposed for the tool to describe its posture.</summary>
    internal ProcessSandboxMode Mode => _mode;

    public ProcessLaunchPlan Plan(EffectivePolicy policy, string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument.Contains('\0')))
            throw new ArgumentException("An argument contains a NUL character.", nameof(arguments));

        // The executable gate is the shared general-start rule: the session's run rules,
        // deny-wins, judged with the same boundary matcher the broker and the terminal use.
        if (!RunGrant.Allows(policy, executable))
            return ProcessLaunchPlan.Denied(
                $"The executable '{executable}' is not permitted by the session's run policy.");

        var environment = ProcessEnvironment.Capture(policy);
        if (_mode == ProcessSandboxMode.Off)
            return ProcessLaunchPlan.Launch([executable, .. arguments], environment, sandboxed: false);

        // The classifier's hook: today the profile is policy-derived; the executable and
        // arguments stay in the signature so per-tool refinement needs no seam change.
        var profile = Classify(policy, executable, arguments);

        var launcher = OperatingSystem.IsMacOS() ? _locateSeatbelt() :
            OperatingSystem.IsLinux() ? _locateBubblewrap() : null;
        if (launcher is null)
        {
            return _mode == ProcessSandboxMode.Require
                ? ProcessLaunchPlan.Denied(
                    "No process sandbox launcher is available on this platform " +
                    "(sandbox-exec on macOS, bubblewrap on Linux) and the sandbox mode requires one.")
                : ProcessLaunchPlan.Launch([executable, .. arguments], environment, sandboxed: false);
        }

        var argv = OperatingSystem.IsMacOS()
            ? RenderSeatbelt(launcher, profile, executable, arguments)
            : RenderBubblewrap(launcher, profile, executable, arguments);
        return ProcessLaunchPlan.Launch(argv, environment, sandboxed: true);
    }

    /// <summary>The sandbox scope for one spawn, derived from the session's own policy: the
    /// child sees exactly the filesystem the session's read/write rules describe — the writable
    /// set is the write allows plus the shared temporary directory, minus every write denial
    /// (deny-wins, invariant 20) — and networking only when the session's net rules grant it.</summary>
    internal static SandboxProfile Classify(
        EffectivePolicy policy,
        string executable,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var read = policy.For(PermissionKind.Read);
        var write = policy.For(PermissionKind.Write);
        var net = policy.For(PermissionKind.Net);

        var writable = new List<string>(write.Allow.Count + 1);
        if (!write.DenyAll)
        {
            writable.AddRange(write.Allow);
            writable.Add(Path.GetTempPath());
            writable.RemoveAll(path =>
                PermissionMatching.MatchesAny(PermissionKind.Write, write.Deny, path));
        }

        return new SandboxProfile(
            ReadOnlyPaths: [.. read.Allow, .. SystemReadOnlyPaths()],
            WritablePaths: writable,
            NetworkAllowed: net.AllowAll || net.Allow.Count > 0);
    }

    /// <summary>The read-only basics every spawned tool needs on its platform: the system
    /// binaries and libraries, and the Deno cache so interpreters spawned from a cell can
    /// resolve their module graph.</summary>
    private static IEnumerable<string> SystemReadOnlyPaths()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/bin";
            yield return "/usr/bin";
            yield return "/usr/lib";
            yield return "/System/Library";
            yield return "/private/etc";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/bin";
            yield return "/usr/bin";
            yield return "/usr/lib";
            yield return "/etc";
        }

        var denoDir = Environment.GetEnvironmentVariable("DENO_DIR");
        if (denoDir is { Length: > 0 } && Path.IsPathRooted(denoDir)) yield return denoDir;
    }

    /// <summary>seatbelt invocation: the profile is a single <c>-p</c> argument, so the
    /// requested argv rides positionally after <c>--</c> with no quoting ambiguity.</summary>
    internal static IReadOnlyList<string> RenderSeatbelt(
        string launcher,
        SandboxProfile profile,
        string executable,
        IReadOnlyList<string> arguments)
    {
        return [launcher, "-p", BuildSeatbeltProfile(profile), "--", executable, .. arguments];
    }

    /// <summary>bubblewrap invocation: read-only and writable binds, then network unsharing
    /// unless the policy grants networking.</summary>
    internal static IReadOnlyList<string> RenderBubblewrap(
        string launcher,
        SandboxProfile profile,
        string executable,
        IReadOnlyList<string> arguments)
    {
        var argv = new List<string> { launcher };
        foreach (var path in profile.ReadOnlyPaths) argv.AddRange(["--ro-bind", path, path]);
        foreach (var path in profile.WritablePaths) argv.AddRange(["--bind", path, path]);
        argv.AddRange(["--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp"]);
        if (!profile.NetworkAllowed) argv.Add("--unshare-net");
        argv.Add("--");
        argv.Add(executable);
        argv.AddRange(arguments);
        return argv;
    }

    /// <summary>The seatbelt profile for one spawn scope: every path walk stats the root and
    /// every ancestor of a granted path (metadata-only, so ancestors never widen content
    /// reads); the granted paths and the system basics are read-only; the writable set carries
    /// both read and write (a tool that writes a file must be able to read it back); and the
    /// network is fully denied unless the policy grants it — everything else denies by default
    /// inside the profile.</summary>
    internal static string BuildSeatbeltProfile(SandboxProfile profile)
    {
        var builder = new StringBuilder(
            "(version 1)(allow file-read* (literal \"/\"))" +
            "(allow file-read* (subpath \"/dev\") (literal \"/dev\"))" +
            "(allow file-write-data (literal \"/dev/null\"))" +
            "(allow file-read* (subpath \"/private/var/select\") (literal \"/private/var/select\"))");
        foreach (var path in profile.ReadOnlyPaths.Distinct(StringComparer.Ordinal))
            AppendPathGrant(builder, path, writable: false);
        foreach (var path in profile.WritablePaths.Distinct(StringComparer.Ordinal))
            AppendPathGrant(builder, path, writable: true);
        builder.Append(profile.NetworkAllowed
            ? "(allow network*)"
            : "(deny network*)");
        builder.Append("(allow process*)(allow mach-lookup)");
        return builder.ToString();
    }

    /// <summary>One granted path: metadata on every ancestor (path traversal), the path's own
    /// read grant, and — for writable paths — the write grant beside it. The path is resolved
    /// to its realpath first: the OS path walk resolves symlinks (on macOS <c>/var</c> is
    /// <c>/private/var</c>), and seatbelt matches literal strings — a grant phrased through a
    /// symlink never matches the resolved path the walk uses. This is deliberately the
    /// opposite of the broker matcher's verbatim stance (ADR 0045: the broker judges the
    /// request as written; the sandbox confines the file as resolved).</summary>
    private static void AppendPathGrant(StringBuilder builder, string path, bool writable)
    {
        var chain = ResolvePathChain(path);
        foreach (var segment in chain)
            builder.Append("(allow file-read-metadata (literal \"").Append(segment).Append("\"))");
        var resolved = chain[^1];
        builder.Append("(allow file-read* (subpath \"").Append(resolved).Append("\") (literal \"").Append(resolved).Append("\"))");
        if (writable)
            builder.Append("(allow file-write* (subpath \"").Append(resolved).Append("\") (literal \"").Append(resolved).Append("\"))");
    }

    /// <summary>The realpath chain of a granted path: every ancestor followed by the resolved
    /// path itself, all symlink-free. Falls back to the lexical chain when resolution is not
    /// available (the path does not exist yet, or a non-unix platform).</summary>
    private static IReadOnlyList<string> ResolvePathChain(string path)
    {
        var resolved = ResolveRealPath(path) ?? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var chain = new List<string>();
        var current = resolved;
        while (current.Length > 1)
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        chain.Reverse();
        return chain;
    }

    private static string? ResolveRealPath(string path)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return null;
        try
        {
            // The caller-owned buffer keeps realpath from allocating: the marshaled byte[] is
            // pinned for the call and the answer is copied out of it.
            var buffer = new byte[4096];
            var resolved = RealPath(path, buffer);
            if (resolved == IntPtr.Zero) return null;
            var terminator = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
    private static extern IntPtr RealPath(string path, byte[] resolved);

    private static string? LocateOnPath(string fileName, params string[] fixedPaths)
    {
        foreach (var candidate in fixedPaths)
            if (File.Exists(candidate))
                return candidate;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? LocateSeatbelt()
    {
        return LocateOnPath("sandbox-exec", "/usr/bin/sandbox-exec", "/usr/sbin/sandbox-exec");
    }

    private static string? LocateBubblewrap()
    {
        return LocateOnPath("bwrap", "/usr/bin/bwrap");
    }

    /// <summary>The filesystem and network scope one sandbox enforces; the platform renderers
    /// are the only consumers.</summary>
    internal sealed record SandboxProfile(
        IReadOnlyList<string> ReadOnlyPaths,
        IReadOnlyList<string> WritablePaths,
        bool NetworkAllowed);
}
