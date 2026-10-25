using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>The instruction-surface domain (ADR 0032): one kernel-internal permission kind with
/// physical-path patterns — the profile and app-default layers can finally express the migration
/// allow, the write domain never loosens the gate, a REPL child's direct Deno write meets the
/// same deny through the derived overlay, and the session override registry's child inheritance
/// is a spawn-time snapshot.</summary>
public sealed class InstructionSurfacePermissionTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create();

    public void Dispose()
    {
        _workspace.Dispose();
    }

    [Fact]
    public void TheInstructionsKindNeverRendersAsADenoFlag()
    {
        var policy = Policy(new PermissionKindRules
        {
            Allow = [Path.Combine(_workspace.Path, "AGENTS.md")]
        });

        var flags = DenoPermissionRenderer.Render(policy);

        Assert.DoesNotContain(flags, flag => flag.Contains("instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorkspaceProfileAllowAdmitsTheInstructionSurface()
    {
        var root = _workspace.Path;
        var profilePath = Path.Combine(root, "permissions.json");
        await File.WriteAllTextAsync(
            profilePath,
            $$"""{ "sets": { "default": { "instructions": { "allow": ["./AGENTS.md"] } } }, "default": "default" }""",
            TestContext.Current.CancellationToken);
        var acquirer = new PermissionPolicyAcquirer(
            () => new ProfileLayerSource(PermissionProfileLoader.Load(profilePath)),
            new NullVariableSource());
        var edit = new WorkspaceEditFunctions(CreateWorkspace(), acquirer: acquirer);

        // The profile layer resolves the relative pattern against its own directory, and the
        // gate judges the same physical address: the migration path is finally expressible.
        var failure = await Record.ExceptionAsync(() => InvokeWrite(edit, "workspace://local/AGENTS.md"));

        Assert.Null(failure);
    }

    [Fact]
    public void TheDerivedOverlayDeniesReplWritesToInstructionSurfacesTheRootWriteGrantCovers()
    {
        var root = _workspace.Path;
        Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", "x"));
        var agentsPath = Path.Combine(root, "AGENTS.md");
        File.WriteAllText(agentsPath, "instructions\n");
        File.WriteAllText(Path.Combine(root, ".agents", "skills", "x", "SKILL.md"), "skill\n");

        // The composed policy grants write of the whole workspace root and allows nothing in
        // the instructions domain — the overlay must deny every instruction surface.
        var composed = Policy(PermissionKindRules.Empty);
        var withOverlay = DenoReplPolicyBuilder.DeriveInstructionDenyOverlay(composed, root);

        Assert.False(DenoPermissionResolver.Resolve(withOverlay, "write", agentsPath).IsAllowed);
        Assert.False(DenoPermissionResolver.Resolve(
            withOverlay,
            "write",
            Path.Combine(root, ".agents", "skills", "x", "SKILL.md")).IsAllowed);
        Assert.True(DenoPermissionResolver.Resolve(withOverlay, "write", Path.Combine(root, "notes.md")).IsAllowed);
    }

    [Fact]
    public void AnInstructionsAllowKeepsTheSurfaceWritableUnderTheOverlay()
    {
        var root = _workspace.Path;
        var agentsPath = Path.Combine(root, "AGENTS.md");
        File.WriteAllText(agentsPath, "instructions\n");

        var composed = Policy(new PermissionKindRules { Allow = [agentsPath] });
        var withOverlay = DenoReplPolicyBuilder.DeriveInstructionDenyOverlay(composed, root);

        Assert.True(DenoPermissionResolver.Resolve(withOverlay, "write", agentsPath).IsAllowed);
    }

    [Fact]
    public async Task PermissionsCommandsSetAndClearTheSessionOverride()
    {
        var registry = new PermissionOverrideRegistry();
        var acquirer = new PermissionPolicyAcquirer(() => new RegistryLayerSource(registry), new NullVariableSource());
        var executor = new MaieuticsCommandExecutor(
            null, null, CreateWorkspace(), null, null, null, registry, acquirer);
        var session = AgentSessionId.Create();

        var denied = await executor.ExecuteAsync("%permissions deny run /usr/bin/curl", session, TestContext.Current.CancellationToken);
        Assert.Contains("run", denied.Markdown, StringComparison.Ordinal);
        if (registry.TryGet(session) is { } stored &&
            stored.Kinds.TryGetValue(PermissionKind.Run, out var rules))
        {
            Assert.Contains("/usr/bin/curl", rules.Deny);
        }
        else
        {
            Assert.Fail("the deny command stored a session override");
        }

        var current = await executor.ExecuteAsync("%permissions current", session, TestContext.Current.CancellationToken);
        Assert.Contains("/usr/bin/curl", current.Markdown, StringComparison.Ordinal);

        await executor.ExecuteAsync("%permissions clear", session, TestContext.Current.CancellationToken);
        Assert.Null(registry.TryGet(session));
    }

    [Fact]
    public async Task PermissionsCommandsRejectUnrepresentablePatternsLoudly()
    {
        var registry = new PermissionOverrideRegistry();
        var acquirer = new PermissionPolicyAcquirer(() => new RegistryLayerSource(registry), new NullVariableSource());
        var executor = new MaieuticsCommandExecutor(
            null, null, CreateWorkspace(), null, null, null, registry, acquirer);
        var session = AgentSessionId.Create();

        var star = await Assert.ThrowsAnyAsync<MaieuticsCommandException>(() =>
            executor.ExecuteAsync("%permissions allow net *", session, TestContext.Current.CancellationToken));
        Assert.Contains("'*'", star.Message, StringComparison.Ordinal);

        var unknownKind = await Assert.ThrowsAnyAsync<MaieuticsCommandException>(() =>
            executor.ExecuteAsync("%permissions allow nosuch x", session, TestContext.Current.CancellationToken));
        Assert.Contains("permission kind", unknownKind.Message, StringComparison.Ordinal);
        Assert.Null(registry.TryGet(session));
    }

    [Fact]
    public async Task PermissionsCommandsBindTheInstructionGate()
    {
        var registry = new PermissionOverrideRegistry();
        var acquirer = new PermissionPolicyAcquirer(() => new RegistryLayerSource(registry), new NullVariableSource());
        var executor = new MaieuticsCommandExecutor(
            null, null, CreateWorkspace(), null, null, null, registry, acquirer);
        var edit = new WorkspaceEditFunctions(CreateWorkspace(), acquirer: acquirer);
        var session = AgentSessionId.Create();
        var agentsPath = Path.Combine(_workspace.Path, "AGENTS.md");

        var forbidden = await Record.ExceptionAsync(() => InvokeWrite(edit, "workspace://local/AGENTS.md", session));
        Assert.Equal("instructions_write_forbidden", Assert.IsType<AgentToolException>(forbidden).Code);

        // The command language splits on whitespace with no quote handling, so the pattern
        // rides as one bare token (temporary roots carry no spaces).
        await executor.ExecuteAsync(
            $"%permissions allow instructions {agentsPath}",
            session,
            TestContext.Current.CancellationToken);
        Assert.Null(await Record.ExceptionAsync(() => InvokeWrite(edit, "workspace://local/AGENTS.md", session)));
    }

    [Fact]
    public void CommaAndStarNamedInstructionSurfacesDoNotBreakTheOverlayAndAreDenied()
    {
        // Workspace file names are untrusted: a surface literally named 'a,b' or '*' must still
        // be denyable — the derived overlay hand-merges kernel-generated paths past the store's
        // flag-grammar rejections (which exist for user-authored, renderer-joined patterns).
        var root = _workspace.Path;
        Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", "csv,export"));
        var commaPath = Path.Combine(root, ".agents", "skills", "csv,export", "SKILL.md");
        File.WriteAllText(commaPath, "x\n");
        var starPath = Path.Combine(root, ".agents", "star*");
        File.WriteAllText(starPath, "x\n");

        var withOverlay = DenoReplPolicyBuilder.DeriveInstructionDenyOverlay(Policy(PermissionKindRules.Empty), root);

        Assert.False(DenoPermissionResolver.Resolve(withOverlay, "write", commaPath).IsAllowed);
        Assert.False(DenoPermissionResolver.Resolve(withOverlay, "write", starPath).IsAllowed);
    }

    [Fact]
    public void AUserAuthoredInstructionsPatternMayCarryAComma()
    {
        // The instructions domain never renders as a Deno flag, so its user-authored patterns
        // keep their commas; a comma-named instruction file stays expressible in configuration.
        var build = () => PermissionLayerStore.Build(
            [new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [PermissionKind.Instructions] = new() { Allow = [Path.Combine(_workspace.Path, "a,b.md")] }
                }
            }],
            new VariableTable(new NullVariableSource()));

        Assert.Null(Record.Exception(build.Invoke));
    }

    [Fact]
    public void ALinkNamedAGENTSMDIsStillDeniedByTheOverlay()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Creating a symlink needs a privilege on Windows.");

        // Replacing AGENTS.md with a link must not slide the surface out of the deny set: the
        // walk yields link-named surfaces (denying the path is the point) even though it never
        // descends through links.
        var root = _workspace.Path;
        var agentsPath = Path.Combine(root, "AGENTS.md");
        File.WriteAllText(Path.Combine(root, "real.md"), "x\n");
        File.CreateSymbolicLink(agentsPath, Path.Combine(root, "real.md"));

        var withOverlay = DenoReplPolicyBuilder.DeriveInstructionDenyOverlay(Policy(PermissionKindRules.Empty), root);

        Assert.False(DenoPermissionResolver.Resolve(withOverlay, "write", agentsPath).IsAllowed);
    }

    [Fact]
    public void AnUnreadableSubtreeStaysResidualInsteadOfFailingTheOverlay()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Assert.Skip("The mode-based unreadable directory is a Unix stand-in.");
            return;
        }

        var root = _workspace.Path;
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "AGENTS.md"), "x\n");
        var original = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, System.IO.UnixFileMode.None);
        try
        {
            var withOverlay = DenoReplPolicyBuilder.DeriveInstructionDenyOverlay(
                Policy(PermissionKindRules.Empty),
                root);

            // The locked subtree is residual; the rest of the workspace still composes.
            Assert.NotNull(withOverlay);
        }
        finally
        {
            File.SetUnixFileMode(locked, original);
        }
    }

    [Fact]
    public async Task InstructionAllowsMatchPercentEncodedWorkspaceUris()
    {
        // The workspace URI percent-encodes its segments; the gate judges the decoded physical
        // address like every other gate, so an allow written with the real path matches.
        var root = _workspace.Path;
        var skillDir = Path.Combine(root, ".agents", "my skills");
        Directory.CreateDirectory(skillDir);
        var acquirer = new PermissionPolicyAcquirer(
            () => new FixedLayerSource(new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [PermissionKind.Instructions] = new() { Allow = [Path.Combine(skillDir, "SKILL.md")] }
                }
            }),
            new NullVariableSource());
        var edit = new WorkspaceEditFunctions(CreateWorkspace(), acquirer: acquirer);

        var failure = await Record.ExceptionAsync(() =>
            InvokeWrite(edit, "workspace://local/.agents/my%20skills/SKILL.md"));

        Assert.Null(failure);
    }

    [Fact]
    public async Task PermissionsCommandsRejectRelativePathPatternsLoudly()
    {
        var registry = new PermissionOverrideRegistry();
        var acquirer = new PermissionPolicyAcquirer(() => new RegistryLayerSource(registry), new NullVariableSource());
        var executor = new MaieuticsCommandExecutor(
            null, null, CreateWorkspace(), null, null, null, registry, acquirer);
        var session = AgentSessionId.Create();

        var failure = await Assert.ThrowsAnyAsync<MaieuticsCommandException>(() =>
            executor.ExecuteAsync("%permissions deny read src", session, TestContext.Current.CancellationToken));

        Assert.Contains("relative", failure.Message, StringComparison.Ordinal);
        Assert.Null(registry.TryGet(session));
    }

    // ---------- snapshot inheritance ----------

    [Fact]
    public void AChildInheritsTheParentOverrideCapturedAtSpawnTime()
    {
        var registry = new PermissionOverrideRegistry();
        var parent = AgentSessionId.Create();
        var deny = new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Run] = new() { Deny = ["/usr/bin/curl"] }
            }
        };

        registry.Set(parent, deny);
        var child = AgentSessionId.Create();
        registry.RegisterChildScope(child, parent);
        Assert.Equal(deny, registry.TryGet(child));

        // The snapshot is captured at spawn: a parent's later tightening does not reach an
        // already-spawned child (the capture-once rule, AGENTS.md invariant 19), and clearing
        // the parent's override leaves the child's captured deny in force.
        registry.Clear(parent);
        Assert.Equal(deny, registry.TryGet(child));

        // A child spawned after the clear inherits nothing.
        var later = AgentSessionId.Create();
        registry.RegisterChildScope(later, parent);
        Assert.Null(registry.TryGet(later));
    }

    [Fact]
    public void AChildSpawnedBeforeAParentDenyCarriesNoInheritedOverride()
    {
        var registry = new PermissionOverrideRegistry();
        var parent = AgentSessionId.Create();
        var child = AgentSessionId.Create();
        registry.RegisterChildScope(child, parent);

        var deny = new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Run] = new() { Deny = ["/usr/bin/curl"] }
            }
        };
        registry.Set(parent, deny);

        Assert.Null(registry.TryGet(child));
    }

    // ---------- helpers ----------

    private Workspace CreateWorkspace()
    {
        return Workspace.Create(_workspace.Path, _workspace.Path);
    }

    private EffectivePolicy Policy(PermissionKindRules instructions)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [PermissionKind.Instructions] = instructions,
                    [PermissionKind.Write] = new() { Allow = [_workspace.Path] }
                }
            }],
            new VariableTable(new NullVariableSource()));
    }

    private static async Task InvokeWrite(
        WorkspaceEditFunctions edit,
        string uri,
        AgentSessionId? session = null)
    {
        var write = edit.Functions.Single(static function => function.Name == "write_text");
        await write.InvokeAsync(new AIFunctionArguments(
                new Dictionary<string, object?> { ["uri"] = uri, ["content"] = "# hi" })
            {
                Context = new Dictionary<object, object?>
                {
                    [typeof(AgentToolContext)] = new AgentToolContext(
                        session ?? AgentSessionId.Create(),
                        AgentRunId.Create(),
                        AgentToolCallId.Create(),
                        static (_, _) => ValueTask.CompletedTask)
                }
            },
            TestContext.Current.CancellationToken);
    }

    private sealed class ProfileLayerSource(PermissionLayer profile) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => null;

        public PermissionLayer? WorkspaceProfile => profile;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class FixedLayerSource(PermissionLayer appDefaults) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => appDefaults;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class RegistryLayerSource(PermissionOverrideRegistry registry) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => null;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => registry.TryGet(sessionId);
    }

    private sealed class NullVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
