namespace Maieutics;

/// <summary>
///     Platform application directories owned by the composition root (ADR 0022). One place
///     decides where persistent product data lives so kernel modules derive their paths from this
///     snapshot instead of calling <c>Environment.GetFolderPath</c> ad hoc. Configuration discovery
///     and the plugins code root keep their existing locations; this type only adds the per-plugin
///     persistent-data root.
/// </summary>
internal sealed record ApplicationPaths(string PluginDataRoot)
{
    public static ApplicationPaths Resolve()
    {
        return new ApplicationPaths(ResolvePluginDataRoot(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    /// <summary>Pure resolver so tests can pin each platform shape without changing the OS.
    /// Windows stores per-machine plugin data in the local application-data root (it must not
    /// roam), macOS uses the application-support root, and Linux follows XDG Base Directory with
    /// <c>$XDG_DATA_HOME</c> (absolute paths only) and the <c>~/.local/share</c> default.</summary>
    internal static string ResolvePluginDataRoot(
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
                "Cannot resolve the platform application-data directory for plugin storage.");
        }
        return Path.Combine(baseDirectory, "Maieutics", "plugin-data");
    }
}
