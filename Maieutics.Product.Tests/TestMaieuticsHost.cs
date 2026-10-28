using Microsoft.AspNetCore.Builder;

namespace Maieutics.Product.Tests;

/// <summary>Places host-created persistent data inside the test-owned configuration area,
/// whose existing teardown deletes it after the host has been disposed.</summary>
internal static class TestMaieuticsHost
{
    public static WebApplicationBuilder CreateApplicationBuilder(string[] args)
    {
        var configIndex = Array.IndexOf(args, "--config");
        if (configIndex < 0 || configIndex + 1 >= args.Length)
            throw new ArgumentException("Test hosts require an explicit temporary configuration file.", nameof(args));

        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(args[configIndex + 1]))
            ?? throw new ArgumentException("The test configuration must have a parent directory.", nameof(args));
        return MaieuticsHost.CreateApplicationBuilder(
            [.. args, "--Maieutics:DataRoot", Path.Combine(configDirectory, "data")]);
    }
}
