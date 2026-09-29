using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Plugins;

/// <summary>The trigger action: how a fired trigger is delivered (ADR 0036).</summary>
internal enum PluginTriggerAction
{
    /// <summary>Wakes the owning worker and dispatches to its PluginEvent extension point.</summary>
    Event,

    /// <summary>Republishes the plugin's MCP registration subset (discovery re-runs, no wake).</summary>
    Rediscover,
}

/// <summary>One manifest-declared trigger (ADR 0036). The declaration is kernel-validated;
/// the listener is held by the Deno host; delivery rides ADR 0035's wake machinery.</summary>
internal sealed record PluginTrigger(
    string Name,
    string Kind,
    PluginTriggerAction Action,
    IReadOnlyList<string> WatchPaths,
    int? Depth,
    string? CronExpression,
    int? IntervalSeconds)
{
    internal static PluginTriggerAction ActionFrom(string value)
    {
        return value switch
        {
            "event" => PluginTriggerAction.Event,
            "rediscover" => PluginTriggerAction.Rediscover,
            _ => throw new InvalidOperationException(
                $"Trigger action '{value}' is not supported (known: event, rediscover)."),
        };
    }

    internal static string ActionName(PluginTriggerAction action)
    {
        return action switch
        {
            PluginTriggerAction.Event => "event",
            PluginTriggerAction.Rediscover => "rediscover",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}

/// <summary>Parses and validates the manifest's `triggers` section (ADR 0036). The kernel
/// owns the declaration plane: kinds are a closed catalog (watch/cron/interval today),
/// watch paths expand through the SAME variable table as the permission store, and the
/// concrete path set ships to the host with the plugin config. Structural failures fail
/// the plugin load; a syntactically valid trigger whose watched path does not exist is
/// tolerated (the trigger may predate the software it watches).</summary>
internal static class PluginTriggerReader
{
    private const int MaximumWatchPaths = 16;
    private const int MaximumDepth = 6;
    private const int MinimumIntervalSeconds = 1;

    public static IReadOnlyList<PluginTrigger> Read(
        JsonElement? section,
        Maieutics.Permissions.VariableTable variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var triggers = new List<PluginTrigger>();
        if (section is not { ValueKind: JsonValueKind.Array } array)
        {
            if (section is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                throw new JsonException("The 'triggers' section must be an array.");
            return triggers;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new JsonException("The 'triggers' entries must be objects.");

            var name = ReadString(entry, "name") ??
                throw new JsonException("A trigger requires a non-empty 'name'.");
            if (!names.Add(name))
                throw new JsonException($"A trigger named '{name}' is declared more than once.");

            var actionValue = ReadString(entry, "action") ?? "event";
            var action = ParseAction(entry, actionValue);
            var kind = ReadString(entry, "kind") ??
                throw new JsonException($"Trigger '{name}' requires a 'kind'.");

            switch (kind)
            {
                case "watch":
                {
                    if (entry.TryGetProperty("paths", out var paths) &&
                        paths.ValueKind == JsonValueKind.Array)
                    {
                        var watchPaths = new List<string>();
                        foreach (var path in paths.EnumerateArray())
                        {
                            if (path.ValueKind != JsonValueKind.String) continue;
                            var raw = path.GetString();
                            if (string.IsNullOrWhiteSpace(raw)) continue;
                            watchPaths.Add(variables.Expand(raw));
                        }

                        if (watchPaths.Count == 0)
                            throw new JsonException($"Watch trigger '{name}' declares no usable paths.");
                        if (watchPaths.Count > MaximumWatchPaths)
                            throw new JsonException(
                                $"Watch trigger '{name}' exceeds {MaximumWatchPaths} paths.");
                        var depth = ReadDepth(entry, name);
                        triggers.Add(new PluginTrigger(
                            name, "watch", action, watchPaths, depth, null, null));
                    }
                    else
                    {
                        throw new JsonException($"Watch trigger '{name}' requires a 'paths' array.");
                    }

                    break;
                }

                case "cron":
                {
                    var expression = ReadString(entry, "expression") ??
                        throw new JsonException($"Cron trigger '{name}' requires an 'expression'.");
                    ValidateCron(name, expression);
                    triggers.Add(new PluginTrigger(
                        name, "cron", action, [], null, expression, null));
                    break;
                }

                case "interval":
                {
                    if (!entry.TryGetProperty("seconds", out var seconds) ||
                        seconds.ValueKind != JsonValueKind.Number ||
                        seconds.GetInt32() < MinimumIntervalSeconds)
                    {
                        throw new JsonException(
                            $"Interval trigger '{name}' requires a positive whole-number 'seconds'.");
                    }

                    triggers.Add(new PluginTrigger(
                        name, "interval", action, [], null, null, seconds.GetInt32()));
                    break;
                }

                default:
                    throw new JsonException(
                        $"Trigger '{name}' uses the unknown kind '{kind}' " +
                        "(known: watch, cron, interval).");
            }
        }

        return triggers;
    }

    private static string? ReadString(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static PluginTriggerAction ParseAction(JsonElement entry, string actionValue)
    {
        if (entry.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            actionValue = ReadString(action, "type") ?? actionValue;
        }

        return PluginTrigger.ActionFrom(actionValue);
    }

    private static int? ReadDepth(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty("depth", out var depth)) return null;
        if (depth.ValueKind != JsonValueKind.Number || depth.GetInt32() is < 1 or > MaximumDepth)
            throw new JsonException(
                $"Watch trigger '{name}' has a 'depth' outside 1..{MaximumDepth}.");
        return depth.GetInt32();
    }

    /// <summary>Five-field local-time cron (minute hour day-of-month month day-of-week).
    /// Validation is structural: field count, ranges, and the step/list operators.</summary>
    private static void ValidateCron(string name, string expression)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new JsonException(
                $"Cron trigger '{name}' must have exactly 5 fields (minute hour day month weekday).");
        var ranges = new[] { (0, 59), (0, 23), (1, 31), (1, 12), (0, 6) };
        for (var i = 0; i < fields.Length; i++)
        {
            foreach (var part in fields[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var step = part.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (step.Length > 2)
                    throw new JsonException($"Cron trigger '{name}' has a malformed step in field {i + 1}.");
                foreach (var value in step[0].Split('-', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (value != "*" &&
                        (!int.TryParse(value, out var parsed) ||
                         parsed < ranges[i].Item1 ||
                         parsed > ranges[i].Item2))
                    {
                        throw new JsonException(
                            $"Cron trigger '{name}' field {i + 1} value '{value}' is out of range.");
                    }
                }
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginHostConfigTrigger))]
internal sealed partial class PluginTriggerJsonContext : JsonSerializerContext;

/// <summary>The trigger wire form shipped to the host in the plugin config (ADR 0036):
/// watch paths are already kernel-expanded; the host only listens on what it was given.</summary>
internal sealed record PluginHostConfigTrigger(
    string Name,
    string Kind,
    string Action,
    IReadOnlyList<string>? Paths = null,
    int? Depth = null,
    string? Expression = null,
    int? Seconds = null);
