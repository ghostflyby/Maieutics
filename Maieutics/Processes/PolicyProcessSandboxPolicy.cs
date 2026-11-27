using System.Runtime.InteropServices;
using System.Text;
using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>Applies run rules and derives the platform sandbox from the same effective policy.
/// A launch that needs sandbox bypass is returned as a pending approval, never a launch plan.</summary>
internal sealed partial class PolicyProcessSandboxPolicy : IProcessSandboxPolicy
{
    private readonly ProcessSandboxMode _mode;
    private readonly Func<string?> _locateSeatbelt;
    private readonly Func<string?> _locateBubblewrap;
    private readonly Func<string> _createTemporaryDirectory;
    private readonly MaieuticsProcessSandboxReadMode _readMode;
    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly IReadOnlyList<string> _optionalProtectedPaths;

    internal PolicyProcessSandboxPolicy(
        ProcessSandboxMode mode,
        Func<string?>? locateSeatbelt = null,
        Func<string?>? locateBubblewrap = null,
        Func<string>? createTemporaryDirectory = null,
        string? bubblewrapPath = null,
        MaieuticsProcessSandboxReadMode readMode = MaieuticsProcessSandboxReadMode.Allowlist,
        IReadOnlyList<string>? protectedPaths = null,
        IReadOnlyList<string>? optionalProtectedPaths = null)
    {
        _mode = mode;
        _readMode = readMode;
        _protectedPaths = protectedPaths?.Select(Path.GetFullPath).ToArray() ?? [];
        _optionalProtectedPaths = optionalProtectedPaths ?? [];
        _locateSeatbelt = locateSeatbelt ?? LocateSeatbelt;
        _locateBubblewrap = locateBubblewrap ?? (() => LocateBubblewrap(bubblewrapPath));
        _createTemporaryDirectory = createTemporaryDirectory ??
            (static () => Directory.CreateTempSubdirectory("maieutics-spawn-").FullName);
    }

    public ProcessLaunchPlan Plan(
        EffectivePolicy policy,
        string executable,
        IReadOnlyList<string> arguments,
        bool outsideSandbox = false,
        string? workingDirectory = null,
        bool hasTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!RunGrant.Allows(policy, executable))
            return ProcessLaunchPlan.Denied(
                $"The executable '{executable}' is not permitted by the session's run policy.");

        if (outsideSandbox && _mode == ProcessSandboxMode.Require)
            return ProcessLaunchPlan.Denied("The process sandbox is required by configuration.");

        var runDenials = policy.For(PermissionKind.Run).Deny;
        if (runDenials.Count != 0)
        {
            // A launcher can otherwise search PATH differently from the spelling checked
            // above. Launch the checked absolute path whenever an explicit denial applies.
            var environment = ProcessEnvironment.Capture(policy);
            var resolved = ProcessExecutable.Resolve(executable, workingDirectory, environment);
            if (resolved is null)
                return ProcessLaunchPlan.Denied(
                    $"The executable '{executable}' could not be verified against the session's run policy.");
            if (IsDeniedExecutable(runDenials, resolved, workingDirectory, environment))
                return ProcessLaunchPlan.Denied(
                    $"The executable '{executable}' is not permitted by the session's run policy.");
            executable = resolved;
        }

        if (_mode == ProcessSandboxMode.Off)
            return Unwrapped(policy, executable, arguments);

        if (outsideSandbox)
            return RequestBypass(policy, executable, arguments, workingDirectory,
                "The caller requested execution outside the process sandbox.");

        var launcher = OperatingSystem.IsMacOS() ? _locateSeatbelt() :
            OperatingSystem.IsLinux() ? _locateBubblewrap() : null;
        if (launcher is null)
        {
            const string reason = "No process sandbox launcher is available (sandbox-exec on macOS, bubblewrap on Linux).";
            return _mode == ProcessSandboxMode.Require
                ? ProcessLaunchPlan.Denied(reason)
                : RequestBypass(policy, executable, arguments, workingDirectory, reason);
        }

        if (policy.For(PermissionKind.Read).DenyAll)
            return ProcessLaunchPlan.Denied(
                "The process sandbox cannot launch a process when the policy denies all filesystem reads.");

        string? temporaryDirectory = null;
        try
        {
            temporaryDirectory = _createTemporaryDirectory();
            var profile = Classify(policy, executable, arguments, temporaryDirectory, workingDirectory,
                _readMode == MaieuticsProcessSandboxReadMode.HostRead, _protectedPaths, _optionalProtectedPaths);
            var argv = OperatingSystem.IsMacOS()
                ? RenderSeatbelt(launcher, profile, executable, arguments, hasTerminal)
                : RenderLinuxLaunch(launcher, profile, executable, arguments, temporaryDirectory);
            return ProcessLaunchPlan.Launch(
                argv, ProcessEnvironment.CaptureSandbox(policy, temporaryDirectory),
                sandboxed: true, temporaryDirectory);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ProcessLaunchPlan.DeleteTemporaryDirectory(temporaryDirectory);
            return ProcessLaunchPlan.Denied(exception.Message);
        }
    }

    private static ProcessLaunchPlan RequestBypass(
        EffectivePolicy policy, string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, string reason)
    {
        if (HasDenials(policy.For(PermissionKind.Read)) ||
            HasDenials(policy.For(PermissionKind.Write)) ||
            HasDenials(policy.For(PermissionKind.Net)))
            return ProcessLaunchPlan.Denied(
                "The process cannot run outside the sandbox because it would drop explicit access denials.");

        var environment = ProcessEnvironment.Capture(policy);
        var resolved = ProcessExecutable.Resolve(executable, workingDirectory, environment);
        if (resolved is null)
            return ProcessLaunchPlan.Denied($"The executable '{executable}' could not be resolved for approval.");

        if (IsDeniedExecutable(policy.For(PermissionKind.Run).Deny, resolved, workingDirectory, environment))
            return ProcessLaunchPlan.Denied(
                $"The executable '{resolved}' is not permitted by the session's run policy.");

        var launch = new ProcessLaunchPlan.LaunchPlan([resolved, .. arguments], environment, false, null);
        return new ProcessLaunchPlan.ApprovalRequiredPlan(reason, launch);
    }

    private static bool HasDenials(PermissionKindRules rules) => rules.DenyAll || rules.Deny.Count > 0;

    private static bool IsDeniedExecutable(
        IReadOnlyList<string> denials,
        string executable,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (denials.Count == 0) return false;
        if (PermissionMatching.MatchesAny(PermissionKind.Run, denials, executable)) return true;
        if (ProcessExecutable.ResolvePhysicalPath(executable) is not { } physical) return true;
        if (PermissionMatching.MatchesAny(PermissionKind.Run, denials, physical)) return true;

        foreach (var denial in denials)
        {
            var path = ProcessExecutable.IsBareName(denial)
                ? ProcessExecutable.Resolve(denial, workingDirectory, environment)
                : Path.IsPathFullyQualified(denial) ? denial : null;
            if (path is not null &&
                (PermissionMatching.MatchesPath(path, executable) ||
                 ProcessExecutable.ResolvePhysicalPath(path) is { } target && PermissionMatching.MatchesPath(target, physical)))
                return true;
        }

        return false;
    }

    private static ProcessLaunchPlan.LaunchPlan Unwrapped(
        EffectivePolicy policy, string executable, IReadOnlyList<string> arguments) =>
        new([executable, .. arguments], ProcessEnvironment.Capture(policy), false, null);

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
        string? trustedWorkspaceRoot = null,
        bool hostRead = false,
        IReadOnlyList<string>? protectedPaths = null,
        IReadOnlyList<string>? optionalProtectedPaths = null)
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

        hostRead |= read.AllowAll;
        return new SandboxProfile(
            ReadOnlyPaths: [.. read.Allow, .. system],
            WritablePaths: writable,
            ReadDeniedPaths: [.. read.Deny, .. protectedPaths ?? []],
            TrustedPaths: system,
            NetworkAllowed: networkAllowed,
            WriteDeniedPaths: write.Deny,
            HostRead: hostRead,
            OptionalProtectedPaths: hostRead ? optionalProtectedPaths : null);
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
            yield return "/usr/share/locale";
            yield return "/Library/Ruby";
            yield return "/Library/Developer/CommandLineTools";
            yield return "/Applications/Xcode.app/Contents";
            yield return "/var/select";
            // readlink can be checked against the link itself under /var's physical parent.
            yield return "/var/db/xcode_select_link";
            const string developerLink = "/private/var/db/xcode_select_link";
            yield return developerLink;
            // xcrun needs Info.plist and frameworks beside Contents/Developer.
            if (ResolveRealPath(developerLink) is { } developer &&
                developer.EndsWith(".app/Contents/Developer", StringComparison.Ordinal))
                yield return Path.GetDirectoryName(developer) ?? developer;
            yield return "/opt/homebrew";
            yield return "/usr/local";
        }
        else if (OperatingSystem.IsLinux())
        {
            // Merged-/usr systems expose /bin and /lib* through symlinks. Keep the
            // loader, libraries, binaries, and their data together in the read-only base.
            yield return "/usr";
            yield return "/bin";
            yield return "/sbin";
            yield return "/lib";
            yield return "/lib64";
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
        IReadOnlyList<string> arguments,
        bool hasTerminal = false)
    {
        return [launcher, "-p", BuildSeatbeltProfile(profile, hasTerminal), "--", executable, .. arguments];
    }

    /// <summary>The seatbelt profile for one spawn scope: every path walk stats the root, so
    /// the root directory is always readable; the granted paths and the system basics are
    /// read-only; the writable set carries both read and write (a tool that writes a file must
    /// be able to read it back); the policy's read/write denials render as explicit deny rules
    /// (SBPL deny takes precedence over allow); and the network is fully denied unless the
    /// policy grants it. TTY job-control operations are available to PTY launches.</summary>
    internal static string BuildSeatbeltProfile(SandboxProfile profile, bool hasTerminal = false)
    {
        var trusted = new HashSet<string>(profile.TrustedPaths, StringComparer.Ordinal);
        var builder = new StringBuilder(
            "(version 1)(allow file-read* (literal \"/\"))" +
            "(allow file-read* (subpath \"/dev\") (literal \"/dev\"))" +
            "(allow file-write-data (literal \"/dev/null\"))");
        builder.Append("(allow sysctl-read)");
        if (profile.HostRead) builder.Append("(allow file-read*)");
        // getpwuid uses the local account-information service, including in ssh -V.
        builder.Append("(allow mach-lookup (global-name \"com.apple.system.opendirectoryd.libinfo\"))");
        if (hasTerminal)
            builder.Append("(allow file-ioctl (literal \"/dev/tty\") (regex #\"^/dev/ttys[0-9]+$\"))");
        foreach (var path in profile.ReadOnlyPaths.Distinct(StringComparer.Ordinal))
            AppendPathGrant(builder, path, writable: false, trusted.Contains(path));
        foreach (var path in profile.WritablePaths.Distinct(StringComparer.Ordinal))
            AppendPathGrant(builder, path, writable: true, trusted.Contains(path));
        builder.Append(profile.NetworkAllowed
            ? "(allow network*)"
            : "(deny network*)");
        if (profile.HostRead && profile.NetworkAllowed)
            builder.Append("(deny network* (local unix-socket))(deny network-outbound (remote unix-socket))");
        builder.Append("(allow process-exec*)(allow process-fork)");
        foreach (var path in profile.ReadDeniedPaths.Concat(profile.OptionalProtectedPaths ?? []).Distinct(StringComparer.Ordinal))
            AppendDenial(path, "file-read*", "file-write*");
        foreach (var path in profile.WriteDeniedPaths?.Distinct(StringComparer.Ordinal) ?? [])
            AppendDenial(path, "file-write*");

        return builder.ToString();

        void AppendDenial(string path, params string[] operations)
        {
            var lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            // Keep both spellings, including when an existing ancestor is a symlink but
            // the leaf does not exist yet.
            var physical = ResolveRealPath(lexical) ?? ResolveMissingSandboxPath(lexical);
            foreach (var spelling in new[] { lexical, physical }.Distinct(StringComparer.Ordinal))
            {
                var literal = EscapeProfileLiteral(spelling);
                foreach (var operation in operations)
                    builder.Append("(deny ").Append(operation).Append(" (subpath \"").Append(literal)
                        .Append("\") (literal \"").Append(literal).Append("\"))");
            }
        }
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

    internal static string? ResolveRealPath(string path)
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

    // The wrapper enforces the boundary, so a writable workspace or a relative PATH
    // entry must never supply it. Use the platform's system installation paths.
    private static string? LocateSeatbelt() =>
        new[] { "/usr/bin/sandbox-exec", "/usr/sbin/sandbox-exec" }.FirstOrDefault(File.Exists);

    internal static string? LocateBubblewrap(string? configuredPath) =>
        configuredPath is not null
            ? File.Exists(configuredPath) ? configuredPath : null
            : new[]
            {
                "/usr/bin/bwrap", "/usr/local/bin/bwrap", "/bin/bwrap",
                "/run/current-system/sw/bin/bwrap", "/nix/var/nix/profiles/default/bin/bwrap"
            }.FirstOrDefault(File.Exists);

    /// <summary>The filesystem and network scope one sandbox enforces; the platform renderers
    /// are the only consumers. <see cref="TrustedPaths" /> names the kernel-owned entries
    /// (the system basics and the per-spawn temporary directory): they resolve without the
    /// steering guard, which exists for policy-derived paths whose trees the cell can
    /// populate.</summary>
    internal sealed record SandboxProfile(
        IReadOnlyList<string> ReadOnlyPaths,
        IReadOnlyList<string> WritablePaths,
        IReadOnlyList<string> ReadDeniedPaths,
        IReadOnlyList<string> TrustedPaths,
        bool NetworkAllowed,
        IReadOnlyList<string>? WriteDeniedPaths = null,
        bool HostRead = false,
        IReadOnlyList<string>? OptionalProtectedPaths = null);
}
