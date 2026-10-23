using System.Globalization;
using Xunit;

namespace Maieutics.Testing;

/// <summary>One trace writer for the suite: prefixed, timestamped diagnostic lines through
/// the test output. Failure diagnostics must name the mechanism they observed
/// (test principle 6); the shared format keeps every suite area's traces readable as one
/// timeline.</summary>
public static class TestTrace
{
    /// <summary>Writes one diagnostic line to the test output, prefixed with the area tag
    /// and a UTC timestamp.</summary>
    public static void Write(ITestOutputHelper output, string tag, string message)
    {
        output.WriteLine($"[{tag}] {DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}");
    }
}
