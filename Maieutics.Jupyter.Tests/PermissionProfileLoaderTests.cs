using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Jupyter.Tests;

public sealed class PermissionProfileLoaderTests
{
    [Fact]
    public void MissingFileYieldsAnEmptyLayer()
    {
        var layer = PermissionProfileLoader.Load("/nonexistent/permissions.json");

        layer.Kinds.Should().BeEmpty();
    }

    [Fact]
    public void LoadsTheDefaultSetAndResolvesRelativePaths()
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
                    "default": { "read": { "allow": ["./src", "/etc/ssl"], "deny": ["./src/secret"] } }
                  },
                  "default": "default"
                }
                """);

            var layer = PermissionProfileLoader.Load(path);

            var read = layer.Kinds[PermissionKind.Read];
            read.Allow.Should().Equal(Path.Combine(root, "src"), "/etc/ssl");
            read.Deny.Should().Equal(Path.Combine(root, "src", "secret"));
        }
        finally
        {
            Directory.Delete(root, true);
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

            layer.Kinds[PermissionKind.Env].Allow.Should().Equal(["PATH"]);
        }
        finally
        {
            Directory.Delete(root, true);
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

            load.Should().Throw<PermissionException>()
                .Which.Code.Should().Be("permission_profile_invalid");
        }
        finally
        {
            Directory.Delete(root, true);
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

            load.Should().Throw<PermissionException>()
                .Which.Code.Should().Be("permission_profile_invalid");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
