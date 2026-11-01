#:property TreatWarningsAsErrors=true

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

if (args is not [var directory])
{
    Console.Error.WriteLine("Usage: test_timing_summary.cs <results-directory>");
    return 2;
}
var reports = Directory.Exists(directory)
    ? Directory.GetFiles(directory, "*.xunit.xml", SearchOption.AllDirectories)
    : [];
var tests = new List<TestTiming>();
var assemblies = new List<(string Name, double Seconds)>();
foreach (var file in reports)
{
    using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    var document = XDocument.Load(reader);
    foreach (var assembly in document.Descendants("assembly"))
    {
        var assemblyName = Path.GetFileName((string?)assembly.Attribute("name") ?? file);
        var reportDirectory = Path.GetDirectoryName(Path.GetRelativePath(directory, file));
        var label = string.IsNullOrEmpty(reportDirectory) ? assemblyName : $"{reportDirectory}/{assemblyName}";
        assemblies.Add((label, Seconds(assembly)));
        foreach (var test in assembly.Descendants("test"))
            tests.Add(new(assemblyName, (string?)test.Attribute("type") ?? "(unknown)",
                (string?)test.Attribute("name") ?? "(unnamed)", Seconds(test),
                (string?)test.Attribute("result") ?? "(unknown)"));
    }
}
var text = new StringBuilder("## Test timing\n\n");
text.AppendLine($"{reports.Length} report(s), {tests.Count} cases: {tests.Count(test => test.Result == "Pass")} passed, {tests.Count(test => test.Result == "Fail")} failed, {tests.Count(test => test.Result == "Skip")} skipped.\n");
text.AppendLine("Class totals sum case durations and can overlap under parallel execution. Assembly times include runner overhead.\n");
text.AppendLine("| Assembly/pass | Wall seconds |\n|---|---:|");
foreach (var assembly in assemblies)
    text.AppendLine($"| {Escape(assembly.Name)} | {Format(assembly.Seconds)} |");
text.AppendLine("\n### Slowest classes\n\n| Class | Cases | Sum seconds |\n|---|---:|---:|");
foreach (var group in tests.GroupBy(test => test.Type).OrderByDescending(group => group.Sum(test => test.Seconds)).Take(20))
    text.AppendLine($"| {Escape(group.Key)} | {group.Count()} | {Format(group.Sum(test => test.Seconds))} |");
text.AppendLine("\n### Slowest cases\n\n| Test | Result | Seconds |\n|---|---|---:|");
foreach (var test in tests.OrderByDescending(test => test.Seconds).Take(20))
    text.AppendLine($"| {Escape(test.Name)} | {Escape(test.Result)} | {Format(test.Seconds)} |");
Console.Write(text);
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
    await File.AppendAllTextAsync(summary, text.ToString());
return 0;

static double Seconds(XElement element) => double.TryParse((string?)element.Attribute("time"),
    NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : 0;
static string Format(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
    .Replace("|", "&#124;").Replace("`", "&#96;").Replace("\r", " ").Replace("\n", " ");
internal sealed record TestTiming(string Assembly, string Type, string Name, double Seconds, string Result);
