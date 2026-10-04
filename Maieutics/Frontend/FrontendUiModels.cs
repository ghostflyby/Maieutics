using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Maieutics.DenoRepl;

namespace Maieutics.Frontend;

/// <summary>Native view-family wire constants (ADR 0038).</summary>
internal static class FrontendUiWire
{
    /// <summary>The display mime every native view-family announcement rides.</summary>
    internal const string ViewMime = "application/vnd.maieutics.view+json";

    /// <summary>The comm target of the <c>maieutics/form</c> family.</summary>
    internal const string FormTarget = "maieutics.view/maieutics.form";

    internal const string FormFamily = "maieutics/form";

    internal const string FormVersion = "1.0";
}

/// <summary>One choice option of a <c>choice</c> form field.</summary>
internal sealed record FrontendUiFormChoice(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label);

/// <summary>One form field of the <c>maieutics/form</c> family state (mirrors the SDK dialect).</summary>
internal sealed record FrontendUiFormField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("type")] string Type = "text",
    [property: JsonPropertyName("choices")] IReadOnlyList<FrontendUiFormChoice>? Choices = null,
    [property: JsonPropertyName("required")] bool? Required = null,
    [property: JsonPropertyName("placeholder")] string? Placeholder = null);

/// <summary>The <c>maieutics/form</c> family state carried on comm_open and in the announcement.</summary>
internal sealed record FrontendUiFormState(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("fields")] IReadOnlyList<FrontendUiFormField>? Fields = null,
    [property: JsonPropertyName("submitLabel")] string? SubmitLabel = null,
    [property: JsonPropertyName("cancelLabel")] string? CancelLabel = null,
    [property: JsonPropertyName("values")] Dictionary<string, object?>? Values = null)
{
    /// <summary>The declared fields; an absent list reads as none.</summary>
    public IReadOnlyList<FrontendUiFormField> FieldList => Fields ?? [];
}

/// <summary>The announcement value inside the <see cref="FrontendUiWire.ViewMime" /> display member.</summary>
internal sealed record FrontendUiFormAnnouncement(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("viewFamily")] string ViewFamily,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("state")] FrontendUiFormState State);

/// <summary>The native uplink dialect: state deltas and one-shot events.</summary>
internal sealed record FrontendUiUplinkPayload(
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("state")] Dictionary<string, object?>? State = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null);

/// <summary>
///     Handler surface of one kernel-owned form model: submit/cancel arrive as one-shot
///     uplink events; <paramref name="Closed" /> fires when the frontend closes the comm.
/// </summary>
internal sealed record FrontendFormHandlers(
    Func<Dictionary<string, object?>, CancellationToken, ValueTask>? OnSubmit = null,
    Func<CancellationToken, ValueTask>? OnCancel = null,
    Action? Closed = null);

/// <summary>
///     One kernel-owned <c>maieutics/form</c> model (ADR 0038 stage 2): publishes the
///     native comm_open, announces itself as a display-mime presentation frame, routes
///     uplink events, and closes deterministically. State lives kernel-side; the
///     announcement embeds the display-time snapshot for snapshot restores.
/// </summary>
internal sealed class FrontendFormModel : IFrontendKernelCommOwner, IAsyncDisposable
{
    private readonly FrontendCommRouter router;
    private readonly AgentSessionId sessionId;
    private readonly FrontendUiFormState state;
    private readonly FrontendFormHandlers handlers;
    private readonly string commId = Guid.NewGuid().ToString("N");
    private readonly Lock gate = new();
    private bool disposed;

    private FrontendFormModel(
        FrontendCommRouter router,
        AgentSessionId sessionId,
        FrontendUiFormState state,
        FrontendFormHandlers handlers)
    {
        this.router = router;
        this.sessionId = sessionId;
        this.state = state;
        this.handlers = handlers;
    }

    internal string CommId => commId;

    internal FrontendUiFormState State => state;

    /// <summary>Uplink routing (kernel-owned): message frames decode through the native
    /// dialect, close releases the model. Unknown shapes are ignored (invariant 18).</summary>
    public async ValueTask OnUplinkAsync(ReplCommMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message.Kind)
        {
            case ReplCommKind.Message:
            {
                if (message.Data is not { ValueKind: JsonValueKind.Object } data) return;
                FrontendUiUplinkPayload? payload;
                try
                {
                    payload = message.Data.Value.Deserialize(FrontendJsonContext.Default.FrontendUiUplinkPayload);
                }
                catch (JsonException)
                {
                    return;
                }

                if (payload?.Method == "event" && payload.Name == "submit")
                {
                    var values = DecodeValues(payload.Payload);
                    if (handlers.OnSubmit is { } onSubmit)
                    {
                        await onSubmit(values, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (payload?.Method == "event" && payload.Name == "cancel")
                {
                    if (handlers.OnCancel is { } onCancel)
                    {
                        await onCancel(cancellationToken).ConfigureAwait(false);
                    }
                }

                return;
            }
            case ReplCommKind.Close:
                await DisposeAsync().ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Sync one state key to the frontend (the native update dialect).</summary>
    internal async ValueTask SyncAsync(string key, object? value, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (state.Values is { } values)
        {
            values[key] = value;
        }
        var update = JsonSerializer.SerializeToElement(
            new FrontendUiUplinkPayload(Method: "update", State: new Dictionary<string, object?> { [key] = value }),
            FrontendJsonContext.Default.FrontendUiUplinkPayload);
        await router.PublishFromKernelAsync(
            sessionId.ToString(),
            new ReplCommMessage(ReplCommKind.Message, commId, null, update, null, []),
            owner: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }

        handlers.Closed?.Invoke();
        await router.PublishFromKernelAsync(
            sessionId.ToString(),
            new ReplCommMessage(ReplCommKind.Close, commId, null, null, null, []),
            owner: null,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Creates the model: publishes the comm_open, then the display-mime
    /// announcement on the session's run stream. The form is abandoned (closed) when no
    /// live stream can carry the announcement.</summary>
    internal static async ValueTask<FrontendFormModel?> TryCreateAsync(
        FrontendCommRouter router,
        IFrontendSessionFramePublisher publisher,
        AgentSessionId sessionId,
        FrontendUiFormState state,
        FrontendFormHandlers handlers,
        CancellationToken cancellationToken)
    {
        var model = new FrontendFormModel(router, sessionId, state, handlers);
        var open = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["state"] = state },
            FrontendJsonContext.Default.DictionaryStringObject);
        await router.PublishFromKernelAsync(
            sessionId.ToString(),
            new ReplCommMessage(ReplCommKind.Open, model.commId, FrontendUiWire.FormTarget, open, null, []),
            owner: model,
            cancellationToken).ConfigureAwait(false);

        var announcement = new Dictionary<string, object?>
        {
            [FrontendUiWire.ViewMime] = new FrontendUiFormAnnouncement(
                model.commId,
                FrontendUiWire.FormFamily,
                FrontendUiWire.FormVersion,
                state),
            ["text/plain"] = string.IsNullOrWhiteSpace(state.Title) ? "form" : state.Title!,
        };
        var published = publisher.TryPublishPresentation(
            sessionId,
            "repl.display",
            JsonSerializer.SerializeToElement(
                announcement,
                FrontendJsonContext.Default.DictionaryStringObject));
        if (!published)
        {
            // No live run stream to announce on (the run already settled): close the
            // comm we opened and report no model — callers must not hang.
            await model.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        return model;
    }

    private static Dictionary<string, object?> DecodeValues(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element) return [];
        if (element.TryGetProperty("values", out var values) &&
            values.ValueKind == JsonValueKind.Object)
        {
            var decoded = new Dictionary<string, object?>();
            foreach (var property in values.EnumerateObject())
            {
                decoded[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var i) ? i : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => property.Value.Clone(),
                };
            }

            return decoded;
        }

        return [];
    }
}

/// <summary>
///     Kernel-side producer surface for native form models (ADR 0038 stage 2). Owns no
///     models itself — callers create via <see cref="FrontendFormModel.TryCreateAsync" />
///     and dispose deterministically; this host only centralizes the wiring.
/// </summary>
internal sealed class FrontendFormModelHost(FrontendCommRouter router, IFrontendSessionFramePublisher publisher)
{
    public ValueTask<FrontendFormModel?> TryCreateFormAsync(
        AgentSessionId sessionId,
        FrontendUiFormState state,
        FrontendFormHandlers handlers,
        CancellationToken cancellationToken) =>
        FrontendFormModel.TryCreateAsync(router, publisher, sessionId, state, handlers, cancellationToken);
}
