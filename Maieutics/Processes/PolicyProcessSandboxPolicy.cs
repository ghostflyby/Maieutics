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
///     gate and environment allowlist; <see cref="ProcessSandboxMode.Require"/> refuses.
/// </summary>
/// <remarks>
///     The executable gate is deliberately the broker's posture, not the launch gate's: a
///     cell-chosen executable is model-controlled code, so with no <c>run</c> rules the spawn
///     is refused — identical to the child's own native Deno path (ADR 0045 decision 2a). The
///     network switch is coarse and fail-closed: only <c>AllowAll</c>, or allows with no
///     denial at all, turn networking on. Per-spawn grants whose realpath escapes the spelling
///     they were granted by are dropped — a symlink planted on a granted path cannot steer the
///     grant to its target (ADR 0045 decision 5a). Known limits, declared in the ADR: the
///     per-executable classifier currently derives everything from the policy — the signature
///     keeps the executable and arguments so per-tool profiles can refine without touching the
///     seam.
/// </remarks>
internal sealed class PolicyProcessSandboxPolicy : IProcessSandboxPolicy
{
    private readonly ProcessSandboxMode _mode;
    private readonly Func<string?> _locateSeatbelt;
    private readonly Func<string?> _locateBubblewrap;
    private readonly Func<string> _createTemporaryDirectory;
    private readonly string? _trustedWorkspaceRoot;

    internal PolicyProcessSandboxPolicy(
        ProcessSandboxMode mode,
        Func<string?>? locateSeatbelt = null,
        Func<string?>? locateBubblewrap = null,
        Func<string>? createTemporaryDirectory = null,
        string? trustedWorkspaceRoot = null)
    {
        this._mode = mode;
        this._locateSeatbelt = locateSeatbelt ?? LocateSeatbelt;
        this._locateBubblewrap = locateBubblewrap ?? LocateBubblewrap;
        this._createTemporaryDirectory = createTemporaryDirectory ??
            (static () => Directory.CreateTempSubdirectory("maieutics-spawn-").FullName);
        this._trustedWorkspaceRoot = trustedWorkspaceRoot;
    }

    /// <summary>The mode the composition root binds from
    /// <c>Maieutics:ProcessSandbox:Mode</c>; exposed for the tool to describe its posture.</summary>
    internal ProcessSandboxMode Mode => _mode;

    public ProcessLaunchPlan Plan(EffectivePolicy policy, string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        // The executable gate is the broker's posture (deny-by-default): the executable is
        // chosen by cell code, and the run kind's grants — not its unrestricted launch-gate
        // default — decide what runs. Denials win, then an explicit grant or AllowAll admits.
        var run = policy.For(PermissionKind.Run);
        if (run.DenyAll ||
            PermissionMatching.MatchesAny(PermissionKind.Run, run.Deny, executable))
            return ProcessLaunchPlan.Denied(
                $"The executable '{executable}' is not permitted by the session's run policy.");
        if (!run.AllowAll &&
            !PermissionMatching.MatchesAny(PermissionKind.Run, run.Allow, executable))
            return ProcessLaunchPlan.Denied(
                $"The executable '{executable}' is not granted by the session's run policy; " +
                "the spawn surface denies by default (grant Run allows to admit executables).");

        var environment = ProcessEnvironment.Capture(policy);
        if (_mode == ProcessSandboxMode.Off)
            return ProcessLaunchPlan.Launch([executable, .. arguments], environment, sandboxed: false, null);

        // The per-spawn temporary directory: kernel-created, kernel-owned, deleted by the
        // tool when the child settles — the shared system temp stays read-only, so a sandboxed
        // child cannot squat another process's temp paths.
        var temporaryDirectory = _createTemporaryDirectory();
        var profile = Classify(policy, executable, arguments, temporaryDirectory, _trustedWorkspaceRoot);

        var launcher = OperatingSystem.IsMacOS() ? _locateSeatbelt() :
            OperatingSystem.IsLinux() ? _locateBubblewrap() : null;
        if (launcher is null)
        {
            return _mode == ProcessSandboxMode.Require
                ? WithAbandonedTemporaryDirectory(
                    temporaryDirectory,
                    ProcessLaunchPlan.Denied(
                        "No process sandbox launcher is available on this platform " +
                        "(sandbox-exec on macOS, bubblewrap on Linux) and the sandbox mode requires one."))
                : WithAbandonedTemporaryDirectory(
                    temporaryDirectory,
                    ProcessLaunchPlan.Launch([executable, .. arguments], environment, sandboxed: false, null));
        }

        var argv = OperatingSystem.IsMacOS()
            ? RenderSeatbelt(launcher, profile, executable, arguments)
            : RenderBubblewrap(launcher, profile, executable, arguments);
        return ProcessLaunchPlan.Launch(argv, environment, sandboxed: true, temporaryDirectory);
    }

    /// <summary>Drops a per-spawn temporary directory the plan ended up not using: the
    /// directory's owner is the plan, and a refused or unwrapped launch must not leak it.</summary>
    private static ProcessLaunchPlan WithAbandonedTemporaryDirectory(
        string? temporaryDirectory,
        ProcessLaunchPlan plan)
    {
        if (temporaryDirectory is not null)
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }

        return plan;
    }

    /// <summary>The sandbox scope for one spawn, derived from the session's own policy: the
    /// child sees exactly the filesystem the session's read/write rules describe — the write
    /// allows plus a fresh per-spawn temporary directory (the shared system temp stays
    /// read-only), every read and write denial carried as an explicit sandbox deny rule
    /// (deny-wins, invariant 20) — and networking only when the net rules carry no denial.</summary>
    internal static SandboxProfile Classify(
        EffectivePolicy policy,
        string executable,
        IReadOnlyList<string> arguments,
        string? sandboxedTemporaryDirectory = null,
        string? trustedWorkspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var read = policy.For(PermissionKind.Read);
        var write = policy.For(PermissionKind.Write);
        var net = policy.For(PermissionKind.Net);

        var writable = new List<string>(write.Allow.Count + 1);
        var system = new List<string>(SystemReadOnlyPaths());
        if (!write.DenyAll && sandboxedTemporaryDirectory is not null)
        {
            writable.AddRange(write.Allow);
            writable.Add(sandboxedTemporaryDirectory);
            system.Add(sandboxedTemporaryDirectory);
        }

        // The kernel-fixed workspace root is trusted: its spelling is chosen by the kernel
        // (not by anything a cell can plant), so it resolves without the steering guard —
        // which exists for policy-derived paths whose trees the cell can populate.
        if (trustedWorkspaceRoot is not null)
            system.Add(trustedWorkspaceRoot);

        // Fail-closed network switch: only an unqualified grant turns the network on; any
        // denial at all (host allowlists are not expressible in a launch profile) keeps it off.
        var networkAllowed = net.AllowAll && !net.DenyAll && net.Deny.Count == 0;

        return new SandboxProfile(
            ReadOnlyPaths: [.. read.Allow, .. system],
            WritablePaths: writable,
            DeniedPaths: [.. read.Deny, .. write.Deny],
            TrustedPaths: system,
            NetworkAllowed: networkAllowed);
    }

    /// <summary>The read-only basics every spawned tool needs on its platform: the system
    /// binaries, libraries, and the common toolchain install prefixes (Homebrew, /usr/local)
    /// — a sandbox that cannot exec the machine's git is not a usable default — plus the Deno
    /// cache so interpreters spawned from a cell resolve their module graph.</summary>
    private static IEnumerable<string> SystemReadOnlyPaths()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/bin";
            yield return "/usr/bin";
            yield return "/usr/lib";
            yield return "/System/Library";
            yield return "/private/etc";
            yield return "/opt/homebrew";
            yield return "/usr/local";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/bin";
            yield return "/usr/bin";
            yield return "/usr/lib";
            yield return "/usr/local";
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

    /// <summary>bubblewrap invocation: read-only and writable binds, deny overlays for the
    /// policy's denials, then network unsharing unless the policy grants networking.</summary>
    internal static IReadOnlyList<string> RenderBubblewrap(
        string launcher,
        SandboxProfile profile,
        string executable,
        IReadOnlyList<string> arguments)
    {
        var argv = new List<string> { launcher };
        foreach (var path in profile.ReadOnlyPaths) argv.AddRange(["--ro-bind", path, path]);
        foreach (var path in profile.WritablePaths) argv.AddRange(["--bind", path, path]);
        foreach (var path in profile.DeniedPaths) argv.AddRange(["--ro-bind-try", path, "/dev/null"]);
        argv.AddRange(["--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp"]);
        if (!profile.NetworkAllowed) argv.Add("--unshare-net");
        argv.Add("--");
        argv.Add(executable);
        argv.AddRange(arguments);
        return argv;
    }

    /// <summary>The seatbelt profile for one spawn scope: every path walk stats the root, so
    /// the root directory is always readable; the granted paths and the system basics are
    /// read-only; the writable set carries both read and write (a tool that writes a file must
    /// be able to read it back); the policy's read/write denials render as explicit deny rules
    /// (SBPL deny takes precedence over allow); and the network is fully denied unless the
    /// policy grants it. Process operations are exec and fork only — no <c>process-info*</c>
    /// (other processes' metadata stays private), and no mach-lookup (verified: shell, exec,
    /// and pipe I/O run without it).</summary>
    internal static string BuildSeatbeltProfile(SandboxProfile profile)
    {
        var trusted = new HashSet<string>(profile.TrustedPaths, StringComparer.Ordinal);
        var builder = new StringBuilder(
            "(version 1)(allow file-read* (literal \"/\"))" +
            "(allow file-read* (subpath \"/dev\") (literal \"/dev\"))" +
            "(allow file-write-data (literal \"/dev/null\"))" +
            "(allow file-read* (subpath \"/private/var/select\") (literal \"/private/var/select\"))");
        foreach (var path in profile.ReadOnlyPaths.Distinct(StringComparer.Ordinal))
            AppendPathGrant(builder, path, writable: false, trusted.Contains(path));
        foreach (var path in profile.WritablePaths.Distinct(StringComparer.Ordinal))
            AppendPathGrant(builder, path, writable: true, trusted.Contains(path));
        builder.Append(profile.NetworkAllowed
            ? "(allow network*)"
            : "(deny network*)");
        builder.Append("(allow process-exec*)(allow process-fork)");
        foreach (var path in profile.DeniedPaths.Distinct(StringComparer.Ordinal))
        {
            // Denials resolve without the steering guard (widening a deny is the safe
            // direction) and render in both spellings, so a write phrased through a symlink
            // cannot slip past a deny phrased resolved — or the reverse.
            var lexical = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var resolved = ResolveRealPath(path) ?? lexical;
            foreach (var spelling in new[] { lexical, resolved }.Distinct(StringComparer.Ordinal))
            {
                var literal = EscapeProfileLiteral(spelling);
                builder.Append("(deny file-read* (subpath \"").Append(literal).Append("\") (literal \"")
                    .Append(literal).Append("\"))");
                builder.Append("(deny file-write* (subpath \"").Append(literal).Append("\") (literal \"")
                    .Append(literal).Append("\"))");
            }
        }

        return builder.ToString();
    }

    /// <summary>One granted path, in both of its spellings: the lexical path as the process
    /// writes it and the realpath the OS walk resolves to (seatbelt's write matching follows
    /// the process's own spelling — a write phrased through a symlink is denied by a grant
    /// phrased resolved-only, and vice versa). Each spelling carries metadata on every
    /// ancestor, the read grant, and — for writable paths — the write grant beside it. A
    /// policy-derived grant whose realpath escapes the spelling it was granted by is dropped:
    /// a symlink planted on the granted path cannot steer the grant to its target.
    /// Kernel-owned paths (<paramref name="trusted" />) resolve without the guard.</summary>
    private static void AppendPathGrant(StringBuilder builder, string path, bool writable, bool trusted)
    {
        var lexical = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var resolved = ResolveRealPath(path) ?? lexical;
        // The steering guard: a policy-derived grant may only resolve to itself or deeper
        // inside its own spelling. Anything else means a symlink on the path redirects the
        // grant elsewhere, and the grant is refused rather than widened.
        if (!trusted &&
            !string.Equals(resolved, lexical, StringComparison.Ordinal) &&
            !resolved.StartsWith(lexical + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;

        foreach (var spelling in new[] { lexical, resolved }.Distinct(StringComparer.Ordinal))
            AppendSpellingGrant(builder, spelling, writable);
    }

    private static void AppendSpellingGrant(StringBuilder builder, string path, bool writable)
    {
        foreach (var ancestor in Ancestors(path))
            builder.Append("(allow file-read-metadata (literal \"").Append(EscapeProfileLiteral(ancestor)).Append("\"))");
        var literal = EscapeProfileLiteral(path);
        builder.Append("(allow file-read* (subpath \"").Append(literal).Append("\") (literal \"").Append(literal).Append("\"))");
        if (writable)
            builder.Append("(allow file-write* (subpath \"").Append(literal).Append("\") (literal \"").Append(literal).Append("\"))");
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var current = path;
        while (current.Length > 1)
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) yield break;
            yield return parent;
            current = parent;
        }
    }

    /// <summary>Escapes a path for use inside an SBPL string literal.</summary>
    private static string EscapeProfileLiteral(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
    /// are the only consumers. <see cref="TrustedPaths" /> names the kernel-owned entries
    /// (the system basics and the per-spawn temporary directory): they resolve without the
    /// steering guard, which exists for policy-derived paths whose trees the cell can
    /// populate.</summary>
    internal sealed record SandboxProfile(
        IReadOnlyList<string> ReadOnlyPaths,
        IReadOnlyList<string> WritablePaths,
        IReadOnlyList<string> DeniedPaths,
        IReadOnlyList<string> TrustedPaths,
        bool NetworkAllowed);
}
