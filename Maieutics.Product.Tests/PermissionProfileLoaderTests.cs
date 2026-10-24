using Maieutics.Permissions;

namespace Maieutics.Product.Tests;

public sealed class PermissionProfileLoaderTests
{
    [Fact]
    public void MissingFileYieldsAnEmptyLayer()
    {
        var layer = PermissionProfileLoader.Load("/nonexistent/permissions.json");

        Assert.Empty(layer.Kinds);
    }

    [Fact]
    public void LoadsTheDefaultSetAndResolvesRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-perm-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // A platform root path (e.g. / on Unix, C:\ on Windows): rooted, so the loader must
            // not resolve it against the profile directory.
            var absolutePath = Path.GetFullPath(Path.GetPathRoot(Path.GetTempPath())!);
            var path = Path.Combine(root, "permissions.json");
            File.WriteAllText(
                path,
                $$"""
                {
                  "sets": {
                    "default": { "read": { "allow": ["./src", "{{absolutePath.Replace("\\", "\\\\")}}"], "deny": ["./src/secret"] } }
                  },
                  "default": "default"
                }
                """);

            var layer = PermissionProfileLoader.Load(path);

            var read = layer.Kinds[PermissionKind.Read];
            Assert.Equal([Path.Combine(root, "src"), absolutePath], read.Allow);
            Assert.Equal([Path.Combine(root, "src", "secret")], read.Deny);
        }
        finally
        {
            TempArea.DeleteTree(root);
        }
    }

    [Fact]
    public void SelectedSetOverridesTheDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-perm-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "permissions.json");
            File.WriteAllText(
                path,
                """
                {
                  "sets": {
                    "default": { "env": { "allow": ["HOME"] } },
                    "strict": { "env": { "allow": ["PATH"] } }
                  },
                  "default": "default"
                }
                """);

            var layer = PermissionProfileLoader.Load(path, "strict");

            Assert.Equal(["PATH"], layer.Kinds[PermissionKind.Env].Allow);
        }
        finally
        {
            TempArea.DeleteTree(root);
        }
    }

    [Fact]
    public void InvalidJsonThrowsATypedError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-perm-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "permissions.json");
            File.WriteAllText(path, "{ not json");

            var load = () => PermissionProfileLoader.Load(path);

            Assert.Equal("permission_profile_invalid", Assert.ThrowsAny<PermissionException>(() => load.Invoke()).Code);
        }
        finally
        {
            TempArea.DeleteTree(root);
        }
    }

    [Fact]
    public void MissingDefaultSetThrowsATypedError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-perm-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "permissions.json");
            File.WriteAllText(path, """{ "sets": { "default": {} } }""");

            var load = () => PermissionProfileLoader.Load(path);

            Assert.Equal("permission_profile_invalid", Assert.ThrowsAny<PermissionException>(() => load.Invoke()).Code);
        }
        finally
        {
            TempArea.DeleteTree(root);
        }
    }
}
