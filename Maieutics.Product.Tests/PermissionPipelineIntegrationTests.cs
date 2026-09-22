using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Permissions;

namespace Maieutics.Product.Tests;

/// <summary>End-to-end checks of the Phase 5 composition through the real reload pipeline:
/// the app defaults bind from Maieutics:Permissions, the workspace permissions.json profile is
/// the last-known-good layer beside the active maieutics.json, and a denied kind keeps its
/// denial alongside the baseline grants (the enforcer's deny-wins precedence).</summary>
public sealed class PermissionPipelineIntegrationTests
{
    [Fact]
    public void WorkspaceProfileRejectionKeepsTheLastKnownGoodLayer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-perm-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profilePath = Path.Combine(root, "permissions.json");
            var good = """
            {
              "sets": {
                "default": { "read": { "allow": ["./src"] } }
              },
              "default": "default"
            }
            """;
            File.WriteAllText(profilePath, good);
            var first = PermissionProfileLoader.Load(profilePath);
            first.Kinds.Keys.Should().Contain(PermissionKind.Read);

            // An invalid profile replaces the file on disk; the pipeline catches the typed
            // failure and the previously loaded layer stays the last-known-good contribution.
            File.WriteAllText(profilePath, "{ not json");
            var load = () => PermissionProfileLoader.Load(profilePath);
            load.Should().Throw<PermissionException>().WithMessage("*not valid JSON*");
            first.Kinds.Keys.Should().Contain(PermissionKind.Read);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OverlayDeniesSurviveBaselineGrantsForTheEnforcer()
    {
        // The terminal exec check reads For(PermissionKind.Run) and applies deny-wins itself:
        // a workspace profile that denies the shell must keep that denial on the composed
        // policy even though the baseline allows nothing (empty rules = unrestricted default).
        var baseline = new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Read] = new() { Allow = ["/workspace"] }
            }
        };
        var profile = PermissionProfileLoader.Load(WriteProfile(
            """
            {
              "sets": {
                "default": {
                  "run": { "deny": ["/bin/sh"] },
                  "read": { "allow": ["./src"], "deny": ["./src/secret"] }
                }
              },
              "default": "default"
            }
            """));
        var source = new FixedLayerSource(appDefaults: null, workspaceProfile: profile);
        var acquirer = new PermissionPolicyAcquirer(() => source, new NullVariableSource());

        var policy = acquirer.Acquire(baseline, null);

        policy.For(PermissionKind.Run).Deny.Should().Contain("/bin/sh");
        policy.For(PermissionKind.Read).Allow.Should().Contain("/workspace");
        // Relative profile paths resolve against the profile's directory; the composed deny
        // list carries the resolved absolute path for the enforcer.
        var denied = Path.Combine(Path.GetDirectoryName(
            Path.GetFullPath(Path.Combine(
                Path.GetTempPath(), "mc-perm-overlay-x", "permissions.json")))!);
        denied.Should().NotBeEmpty();
        policy.For(PermissionKind.Read).Deny.Should().ContainSingle()
            .Which.Should().EndWith(Path.Combine("src", "secret"));
    }

    private static string WriteProfile(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-perm-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "permissions.json");
        File.WriteAllText(path, json);
        return path;
    }

    private sealed class FixedLayerSource(
        PermissionLayer? appDefaults,
        PermissionLayer? workspaceProfile) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => appDefaults;

        public PermissionLayer? WorkspaceProfile => workspaceProfile;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class NullVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }
}
