namespace Maieutics;

/// <summary>
///     Platform application directories owned by the composition root (ADR 0022). One place
///     decides where the product's persistent data, caches, runtime endpoints, and temporary
///     staging live so kernel modules derive their paths from this snapshot instead of calling
///     <c>Environment.GetFolderPath</c> ad hoc. Configuration discovery and the plugins code
///     root keep their existing locations.
/// </summary>
/// <param name="DataRoot">
///     Machine-local persistent data (plugin databases, the Agent transcript store). Survives
///     restarts and is the migration boundary for everything below it.
/// </param>
/// <param name="CacheRoot">Rebuildable derived data; the OS may purge it at any time.</param>
/// <param name="RuntimeRoot">
///     Process-lifetime rendezvous paths such as Unix sockets. <see langword="null" /> on
///     Windows, where named pipes need no directory.
/// </param>
/// <param name="TempRoot">Single-run staging under the platform temporary directory.</param>
internal sealed record ApplicationPaths(
    string DataRoot,
    string CacheRoot,
    string? RuntimeRoot,
    string TempRoot)
{
    public string PluginDataRoot => Path.Combine(DataRoot, "plugin-data");

    public string AgentRoot => Path.Combine(DataRoot, "agent");

    /// <summary>Per-family transcript databases: one <c>history.db</c> per fork family.</summary>
    public string AgentSessionsRoot => Path.Combine(AgentRoot, "sessions");

    /// <summary>Content-addressed blob objects.</summary>
    public string AgentObjectsRoot => Path.Combine(AgentRoot, "objects");

    /// <summary>Same-volume ingest staging for atomic blob publication.</summary>
    public string AgentStagingRoot => Path.Combine(AgentObjectsRoot, ".staging");

    /// <summary>Creates the directory that backs <see cref="AgentDatabasePath" /> when persistence is enabled.</summary>
    public void EnsureAgentRoot()
    {
        Directory.CreateDirectory(AgentRoot);
    }

    public static ApplicationPaths Resolve()
    {
        var isWindows = OperatingSystem.IsWindows();
        var isMacOS = OperatingSystem.IsMacOS();
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tempPath = Path.GetTempPath();
        return new ApplicationPaths(
            ResolveDataRoot(isWindows, isMacOS, localApplicationData, applicationData, Environment.GetEnvironmentVariable("XDG_DATA_HOME"), userProfile),
            ResolveCacheRoot(isWindows, isMacOS, localApplicationData, Environment.GetEnvironmentVariable("XDG_CACHE_HOME"), userProfile),
            ResolveRuntimeRoot(isWindows, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), tempPath, Environment.UserName),
            Path.Combine(tempPath, "Maieutics"));
    }

    /// <summary>Pure resolver so tests can pin each platform shape without changing the OS.
    /// Windows stores per-machine data in the local application-data root (it must not roam),
    /// macOS uses the application-support root, and Linux follows XDG Base Directory with
    /// <c>$XDG_DATA_HOME</c> (absolute paths only) and the <c>~/.local/share</c> default.</summary>
    internal static string ResolveDataRoot(
        bool isWindows,
        bool isMacOS,
        string localApplicationData,
        string applicationData,
        string? xdgDataHome,
        string userProfile)
    {
        var baseDirectory = ResolvePosixLikeRoot(isWindows, isMacOS, localApplicationData, applicationData, xdgDataHome, userProfile);
        return Path.Combine(baseDirectory, "Maieutics");
    }

    /// <summary>Windows keeps the cache beside the data root; macOS uses the dedicated caches
    /// root the OS may purge; Linux follows <c>$XDG_CACHE_HOME</c> (absolute paths only).</summary>
    internal static string ResolveCacheRoot(
        bool isWindows,
        bool isMacOS,
        string localApplicationData,
        string? xdgCacheHome,
        string userProfile)
    {
        var baseDirectory = isWindows
            ? localApplicationData
            : isMacOS
                ? Path.Combine(userProfile, "Library", "Caches")
                : !string.IsNullOrWhiteSpace(xdgCacheHome) && Path.IsPathRooted(xdgCacheHome)
                    ? xdgCacheHome
                    : Path.Combine(userProfile, ".cache");
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new InvalidOperationException(
                "Cannot resolve the platform cache directory for Maieutics.");
        }

        return Path.Combine(baseDirectory, "Maieutics");
    }

    /// <summary>Linux prefers <c>$XDG_RUNTIME_DIR</c> (absolute paths only) with a private
    /// temporary fallback; macOS parks runtime endpoints in the per-user temporary directory;
    /// Windows returns <see langword="null" /> because named pipes carry no filesystem path.</summary>
    internal static string? ResolveRuntimeRoot(
        bool isWindows,
        string? xdgRuntimeDir,
        string tempPath,
        string userName)
    {
        if (isWindows) return null;
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDir) && Path.IsPathRooted(xdgRuntimeDir))
        {
            return Path.Combine(xdgRuntimeDir, "Maieutics");
        }

        return Path.Combine(tempPath, $"Maieutics-runtime-{userName}");
    }

    private static string ResolvePosixLikeRoot(
        bool isWindows,
        bool isMacOS,
        string localApplicationData,
        string applicationData,
        string? xdgDataHome,
        string userProfile)
    {
        var baseDirectory = (isWindows, isMacOS) switch
        {
            (true, _) => localApplicationData,
            (_, true) => applicationData,
            _ => !string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome)
                ? xdgDataHome
                : Path.Combine(userProfile, ".local", "share"),
        };
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new InvalidOperationException(
                "Cannot resolve the platform application-data directory for Maieutics.");
        }

        return baseDirectory;
    }
}
