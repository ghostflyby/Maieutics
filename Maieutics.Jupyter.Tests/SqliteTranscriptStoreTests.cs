using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

/// <summary>Product tests for the SQLite Agent transcript store: durable round trips across
/// reopen, session metadata, and schema-version refusal (ADR 0009 metadata store, v1).</summary>
public sealed class SqliteTranscriptStoreTests : IDisposable
{
    private readonly string databaseDirectory;
    private readonly string databasePath;

    public SqliteTranscriptStoreTests()
    {
        databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-transcript-store-tests",
            Guid.NewGuid().ToString("N"));
        databasePath = Path.Combine(databaseDirectory, "history.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(databaseDirectory))
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void FamilyDatabasePathJoinsTheSessionDirectory()
    {
        var familyId = AgentSessionId.Create();
        var path = SqliteTranscriptStore.FamilyDatabasePath("/data/agent/sessions", familyId);
        path.Replace('\\', '/').Should().Be($"/data/agent/sessions/{familyId.Value.ToString("N")}/history.db");
    }

    [Fact]
    public void AppendedTurnsSurviveReopenWithCanonicalContent()
    {
        var sessionId = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        var first = Turn(
            sessionId,
            "a",
            identity,
            truncated: false,
            ("user", "Question one"),
            ("assistant", "Answer one"));
        var second = Turn(
            sessionId,
            "b",
            identity: null,
            truncated: true,
            ("user", "Question two"),
            ("assistant", "Answer two"));

        using (var store = new SqliteTranscriptStore(databasePath))
        {
            store.AppendTurn(sessionId, first);
            store.AppendTurn(sessionId, second);
        }

        using var reopened = new SqliteTranscriptStore(databasePath);
        var transcript = reopened.LoadTranscript(sessionId);
        transcript.Should().NotBeNull();
        transcript!.SessionId.Should().Be(sessionId);
        transcript.Turns.Should().HaveCount(2);
        transcript.Turns[0].RunId.Should().Be(first.RunId);
        transcript.Turns[0].ModelIdentity.Should().Be(identity);
        transcript.Turns[0].Truncated.Should().BeFalse();
        transcript.Turns[0].Messages.Select(message => message.Text)
            .Should().Equal("Question one", "Answer one");
        transcript.Turns[1].ModelIdentity.Should().BeNull();
        transcript.Turns[1].Truncated.Should().BeTrue();
        transcript.Turns[1].Messages.Select(message => message.Text)
            .Should().Equal("Question two", "Answer two");

        var sessions = reopened.ListSessions();
        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(sessionId);
        sessions[0].TurnCount.Should().Be(2);
        sessions[0].LastActivityAt.Should().BeOnOrAfter(sessions[0].CreatedAt);
    }

    [Fact]
    public void RetainsProviderReasoningContentAcrossTheStore()
    {
        var sessionId = AgentSessionId.Create();
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new TextReasoningContent("private reasoning summary"),
            new TextContent("public answer"),
        ]);
        var turn = new AgentTranscriptTurn(
            AgentRunId.Create(),
            [new ChatMessage(ChatRole.User, "ask"), assistant],
            modelIdentity: null);

        using var store = new SqliteTranscriptStore(databasePath);
        store.AppendTurn(sessionId, turn);
        var loaded = store.LoadTranscript(sessionId);

        loaded.Should().NotBeNull();
        var contents = loaded!.Turns[0].Messages[^1].Contents;
        contents.OfType<TextReasoningContent>().Should().ContainSingle().Which.Text.Should().Be("private reasoning summary");
        contents.OfType<TextContent>().Should().ContainSingle().Which.Text.Should().Be("public answer");
    }

    [Fact]
    public void UnknownSessionsLoadAsAbsent()
    {
        using var store = new SqliteTranscriptStore(databasePath);
        store.LoadTranscript(AgentSessionId.Create()).Should().BeNull();
        store.ListSessions().Should().BeEmpty();
    }

    [Fact]
    public void RefusesDatabasesWrittenByANewerSchema()
    {
        Directory.CreateDirectory(databaseDirectory);
        using (var newer = new SqliteConnection($"Data Source={databasePath}"))
        {
            newer.Open();
            using var bump = newer.CreateCommand();
            bump.CommandText = "PRAGMA user_version=99;";
            bump.ExecuteNonQuery();
        }

        var open = () => new SqliteTranscriptStore(databasePath);
        open.Should().Throw<InvalidOperationException>()
            .WithMessage("*newer build*");
    }

    private static AgentTranscriptTurn Turn(
        AgentSessionId sessionId,
        string runIdSuffix,
        AgentModelIdentity? identity,
        bool truncated,
        params (string Role, string Text)[] messages)
    {
        var chatMessages = messages.Select(pair => new ChatMessage(
            pair.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            pair.Text)).ToArray();
        return new AgentTranscriptTurn(
            new AgentRunId(RunGuid(runIdSuffix)),
            chatMessages,
            identity,
            truncated);
    }

    private static Guid RunGuid(string suffix) =>
        Guid.Parse($"00000000-0000-0000-0000-{suffix.PadLeft(12, '0')}");
}
