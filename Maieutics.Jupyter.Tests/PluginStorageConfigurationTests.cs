using System.Text.Json;
using FluentAssertions;
using Maieutics;
using Maieutics.Plugins;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginStorageConfigurationTests
{
    // —— ApplicationPaths (ADR 0022 directory selection; the data root also hosts the Agent store) ——

    [Theory]
    [InlineData(true, false, @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming", null, "/Users/u",
        "C:/Users/u/AppData/Local/Maieutics")]
    [InlineData(false, true, "", "/Users/u/Library/Application Support", null, "/Users/u",
        "/Users/u/Library/Application Support/Maieutics")]
    [InlineData(false, false, "", "/home/u/.config", "/xdg/data", "/home/u",
        "/xdg/data/Maieutics")]
    [InlineData(false, false, "", "/home/u/.config", "relative/path", "/home/u",
        "/home/u/.local/share/Maieutics")]
    [InlineData(false, false, "", "/home/u/.config", null, "/home/u",
        "/home/u/.local/share/Maieutics")]
    public void ResolvesThePlatformDataRoot(
        bool isWindows,
        bool isMacOS,
        string localApplicationData,
        string applicationData,
        string? xdgDataHome,
        string userProfile,
        string expected)
    {
        var root = ApplicationPaths.ResolveDataRoot(
            isWindows,
            isMacOS,
            localApplicationData,
            applicationData,
            xdgDataHome,
            userProfile);
        BeSamePath(root, expected);
    }

    [Fact]
    public void DerivesLegacyAndAgentPathsFromTheDataRoot()
    {
        // The resolved data root already carries the product segment (<base>/Maieutics).
        var paths = new ApplicationPaths("/base/Maieutics", "/base/Maieutics", null, "/tmp/Maieutics");
        BeSamePath(paths.PluginDataRoot, "/base/Maieutics/plugin-data");
        BeSamePath(paths.AgentRoot, "/base/Maieutics/agent");
        BeSamePath(paths.AgentSessionsRoot, "/base/Maieutics/agent/sessions");
        BeSamePath(paths.AgentObjectsRoot, "/base/Maieutics/agent/objects");
        BeSamePath(paths.AgentStagingRoot, "/base/Maieutics/agent/objects/.staging");
    }

    [Theory]
    [InlineData(true, false, @"C:\Users\u\AppData\Local", null, "/Users/u",
        "C:/Users/u/AppData/Local/Maieutics")]
    [InlineData(false, true, "", null, "/Users/u",
        "/Users/u/Library/Caches/Maieutics")]
    [InlineData(false, false, "", "/xdg/cache", "/home/u",
        "/xdg/cache/Maieutics")]
    [InlineData(false, false, "", "relative/cache", "/home/u",
        "/home/u/.cache/Maieutics")]
    [InlineData(false, false, "", null, "/home/u",
        "/home/u/.cache/Maieutics")]
    public void ResolvesThePlatformCacheRoot(
        bool isWindows,
        bool isMacOS,
        string localApplicationData,
        string? xdgCacheHome,
        string userProfile,
        string expected)
    {
        var root = ApplicationPaths.ResolveCacheRoot(
            isWindows,
            isMacOS,
            localApplicationData,
            xdgCacheHome,
            userProfile);
        BeSamePath(root, expected);
    }

    [Theory]
    [InlineData(true, null, "/tmp", "u",
        null)]
    [InlineData(false, "/run/user/1000", "/tmp", "u",
        "/run/user/1000/Maieutics")]
    [InlineData(false, "relative/run", "/tmp", "u",
        "/tmp/Maieutics-runtime-u")]
    [InlineData(false, null, "/tmp", "u",
        "/tmp/Maieutics-runtime-u")]
    public void ResolvesThePlatformRuntimeRoot(
        bool isWindows,
        string? xdgRuntimeDir,
        string tempPath,
        string userName,
        string? expected)
    {
        var root = ApplicationPaths.ResolveRuntimeRoot(
            isWindows,
            xdgRuntimeDir,
            tempPath,
            userName);
        if (expected is null)
        {
            root.Should().BeNull();
            return;
        }

        BeSamePath(root!, expected);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Asserts two path spellings match after separator normalization: the production
    /// code joins with the platform separator, so on Windows a POSIX-styled root produces mixed
    /// separators that only compare equal once normalized.</summary>
    private static void BeSamePath(string actual, string expected) =>
        Normalize(actual).Should().Be(Normalize(expected));

    // —— PluginStoragePaths (identity → directory naming) ——

    [Fact]
    public void KeepsSafeManifestNamesVerbatim()
    {
        BeSamePath(PluginStoragePaths.DirectoryFor("/data", "my-plugin"), "/data/my-plugin");
    }

    [Fact]
    public void SanitizedNamesCarryAStableIdentityHash()
    {
        var directory = PluginStoragePaths.DirectoryFor("/data", "@maieutics/example");
        var fileName = Path.GetFileName(directory);
        fileName.Should().StartWith(PluginStoragePaths.Sanitize("@maieutics/example") + "-");
        fileName.Should().NotContain("@");
        // Recomputing for the same identity is deterministic, and a distinct
        // identity never lands on the same directory name.
        PluginStoragePaths.DirectoryFor("/data", "@maieutics/example").Should().Be(directory);
        PluginStoragePaths.DirectoryFor("/data", "@maieutics/other").Should().NotBe(directory);
    }

    [Fact]
    public void SanitizeCollapsesUnsupportedCharacters()
    {
        PluginStoragePaths.Sanitize("@scope/name v2").Should().Be("_scope_name_v2");
        PluginStoragePaths.Sanitize("..").Should().Be("plugin");
        PluginStoragePaths.Sanitize("///").Should().Be("___");
    }

    [Fact]
    public void AssignDisablesStorageForACollidingGroupInsteadOfSharing()
    {
        // Distinct safe names keep their own directories.
        var distinct = PluginStoragePaths.Assign("/data", ["alpha", "beta"]);
        BeSamePath(distinct["alpha"]!, "/data/alpha");
        BeSamePath(distinct["beta"]!, "/data/beta");

        // One identity listed twice is still one store, not a collision.
        BeSamePath(PluginStoragePaths.Assign("/data", ["alpha", "alpha"])["alpha"]!, "/data/alpha");

        // A real directory collision needs two distinct identities whose
        // derived directory names coincide (an 8-hex hash-prefix collision,
        // reachable by birthday search within a few tens of thousands of
        // candidates). The whole colliding group must start WITHOUT storage
        // instead of silently sharing one store.
        var (first, second) = FindDirectoryCollisionPair();
        first.Should().NotBe(second);
        var assignment = PluginStoragePaths.Assign("/data", [first, second]);
        assignment[first].Should().BeNull();
        assignment[second].Should().BeNull();
    }

    /// <summary>Brute-forces two distinct identities whose
    /// <see cref="PluginStoragePaths.DirectoryFor"/> results are equal, using
    /// the public API only. Candidates are <c>probe</c> plus one non-ASCII
    /// code unit — every variant sanitizes to the same form, so the 8-hex
    /// identity hash is the only remaining entropy and a birthday search
    /// finds a collision within tens of thousands of candidates.</summary>
    private static (string First, string Second) FindDirectoryCollisionPair()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 0xF0000; offset++)
        {
            // Astral-plane code points: each candidate is distinct, and both
            // UTF-16 code units are non-ASCII, so every candidate sanitizes to
            // the same form and the 8-hex identity hash is the only entropy.
            var candidate = "probe" + char.ConvertFromUtf32(0x10000 + offset);
            var directory = PluginStoragePaths.DirectoryFor("/data", candidate);
            if (seen.TryGetValue(directory, out var owner))
            {
                return (owner, candidate);
            }
            seen[directory] = candidate;
        }
        throw new InvalidOperationException(
            "No storage-directory collision found within the search budget; the hash prefix is wider than expected.");
    }

    // —— Host config wire format ——

    [Fact]
    public void SerializesTheStorageSectionInCamelCase()
    {
        var emptyGrant = JsonSerializer.SerializeToElement(Array.Empty<string>());
        var config = new PluginHostConfigFile([
            new PluginHostConfigPlugin(
                "example",
                "/plugins/example",
                [new PluginHostConfigWorker("./main", "file:///plugins/example/mod.ts", "example/main")],
                new PluginHostConfigPermissions(
                    emptyGrant,
                    emptyGrant,
                    emptyGrant,
                    emptyGrant,
                    emptyGrant,
                    emptyGrant,
                    emptyGrant,
                    emptyGrant),
                [],
                new PluginHostConfigStorage("/data/plugin-data/example")),
        ]);
        var json = JsonSerializer.Serialize(config, PluginHostJsonContext.Default.PluginHostConfigFile);
        json.Should().Contain("\"storage\":{\"dataDir\":");
    }

    [Fact]
    public void ToleratesAConfigWithoutTheStorageSection()
    {
        // An older kernel writing the pre-storage wire shape must still parse.
        const string json = """
            {"plugins":[{"id":"example","rootDir":"/plugins/example","workers":[],"permissions":
            {"env":[],"net":[],"read":[],"write":[],"run":[],"ffi":[],"sys":[],"import":[]},"dependencies":[]}]}
            """;
        var config = JsonSerializer.Deserialize(json, PluginHostJsonContext.Default.PluginHostConfigFile);
        config.Should().NotBeNull();
        config!.Plugins.Should().HaveCount(1);
        config.Plugins[0].Storage.Should().BeNull();
    }
}
