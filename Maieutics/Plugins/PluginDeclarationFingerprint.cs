using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Maieutics.Plugins;

/// <summary>
///     Canonical fingerprint of a plugin's security-relevant declaration surface (ADR 0037).
///     Approval records persist this fingerprint; a descriptor whose fingerprint no longer
///     matches the recorded one is unapproved until the user re-approves it.
/// </summary>
/// <remarks>
///     The fingerprint covers declarations only — never module code — so editing an approved
///     plugin's source keeps its approval, while any change to entrypoints, permissions,
///     dependencies, isolation, capabilities, extensions, data entries, MCP servers, triggers,
///     or content observation revokes it. Canonicalization follows the
///     <see cref="Mcp.McpServerDefinition.CreateGenerationKey" /> style (fixed field order,
///     Ordinal-sorted lists, length-prefixed UTF-8 into SHA-256), and embedded JSON data is
///     canonicalized recursively so formatting-only manifest edits do not revoke approval.
/// </remarks>
internal static class PluginDeclarationFingerprint
{
    /// <summary>Computes the SHA-256 fingerprint (uppercase hex) of the descriptor's
    /// declaration surface.</summary>
    public static string Compute(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Add(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        void AddList(string tag, IEnumerable<string> values)
        {
            Add(tag);
            foreach (var value in values.Order(StringComparer.Ordinal)) Add(value);

            Add(";");
        }

        void AddGrant(string kind, PluginPermissionGrant grant)
        {
            Add(kind);
            Add(grant.AllowAll ? "*" : string.Empty);
            AddList("v", grant.Values);
        }

        AddGrant("perm.env", descriptor.Permissions.Env);
        AddGrant("perm.net", descriptor.Permissions.Net);
        AddGrant("perm.read", descriptor.Permissions.Read);
        AddGrant("perm.write", descriptor.Permissions.Write);
        AddGrant("perm.run", descriptor.Permissions.Run);
        AddGrant("perm.ffi", descriptor.Permissions.Ffi);
        AddGrant("perm.sys", descriptor.Permissions.Sys);
        AddGrant("perm.import", descriptor.Permissions.Import);

        Add("workers");
        foreach (var worker in descriptor.Workers
                     .OrderBy(static worker => worker.ExportName, StringComparer.Ordinal)
                     .ThenBy(static worker => worker.EntryUrl, StringComparer.Ordinal))
        {
            Add(worker.ExportName);
            Add(worker.EntryUrl);
        }

        Add(";");
        AddList("deps", descriptor.Dependencies);
        Add("isolation");
        Add(descriptor.Isolation);
        AddList("caps", descriptor.Capabilities);

        Add("extensions");
        foreach (var extension in descriptor.Extensions
                     .Select(static extension => (extension.Kind, Data: CanonicalJson(extension.Data)))
                     .OrderBy(static extension => extension.Kind, StringComparer.Ordinal)
                     .ThenBy(static extension => extension.Data, StringComparer.Ordinal))
        {
            Add(extension.Kind);
            Add(extension.Data);
        }

        Add(";");

        Add("data");
        foreach (var entry in descriptor.DataEntries
                     .OrderBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            Add(entry.Name);
            Add(entry.Error is { } error ? $"error:{error}" : CanonicalJson(entry.Data));
        }

        Add(";");

        Add("mcp");
        foreach (var server in descriptor.McpServers
                     .OrderBy(static server => server.Id, StringComparer.Ordinal))
        {
            Add(server.Id);
            Add(server.GenerationKey);
        }

        Add(";");

        Add("triggers");
        foreach (var trigger in descriptor.Triggers
                     .OrderBy(static trigger => trigger.Name, StringComparer.Ordinal))
        {
            Add(trigger.Name);
            Add(trigger.Kind);
            Add(PluginTrigger.ActionName(trigger.Action));
            AddList("paths", trigger.WatchPaths);
            Add(trigger.Depth?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(trigger.CronExpression);
            Add(trigger.IntervalSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Add(";");
        Add(descriptor.InspectionsContentReadAll ? "inspections.contentReadAll" : string.Empty);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Whether the descriptor declares nothing that needs approval: no workers,
    /// no data entries, no capabilities, no extensions, no MCP servers, no triggers, no
    /// permission grants, and no content observation (ADR 0037 decision 2 — the first-boot
    /// plugins-root skeleton never blocks).</summary>
    public static bool IsApprovalExempt(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Workers.Count == 0 &&
               descriptor.DataEntries.Count == 0 &&
               descriptor.Capabilities.Count == 0 &&
               descriptor.Extensions.Count == 0 &&
               descriptor.McpServers.Count == 0 &&
               descriptor.Triggers.Count == 0 &&
               !descriptor.InspectionsContentReadAll &&
               HasNoGrants(descriptor.Permissions);
    }

    private static bool HasNoGrants(PluginPermissionGrants permissions)
    {
        return IsEmpty(permissions.Env) && IsEmpty(permissions.Net) && IsEmpty(permissions.Read) &&
               IsEmpty(permissions.Write) && IsEmpty(permissions.Run) && IsEmpty(permissions.Ffi) &&
               IsEmpty(permissions.Sys) && IsEmpty(permissions.Import);

        static bool IsEmpty(PluginPermissionGrant grant)
        {
            return !grant.AllowAll && grant.Values.Count == 0;
        }
    }

    /// <summary>Canonical JSON text of one embedded manifest value: object members sorted
    /// Ordinal, arrays in order, primitives in their source form. Two documents that differ
    /// only in member order or whitespace canonicalize identically, so reformatting a
    /// manifest does not revoke its approval.</summary>
    private static string CanonicalJson(JsonElement? element)
    {
        if (element is not { } value) return string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                // Canonicalize through an invariant round-trip so equal values written in
                // different spellings (1, 1.0, 1e2) hash identically.
                if (element.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else if (element.TryGetDouble(out var real)) writer.WriteNumberValue(real);
                else writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
