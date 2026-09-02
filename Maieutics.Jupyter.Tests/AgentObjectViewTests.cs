using System.Text;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

/// <summary>The derived inspection view: repair rebuilds relative links from canonical data,
/// is idempotent, and never links to absent objects.</summary>
public sealed class AgentObjectViewTests : IDisposable
{
    private readonly string agentRoot;

    public AgentObjectViewTests()
    {
        agentRoot = Path.Combine(
            Path.GetTempPath(),
            "maieutics-object-view-tests",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(agentRoot))
        {
            Directory.Delete(agentRoot, recursive: true);
        }
    }

    [Fact]
    public void RepairCreatesIdempotentRelativeLinksForReferencedObjects()
    {
        var objectsRoot = Path.Combine(agentRoot, "objects");
        var viewSessionsRoot = Path.Combine(agentRoot, "view", "sessions");
        var objectStore = new ObjectStore(objectsRoot);
        var sessionId = AgentSessionId.Create();
        var familyId = sessionId;
        var ingested = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("inspectable bytes")));
        using var store = new SqliteTranscriptStore(
            SqliteTranscriptStore.FamilyDatabasePath(Path.Combine(agentRoot, "families"), familyId));
        store.AppendTurn(sessionId, Turn(sessionId, ingested.Sha256), [ingested.Sha256]);

        var ensured = AgentObjectView.Repair(viewSessionsRoot, objectsRoot, [store]);

        // The view degrades on platforms without symlink privileges (Windows CI); the
        // degradation contract is an empty view, never a failed repair.
        if (!SymlinksSupported(agentRoot))
        {
            ensured.Should().Be(0);
            return;
        }

        ensured.Should().Be(1);
        var linkPath = Path.Combine(viewSessionsRoot, sessionId.Value.ToString("N"), "objects", ingested.Sha256);
        File.Exists(linkPath).Should().BeTrue();
        File.ReadAllText(linkPath).Should().Be("inspectable bytes");
        Path.GetFileName(File.ResolveLinkTarget(linkPath, returnFinalTarget: false)!.FullName)
            .Should().Be(ingested.Sha256);

        AgentObjectView.Repair(viewSessionsRoot, objectsRoot, [store]).Should().Be(1);
    }

    [Fact]
    public void RepairSkipsReferencesToAbsentObjects()
    {
        var objectsRoot = Path.Combine(agentRoot, "objects");
        var viewSessionsRoot = Path.Combine(agentRoot, "view", "sessions");
        var sessionId = AgentSessionId.Create();
        var missing = new string('a', 64);
        using var store = new SqliteTranscriptStore(
            SqliteTranscriptStore.FamilyDatabasePath(Path.Combine(agentRoot, "families"), sessionId));
        store.AppendTurn(sessionId, Turn(sessionId, missing), [missing]);

        AgentObjectView.Repair(viewSessionsRoot, objectsRoot, [store]).Should().Be(0);
        Directory.Exists(Path.Combine(viewSessionsRoot, sessionId.Value.ToString("N"), "objects"))
            .Should().BeFalse();
    }

    /// <summary>Probes whether this environment may create symlinks at all (Windows needs
    /// Developer Mode or elevation; the CI runner has neither).</summary>
    private static bool SymlinksSupported(string root)
    {
        var probe = Path.Combine(root, ".symlink-probe");
        try
        {
            File.CreateSymbolicLink(probe, "probe-target");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static AgentTranscriptTurn Turn(AgentSessionId sessionId, string sha256)
    {
        var runId = new AgentRunId(Guid.Parse($"00000000-0000-0000-0000-{sha256[..11].PadLeft(12, '0')}"));
        return new AgentTranscriptTurn(
            runId,
            [new ChatMessage(ChatRole.User, "Question"), new ChatMessage(ChatRole.Assistant, "Answer")]);
    }
}
