using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Plugins;

/// <summary>One persisted user approval (ADR 0037): the declaration fingerprint the user
/// accepted, plus the grant summary captured at approval time for display and diffing.</summary>
internal sealed record PluginApprovalRecord(
    string Fingerprint,
    string? Name,
    DateTimeOffset ApprovedAt,
    PluginPermissionGrants Grants);

/// <summary>
///     The persisted plugin approval registry (ADR 0037): <c>&lt;DataRoot&gt;/plugin-approvals.json</c>.
///     One record per plugin id, replaced wholesale by each approval. The file is versioned and
///     tolerates unknown fields; loading is fail-closed (a missing file is an empty store, an
///     unusable file approves nothing and reports its error); saves are atomic and a failed
///     save fails the approval action. The store is a consent gate at the same trust level as
///     the workspace permission profile, not a boundary against the local user.
/// </summary>
internal sealed class PluginApprovalStore
{
    internal const int CurrentVersion = 1;

    private readonly string path;
    private readonly Lock gate = new();
    private Dictionary<string, PluginApprovalRecord> approvals = new(StringComparer.Ordinal);

    private PluginApprovalStore(string path)
    {
        this.path = path;
    }

    /// <summary>An empty, persisted-nowhere store: the fail-closed stand-in before the first
    /// load and after an unusable file.</summary>
    internal static PluginApprovalStore Empty()
    {
        return new PluginApprovalStore(Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.json"));
    }

    /// <summary>Loads the store from disk. A missing file yields an empty store; a file that
    /// exists but cannot be used (invalid JSON, unknown schema version) yields an empty store
    /// plus a visible error — nothing stays approved on a corrupt file. The in-memory snapshot
    /// then persists for the process lifetime; the file is not watched.</summary>
    public static PluginApprovalStore Load(string path, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var store = new PluginApprovalStore(path);
        error = null;
        if (!File.Exists(path)) return store;

        try
        {
            var file = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                PluginApprovalsJsonContext.Default.PluginApprovalsFile);
            if (file is null || file.Version != CurrentVersion)
            {
                error = file is null
                    ? "The plugin approvals file is null."
                    : $"The plugin approvals file version {file.Version} is not supported (expected {CurrentVersion}).";
                return store;
            }

            store.approvals = (file.Approvals ?? new Dictionary<string, PluginApprovalRecordFile>())
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => new PluginApprovalRecord(
                        pair.Value.Fingerprint,
                        pair.Value.Name,
                        pair.Value.ApprovedAt,
                        FromFileGrants(pair.Value.Grants)),
                    StringComparer.Ordinal);
            return store;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
            NotSupportedException or InvalidOperationException)
        {
            error = $"The plugin approvals file '{path}' could not be read: {exception.Message}";
            return store;
        }
    }

    public bool TryGet(string pluginId, out PluginApprovalRecord? record)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        lock (gate)
        {
            if (approvals.TryGetValue(pluginId, out var found))
            {
                record = found;
                return true;
            }

            record = null;
            return false;
        }
    }

    /// <summary>Replaces (or creates) one plugin's approval and persists it. A failed persist
    /// leaves both the file and the in-memory store unchanged — an approval that was not
    /// written never counts.</summary>
    public void Set(string pluginId, PluginApprovalRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            var next = new Dictionary<string, PluginApprovalRecord>(approvals, StringComparer.Ordinal)
            {
                [pluginId] = record
            };
            Persist(next);
            approvals = next;
        }
    }

    /// <summary>Removes one plugin's approval (a revoke) and persists it.</summary>
    public bool Remove(string pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        lock (gate)
        {
            if (!approvals.ContainsKey(pluginId)) return false;
            var next = new Dictionary<string, PluginApprovalRecord>(approvals, StringComparer.Ordinal);
            next.Remove(pluginId);
            Persist(next);
            approvals = next;
            return true;
        }
    }

    public IReadOnlyDictionary<string, PluginApprovalRecord> Snapshot()
    {
        lock (gate)
        {
            return new Dictionary<string, PluginApprovalRecord>(approvals, StringComparer.Ordinal);
        }
    }

    private void Persist(Dictionary<string, PluginApprovalRecord> next)
    {
        var file = new PluginApprovalsFile(
            CurrentVersion,
            next.ToDictionary(
                static pair => pair.Key,
                static pair => new PluginApprovalRecordFile(
                    pair.Value.Fingerprint,
                    pair.Value.Name,
                    pair.Value.ApprovedAt,
                    ToFileGrants(pair.Value.Grants)),
                StringComparer.Ordinal));
        var json = JsonSerializer.Serialize(file, PluginApprovalsJsonContext.Default.PluginApprovalsFile);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Grant summaries persist in the manifest wire shape (a kind appears only
    /// when it carries a grant), so the file reads like the plugin's own declaration.</summary>
    private static PluginManifestPermissionSet ToFileGrants(PluginPermissionGrants grants)
    {
        return new PluginManifestPermissionSet(
            GrantOrNone(grants.Env),
            GrantOrNone(grants.Net),
            GrantOrNone(grants.Read),
            GrantOrNone(grants.Write),
            GrantOrNone(grants.Run),
            GrantOrNone(grants.Ffi),
            GrantOrNone(grants.Sys),
            GrantOrNone(grants.Import));

        static PluginPermissionGrant? GrantOrNone(PluginPermissionGrant grant)
        {
            return grant.AllowAll || grant.Values.Count > 0 ? grant : null;
        }
    }

    private static PluginPermissionGrants FromFileGrants(PluginManifestPermissionSet? grants)
    {
        return new PluginPermissionGrants(
            grants?.Env ?? PluginPermissionGrant.None,
            grants?.Net ?? PluginPermissionGrant.None,
            grants?.Read ?? PluginPermissionGrant.None,
            grants?.Write ?? PluginPermissionGrant.None,
            grants?.Run ?? PluginPermissionGrant.None,
            grants?.Ffi ?? PluginPermissionGrant.None,
            grants?.Sys ?? PluginPermissionGrant.None,
            grants?.Import ?? PluginPermissionGrant.None);
    }
}

internal sealed record PluginApprovalsFile(
    int Version,
    Dictionary<string, PluginApprovalRecordFile>? Approvals);

internal sealed record PluginApprovalRecordFile(
    string Fingerprint,
    string? Name,
    DateTimeOffset ApprovedAt,
    PluginManifestPermissionSet? Grants);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(PluginApprovalsFile))]
[JsonSerializable(typeof(PluginApprovalRecordFile))]
[JsonSerializable(typeof(PluginManifestPermissionSet))]
internal sealed partial class PluginApprovalsJsonContext : JsonSerializerContext;
