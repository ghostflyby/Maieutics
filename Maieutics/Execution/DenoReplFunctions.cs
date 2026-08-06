using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

internal sealed class DenoReplFunctions
{
    private static readonly JsonSerializerOptions SerializerOptions =
        DenoReplJsonSerializerContext.Default.Options;

    private readonly DenoReplRegistry registry;

    public DenoReplFunctions(DenoReplRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Functions =
        [
            CreateFunction(
                (Func<string, AIFunctionArguments, string?, CancellationToken, ValueTask<DenoReplExecutionResult>>)
                ExecuteAsync,
                "repl_execute",
                "Executes TypeScript in a stateful Deno Jupyter REPL. console output and the final expression are " +
                "private reasoning results. Show rich output to the notebook user with " +
                "Deno.jupyter.display({ 'text/html': html, 'text/plain': fallback }, { raw: true }). For updates, " +
                "first display with { raw: true, display_id: id }, then display the replacement with " +
                "{ raw: true, display_id: id, update: true }. The property is display_id, not displayId, and every " +
                "rich MIME bundle should include a text/plain fallback."),
            CreateFunction(
                (Func<AIFunctionArguments, CancellationToken, ValueTask<DenoReplSessionResult>>)CreateAsync,
                "repl_create",
                "Creates and starts an additional isolated stateful Deno Jupyter REPL process."),
            CreateFunction(
                (Func<AIFunctionArguments, CancellationToken, ValueTask<DenoReplListResult>>)ListAsync,
                "repl_list",
                "Lists Deno REPL sessions owned by the current Agent session."),
            CreateFunction(
                (Func<AIFunctionArguments, string?, CancellationToken, ValueTask<DenoReplSessionResult>>)RestartAsync,
                "repl_restart",
                "Restarts a Deno REPL, preserving its logical ID while clearing all TypeScript state."),
            CreateFunction(
                (Func<AIFunctionArguments, string?, CancellationToken, ValueTask<DenoReplCloseResult>>)CloseAsync,
                "repl_close",
                "Closes and removes a Deno REPL session.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    private static AIFunction CreateFunction(Delegate method, string name, string description)
    {
        return AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = SerializerOptions
            });
    }

    [Description("Executes TypeScript in one stateful Deno Jupyter REPL.")]
    private async ValueTask<DenoReplExecutionResult> ExecuteAsync(
        [Description("The TypeScript or JavaScript source to execute.")]
        string code,
        AIFunctionArguments arguments,
        [Description("An opaque ID returned by repl_create. Omit to use the lazy default REPL.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new AgentToolException("repl_invalid_arguments", "code is required and cannot be empty.");

        return await registry.ExecuteAsync(
            AgentToolContext.GetRequired(arguments),
            code,
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Creates and starts an additional Deno Jupyter REPL process.")]
    private async ValueTask<DenoReplSessionResult> CreateAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        return await registry.CreateAsync(
            AgentToolContext.GetRequired(arguments).SessionId,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Lists Deno REPL sessions owned by the current Agent session.")]
    private ValueTask<DenoReplListResult> ListAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(registry.List(AgentToolContext.GetRequired(arguments).SessionId));
    }

    [Description("Restarts a Deno REPL and clears its runtime state.")]
    private async ValueTask<DenoReplSessionResult> RestartAsync(
        AIFunctionArguments arguments,
        [Description("An opaque REPL ID. Omit to restart the default REPL.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return await registry.RestartAsync(
            AgentToolContext.GetRequired(arguments).SessionId,
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Closes and removes a Deno REPL session.")]
    private async ValueTask<DenoReplCloseResult> CloseAsync(
        AIFunctionArguments arguments,
        [Description("An opaque REPL ID. Omit to close the default REPL.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return await registry.CloseAsync(
            AgentToolContext.GetRequired(arguments).SessionId,
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DenoReplSessionResult))]
[JsonSerializable(typeof(DenoReplListResult))]
[JsonSerializable(typeof(DenoReplCloseResult))]
[JsonSerializable(typeof(DenoReplExecutionResult))]
internal sealed partial class DenoReplJsonSerializerContext : JsonSerializerContext;