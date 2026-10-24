namespace Maieutics.Product.Tests;

/// <summary>App-wide default path patterns must be absolute or variable-anchored: the app config
/// is anchored to no directory, so a relative path pattern has no defined base there (the
/// workspace profile resolves relatives against its own file). <c>run</c> keeps bare command
/// names, so it is exempt from the path rule.</summary>
public sealed class MaieuticsPermissionsOptionsTests
{
    [Fact]
    public void RelativePathPatternIsRejected()
    {
        var options = new MaieuticsOptions
        {
            Permissions = new MaieuticsPermissionsOptions
            {
                Write = new MaieuticsPermissionKindOptions { Allow = ["AGENTS.md"] }
            }
        };

        var validate = () => options.ValidateCommon();

        var failure = Assert.ThrowsAny<ArgumentException>(validate.Invoke);
        Assert.Contains("relative", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsoluteAndVariableAnchoredPathPatternsAreAccepted()
    {
        var options = new MaieuticsOptions
        {
            Permissions = new MaieuticsPermissionsOptions
            {
                Read = new MaieuticsPermissionKindOptions { Allow = ["/etc/ssl", "${var.workspace}/cache"] },
                Ffi = new MaieuticsPermissionKindOptions { Deny = ["/opt/lib"] }
            }
        };

        options.ValidateCommon();
    }

    [Fact]
    public void RunKeepsBareCommandNames()
    {
        var options = new MaieuticsOptions
        {
            Permissions = new MaieuticsPermissionsOptions
            {
                Run = new MaieuticsPermissionKindOptions { Allow = ["deno", "/usr/bin/env"] }
            }
        };

        options.ValidateCommon();
    }
}
