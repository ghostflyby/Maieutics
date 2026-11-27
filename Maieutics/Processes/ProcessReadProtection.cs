namespace Maieutics.Processes;

/// <summary>Host-owned credential and private runtime paths hidden by a broad read grant.</summary>
internal static class ProcessReadProtection
{
    internal static string[] CaptureOptional(
        IEnumerable<string?> applicationPaths, string home, string? configHome, string? sshAgentSocket)
    {
        var paths = applicationPaths.OfType<string>().Select(Path.GetFullPath).ToList();
        if (!string.IsNullOrEmpty(sshAgentSocket)) paths.Add(Path.GetFullPath(sshAgentSocket));

        if (Path.IsPathFullyQualified(home))
        {
            foreach (var relative in new[]
                     {
                         ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker/config.json",
                         ".git-credentials", ".netrc", ".npmrc", ".cargo/credentials", ".cargo/credentials.toml",
                         ".pgpass", ".vault-token", "Library/Keychains"
                     })
                paths.Add(Path.Combine(home, relative));

            var config = configHome is not null && Path.IsPathFullyQualified(configHome)
                ? configHome : Path.Combine(home, ".config");
            foreach (var relative in new[] { "gcloud", "gh", "git/credentials" })
                paths.Add(Path.Combine(config, relative));
        }

        return paths.ToArray();
    }
}
