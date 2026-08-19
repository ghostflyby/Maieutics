using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

/// <summary>The model-facing terminal tools. The screen stays structured until an output adapter renders it;
/// today the frame is delivered to the model as JSON and is never pushed to the notebook.</summary>
internal sealed class TerminalFunctions
{
    private const int MaximumFrameCharacters = 1_048_576;

    private static readonly JsonSerializerOptions SerializerOptions =
        TerminalJsonSerializerContext.Default.Options;

    private readonly TerminalRegistry registry;

    public TerminalFunctions(TerminalRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Functions =
        [
            CreateFunction(
                (Func<string, AIFunctionArguments, bool?, int?, string?, CancellationToken, ValueTask<TerminalInputResult>>)
                InputAsync,
                "terminal_input",
                "Sends a batch of input lines to the interactive terminal. Each line is either 't <text>' " +
                "for raw text (no escape processing) or 'k <keys>' for key sequences in vim notation, for " +
                "example k <Esc>, k 5<Down>, or k <C-c>. The keys include <CR>, <Esc>, <Tab>, <S-Tab>, <BS>, " +
                "<Del>, <Space>, <Up>, <Down>, <Left>, <Right>, <Home>, <End>, <PageUp>, <PageDown>, <F1>-<F12>, " +
                "C-<letter> controls such as <C-c> and <C-d>, and M-<letter>/A-<letter> meta keys. To type " +
                "ordinary characters use 't ' lines; to enter ':' in a vim command, send k <Esc> then t :w " +
                "then k <CR> separately. Returns the screen rows that changed since the last frame, plus the " +
                "cursor and whether the program is drawing the alternate screen (vim, less, or a full-screen " +
                "UI). The first call of a session returns the full screen; pass full=true to force a full snapshot."),
            CreateFunction(
                (Func<string, AIFunctionArguments, string[]?, int?, bool?, int?, CancellationToken, ValueTask<TerminalRunResult>>)
                RunAsync,
                "terminal_run",
                "Starts a PTY session running an executable with arguments and returns its screen. Without " +
                "timeout this creates a persistent interactive session (state 'idle'); pass timeout to run " +
                "the program as a one-shot command: state 'completed' with the exitCode and final frame when " +
                "it finishes in time, or state 'running' with sessionId as a live handle to poll with " +
                "terminal_snapshot, feed input with terminal_input, interrupt with terminal_interrupt, or " +
                "close with terminal_close. The one-shot session reports 'completed' after the program exits."),
            CreateFunction(
                (Func<AIFunctionArguments, bool?, int?, string?, CancellationToken, ValueTask<TerminalSnapshotResult>>)
                SnapshotAsync,
                "terminal_snapshot",
                "Returns the current terminal screen as the rows that changed since the last frame. Pass " +
                "full=true to get the complete screen. Use this after running commands that print more than " +
                "one screenful, or to re-read the screen after a delay. Requires an existing session: the " +
                "default session is created by the first terminal_input or terminal_paste."),
            CreateFunction(
                (Func<string, AIFunctionArguments, bool?, int?, string?, CancellationToken, ValueTask<TerminalPasteResult>>)
                PasteAsync,
                "terminal_paste",
                "Pastes multi-line text into the terminal. When the running program has enabled bracketed " +
                "paste (as vim and modern line editors do), the text is wrapped in bracketed-paste markers so " +
                "it is inserted literally without auto-indent mangling or being treated as key presses."),
            CreateFunction(
                (Func<AIFunctionArguments, string?, CancellationToken, ValueTask<TerminalCloseResult>>)CloseAsync,
                "terminal_close",
                "Closes a terminal session, gracefully closing the program and then force-killing it if it " +
                "does not exit. Omit sessionId to close the default session."),
            CreateFunction(
                (Func<AIFunctionArguments, CancellationToken, ValueTask<TerminalInfo[]>>)ListAsync,
                "terminal_list",
                "Lists the terminal sessions owned by the current Agent session as an array of session " +
                "infos, each with sessionId, state, kind, and exitCode."),
            CreateFunction(
                (Func<AIFunctionArguments, bool?, int?, string?, CancellationToken, ValueTask<TerminalInterruptResult>>)
                InterruptAsync,
                "terminal_interrupt",
                "Sends the terminal interrupt byte (Ctrl-C) to the running foreground program. Returns the " +
                "screen after the interrupt settles. Requires an existing session; the default session is " +
                "created by the first terminal_input or terminal_paste.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    private static void ValidateSnapshotRequest(int? maxCharacters)
    {
        if (maxCharacters is { } max && max is < 1 or > MaximumFrameCharacters)
            throw new AgentToolException(
                "terminal_invalid_arguments",
                $"maxCharacters must be between 1 and {MaximumFrameCharacters}.");
    }

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

    [Description("Sends a batch of input lines to the interactive terminal.")]
    private async ValueTask<TerminalInputResult> InputAsync(
        [Description("Lines starting with 't ' (raw text) or 'k ' (vim-notation key sequences).")]
        string input,
        AIFunctionArguments arguments,
        [Description("Whether to return the full screen instead of only the changed rows. Defaults to false.")]
        bool? full = null,
        [Description("The maximum characters the frame may carry, from 1 through 1048576. Defaults to the session limit.")]
        int? maxCharacters = null,
        [Description("An opaque session ID returned by terminal_run. Omit to use the lazy default session.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(input))
            throw new AgentToolException("terminal_invalid_arguments", "input is required and cannot be empty.");

        ValidateSnapshotRequest(maxCharacters);
        var batch = TerminalInputBatchParser.Parse(input);
        return await registry.ExecuteAsync(
            AgentToolContext.GetRequired(arguments),
            batch,
            new TerminalSnapshotRequest(full, maxCharacters),
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Starts a PTY session running an executable with arguments.")]
    private async ValueTask<TerminalRunResult> RunAsync(
        [Description("The executable to launch; resolved through PATH when it has no path separator.")]
        string executable,
        AIFunctionArguments arguments,
        [Description("The command-line arguments passed to the executable. Defaults to none.")]
        string[]? argumentsList = null,
        [Description("The deadline in seconds, from 1 through 600. Omit to create a persistent interactive session.")]
        int? timeout = null,
        [Description("Whether to return the full screen instead of only the changed rows. Defaults to false.")]
        bool? full = null,
        [Description("The maximum characters the frame may carry, from 1 through 1048576. Defaults to the session limit.")]
        int? maxCharacters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(executable))
            throw new AgentToolException("terminal_invalid_arguments", "executable is required and cannot be empty.");

        ValidateSnapshotRequest(maxCharacters);
        if (timeout is { } requested && requested is < 1 or > 600)
            throw new AgentToolException(
                "terminal_invalid_arguments",
                "timeout must be between 1 and 600 seconds.");

        return await registry.RunOnceAsync(
            AgentToolContext.GetRequired(arguments).SessionId,
            executable,
            argumentsList ?? [],
            timeout is { } deadline
                ? TimeSpan.FromSeconds(deadline)
                : null,
            new TerminalSnapshotRequest(full, maxCharacters),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Returns the current terminal screen.")]
    private ValueTask<TerminalSnapshotResult> SnapshotAsync(
        AIFunctionArguments arguments,
        [Description("Whether to return the full screen instead of only the changed rows. Defaults to false.")]
        bool? full = null,
        [Description("The maximum characters the frame may carry, from 1 through 1048576. Defaults to the session limit.")]
        int? maxCharacters = null,
        [Description("An opaque session ID returned by terminal_run. Omit to use the default session.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateSnapshotRequest(maxCharacters);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(registry.Snapshot(
                AgentToolContext.GetRequired(arguments).SessionId,
                new TerminalSnapshotRequest(full, maxCharacters),
                sessionId));
        }
        catch (Exception exception)
        {
            return ValueTask.FromException<TerminalSnapshotResult>(exception);
        }
    }

    [Description("Pastes multi-line text into the terminal.")]
    private async ValueTask<TerminalPasteResult> PasteAsync(
        [Description("The text to paste. May contain newlines; must not contain escape bytes.")]
        string text,
        AIFunctionArguments arguments,
        [Description("Whether to return the full screen instead of only the changed rows. Defaults to false.")]
        bool? full = null,
        [Description("The maximum characters the frame may carry, from 1 through 1048576. Defaults to the session limit.")]
        int? maxCharacters = null,
        [Description("An opaque session ID returned by terminal_run. Omit to use the lazy default session.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new AgentToolException("terminal_invalid_arguments", "text is required and cannot be empty.");

        ValidateSnapshotRequest(maxCharacters);
        return await registry.PasteAsync(
            AgentToolContext.GetRequired(arguments),
            text,
            new TerminalSnapshotRequest(full, maxCharacters),
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Closes a terminal session.")]
    private async ValueTask<TerminalCloseResult> CloseAsync(
        AIFunctionArguments arguments,
        [Description("An opaque session ID returned by terminal_run. Omit to close the default session.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return await registry.CloseAsync(
            AgentToolContext.GetRequired(arguments).SessionId,
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Lists terminal sessions owned by the current Agent session.")]
    private ValueTask<TerminalInfo[]> ListAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(registry.List(AgentToolContext.GetRequired(arguments).SessionId));
    }

    [Description("Sends the terminal interrupt byte (Ctrl-C) to the foreground program.")]
    private async ValueTask<TerminalInterruptResult> InterruptAsync(
        AIFunctionArguments arguments,
        [Description("Whether to return the full screen instead of only the changed rows. Defaults to false.")]
        bool? full = null,
        [Description("The maximum characters the frame may carry, from 1 through 1048576. Defaults to the session limit.")]
        int? maxCharacters = null,
        [Description("An opaque session ID returned by terminal_run. Omit to use the default session.")]
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshotRequest(maxCharacters);
        return await registry.InterruptAsync(
            AgentToolContext.GetRequired(arguments).SessionId,
            new TerminalSnapshotRequest(full, maxCharacters),
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(TerminalInfo))]
[JsonSerializable(typeof(TerminalInfo[]))]
[JsonSerializable(typeof(TerminalCloseResult))]
[JsonSerializable(typeof(TerminalCursor))]
[JsonSerializable(typeof(TerminalScreenRow))]
[JsonSerializable(typeof(TerminalFrame))]
[JsonSerializable(typeof(TerminalSnapshotResult))]
[JsonSerializable(typeof(TerminalInputResult))]
[JsonSerializable(typeof(TerminalPasteResult))]
[JsonSerializable(typeof(TerminalInterruptResult))]
[JsonSerializable(typeof(TerminalRunResult))]
internal sealed partial class TerminalJsonSerializerContext : JsonSerializerContext;
