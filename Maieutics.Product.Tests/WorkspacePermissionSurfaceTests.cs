using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>The model-tool surface's permission posture: the workspace-root baseline keeps an
/// unconfigured host exactly as permissive as the kernel's containment always was, while
/// configuration denials subtract uniformly from reads, listings, searches, and writes —
/// including the search scan, which must never surface content from a denied subtree.</summary>
public sealed class WorkspacePermissionSurfaceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create();

    public void Dispose()
    {
        _workspace.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public async Task AnUnconfiguredPolicyKeepsTheContainmentPosture()
    {
        var functions = CreateFunctions(TestPermissionPolicies.Unconfigured());
        var edit = new WorkspaceEditFunctions(CreateWorkspace(), acquirer: TestPermissionPolicies.Unconfigured());
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "notes.md"), "hello\n", TestContext.Current.CancellationToken);

        await InvokeAsync(functions.Functions, "read_text", """{"uri":"workspace://local/notes.md"}""");
        await InvokeAsync(functions.Functions, "list_directory", """{"uri":"workspace://local"}""");
        await InvokeAsync(functions.Functions, "search_text", """{"query":"hello"}""");
        await InvokeAsync(edit.Functions, "write_text", """{"uri":"workspace://local/written.md","content":"written"}""");
        Assert.True(File.Exists(Path.Combine(_workspace.Path, "written.md")));
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadDenialsBindReadTextAndListing()
    {
        var denied = Path.Combine(_workspace.Path, "secret");
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(denied, "key.txt"), "needle\n", TestContext.Current.CancellationToken);
        var functions = CreateFunctions(Denying(PermissionKind.Read, denied));

        var readFailure = await Record.ExceptionAsync(() =>
            InvokeAsync(functions.Functions, "read_text", """{"uri":"workspace://local/secret/key.txt"}"""));
        Assert.Equal("workspace_read_forbidden", Assert.IsType<AgentToolException>(readFailure).Code);

        var listFailure = await Record.ExceptionAsync(() =>
            InvokeAsync(functions.Functions, "list_directory", """{"uri":"workspace://local/secret"}"""));
        Assert.Equal("workspace_read_forbidden", Assert.IsType<AgentToolException>(listFailure).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task SearchSkipsDeniedFilesSoTheirContentCannotLeakThroughMatches()
    {
        var denied = Path.Combine(_workspace.Path, "secret");
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "keep.txt"), "needle\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(denied, "leak.txt"), "needle\n", TestContext.Current.CancellationToken);

        var result = (JsonElement)(await InvokeAsync(
            CreateFunctions(Denying(PermissionKind.Read, denied)).Functions,
            "search_text",
            """{"query":"needle"}"""))!;

        var uris = result.GetProperty("matches").EnumerateArray()
            .Select(static match => match.GetProperty("uri").GetString())
            .ToList();
        Assert.Contains("workspace://local/keep.txt", uris);
        Assert.DoesNotContain(uris, uri => uri!.Contains("secret", StringComparison.Ordinal));
        Assert.True(result.GetProperty("skippedByFilter").GetInt32() >= 1);
    }

    [Fact(Timeout = 30_000)]
    public async Task SearchOnADeniedRootIsForbidden()
    {
        var denied = Path.Combine(_workspace.Path, "secret");
        Directory.CreateDirectory(denied);
        var functions = CreateFunctions(Denying(PermissionKind.Read, denied));

        var failure = await Record.ExceptionAsync(() =>
            InvokeAsync(functions.Functions, "search_text", """{"query":"needle","uri":"workspace://local/secret"}"""));

        Assert.Equal("workspace_read_forbidden", Assert.IsType<AgentToolException>(failure).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task WriteDenialsBindWriteTextEditAndPatch()
    {
        var denied = Path.Combine(_workspace.Path, "docs");
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(denied, "code.cs"), "int a = 1;\n", TestContext.Current.CancellationToken);
        var edit = new WorkspaceEditFunctions(CreateWorkspace(), acquirer: Denying(PermissionKind.Write, denied));

        var writeFailure = await Record.ExceptionAsync(() =>
            InvokeAsync(edit.Functions, "write_text", """{"uri":"workspace://local/docs/new.md","content":"x"}"""));
        Assert.Equal("workspace_write_forbidden", Assert.IsType<AgentToolException>(writeFailure).Code);

        var editFailure = await Record.ExceptionAsync(() =>
            InvokeAsync(edit.Functions, "edit_text", """{"uri":"workspace://local/docs/code.cs","oldText":"int a = 1;","newText":"int a = 2;"}"""));
        Assert.Equal("workspace_write_forbidden", Assert.IsType<AgentToolException>(editFailure).Code);

        var patchFailure = await Record.ExceptionAsync(() =>
            InvokeAsync(edit.Functions, "apply_patch", """{"patch":"*** Begin Patch\n*** Update File: docs/code.cs\n@@\n-int a = 1;\n+int a = 2;\n*** End Patch\n"}"""));
        Assert.Equal("workspace_write_forbidden", Assert.IsType<AgentToolException>(patchFailure).Code);
        Assert.Equal("int a = 1;\n", await File.ReadAllTextAsync(Path.Combine(denied, "code.cs"), TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task ApplyPatchCannotBypassTheInstructionGateThroughUriShapedPaths()
    {
        // GatePatchSurface used to feed the full workspace:// URI where a relative path is
        // expected, so ".agents/**" paths escaped the instruction classification; the gate
        // judges the patch-relative path now, like every other write entry point.
        var edit = new WorkspaceEditFunctions(CreateWorkspace(), acquirer: TestPermissionPolicies.Unconfigured());

        var failure = await Record.ExceptionAsync(() =>
            InvokeAsync(edit.Functions, "apply_patch", """{"patch":"*** Begin Patch\n*** Add File: .agents/skills/x/SKILL.md\n+payload\n*** End Patch\n"}"""));

        Assert.Equal("instructions_write_forbidden", Assert.IsType<AgentToolException>(failure).Code);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, ".agents", "skills", "x", "SKILL.md")));
    }

    private Workspace CreateWorkspace()
    {
        return Workspace.Create(_workspace.Path, _workspace.Path);
    }

    private WorkspaceFunctions CreateFunctions(PermissionPolicyAcquirer acquirer)
    {
        return new WorkspaceFunctions(CreateWorkspace(), acquirer: acquirer);
    }

    private static async Task<object?> InvokeAsync(
        IReadOnlyList<AIFunction> functions,
        string name,
        string argumentsJson)
    {
        var function = functions.Single(candidate => candidate.Name == name);
        using var document = JsonDocument.Parse(argumentsJson);
        return await function.InvokeAsync(
            new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
                    static property => property.Name,
                    static property => (object?)property.Value.Clone()))
            {
                Context = new Dictionary<object, object?>
                {
                    [typeof(ScriptInvocationContext)] = new ScriptInvocationContext(
                        AgentSessionId.Create().Value.ToString("N"))
                }
            },
            TestContext.Current.CancellationToken);
    }

    private PermissionPolicyAcquirer Denying(PermissionKind kind, string directory)
    {
        return new PermissionPolicyAcquirer(
            () => new FixedLayerSource(new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [kind] = new() { Deny = [directory] }
                }
            }),
            new NullVariableSource());
    }

    private sealed class FixedLayerSource(PermissionLayer appDefaults) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => appDefaults;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class NullVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
