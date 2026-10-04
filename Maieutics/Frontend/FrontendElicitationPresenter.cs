using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Maieutics.Agent;
using Maieutics.Mcp;

namespace Maieutics.Frontend;

/// <summary>Publishes one session-scoped presentation frame onto the session's current run
/// stream. Implemented by <see cref="FrontendSessionService" />; the elicitation presenter
/// depends on this seam only, so attribution and frame delivery stay decoupled.</summary>
internal interface IFrontendSessionFramePublisher
{
    /// <summary>Publishes an unsequenced presentation frame on the session's latest run
    /// stream; false when the session has no live stream.</summary>
    bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data);
}

/// <summary>Shows MCP elicitation requests as <c>input.request</c> frames on the attributed
/// session's stream and completes them through the shared input endpoint (ADR 0029 decision
/// 3). Answers carry the terminal action plus a JSON object of field values for accept.
/// When the request carries a schema, a live <c>maieutics/form</c> model rides the comm
/// plane alongside (ADR 0038 stage 2): submitting the form answers the elicitation
/// directly; the input.request frame remains the fallback for comm-less frontends.</summary>
internal sealed class FrontendElicitationPresenter(
    IFrontendSessionFramePublisher publisher,
    FrontendFormModelHost formHost)
    : IMcpElicitationPresenter
{
    /// <summary>The maximum number of form fields derived from one elicitation schema.</summary>
    private const int MaxFormFields = 16;

    private readonly Lock gate = new();
    private readonly Dictionary<string, TaskCompletionSource<McpElicitationAnswer>> pending = [];
    private readonly Dictionary<string, FrontendFormModel> forms = [];

    public ValueTask<McpElicitationAnswer> PresentAsync(
        McpElicitationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = new AgentSessionId(Guid.ParseExact(request.SessionId, "N"));
        var requestId = $"elicit-{Guid.NewGuid().ToString("N")}";
        var completion = new TaskCompletionSource<McpElicitationAnswer>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending[requestId] = completion;
        }

        var published = publisher.TryPublishPresentation(
            sessionId,
            "input.request",
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest(
                    requestId,
                    request.Message,
                    request.Password,
                    request.Schema,
                    request.ServerId),
                FrontendJsonContext.Default.FrontendInputRequest));
        if (!published)
        {
            lock (gate)
            {
                pending.Remove(requestId);
            }

            // No live stream to present on (the run already settled): cancel, never hang.
            return ValueTask.FromResult(new McpElicitationAnswer("cancel", null));
        }

        return PresentWithFormAsync(sessionId, requestId, request, completion, cancellationToken);
    }

    private async ValueTask<McpElicitationAnswer> PresentWithFormAsync(
        AgentSessionId sessionId,
        string requestId,
        McpElicitationRequest request,
        TaskCompletionSource<McpElicitationAnswer> completion,
        CancellationToken cancellationToken)
    {
        var fields = DeriveFormFields(request);
        if (fields.Count > 0)
        {
            var model = await formHost.TryCreateFormAsync(
                sessionId,
                new FrontendUiFormState(
                    Title: request.Message,
                    Fields: fields,
                    SubmitLabel: "Submit",
                    CancelLabel: "Decline"),
                new FrontendFormHandlers(
                    OnSubmit: (values, _) =>
                    {
                        var content = JsonSerializer.Serialize(
                            (Dictionary<string, object?>)values,
                            FrontendJsonContext.Default.DictionaryStringObject);
                        Complete(requestId, "accept", content);
                        return ValueTask.CompletedTask;
                    },
                    OnCancel: _ =>
                    {
                        Complete(requestId, "decline", null);
                        return ValueTask.CompletedTask;
                    }),
                cancellationToken).ConfigureAwait(false);
            if (model is not null)
            {
                lock (gate)
                {
                    forms[requestId] = model;
                }
            }
        }

        return await WaitAsync(requestId, completion, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<McpElicitationAnswer> WaitAsync(
        string requestId,
        TaskCompletionSource<McpElicitationAnswer> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new McpElicitationAnswer("cancel", null);
        }
        finally
        {
            await CloseFormAsync(requestId).ConfigureAwait(false);
            lock (gate)
            {
                pending.Remove(requestId);
            }
        }
    }

    /// <summary>Derives bounded form fields from the elicitation schema (untrusted input):
    /// string/number/boolean/enum properties map to the form's field types; anything the
    /// form cannot express is skipped rather than guessed.</summary>
    private static IReadOnlyList<FrontendUiFormField> DeriveFormFields(McpElicitationRequest request)
    {
        if (request.Schema is not { ValueKind: JsonValueKind.Object } schema) return [];
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        HashSet<string>? required = null;
        if (schema.TryGetProperty("required", out var requiredList) &&
            requiredList.ValueKind == JsonValueKind.Array)
        {
            required = [];
            foreach (var entry in requiredList.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    required.Add(entry.GetString()!);
                }
            }
        }

        var fields = new List<FrontendUiFormField>();
        foreach (var property in properties.EnumerateObject())
        {
            if (fields.Count >= MaxFormFields) break;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = property.Value;
            definition.TryGetProperty("type", out var typeElement);
            var type = typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;

            definition.TryGetProperty("title", out var titleElement);
            var label = titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() : null;

            FrontendUiFormField? field = null;
            if (definition.TryGetProperty("enum", out var enumElement) &&
                enumElement.ValueKind == JsonValueKind.Array)
            {
                var choices = new List<FrontendUiFormChoice>();
                foreach (var option in enumElement.EnumerateArray())
                {
                    var text = option.ValueKind switch
                    {
                        JsonValueKind.String => option.GetString(),
                        JsonValueKind.Number => option.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => null,
                    };
                    if (text is not null) choices.Add(new FrontendUiFormChoice(text, text));
                }

                if (choices.Count > 0)
                {
                    field = new FrontendUiFormField(
                        property.Name,
                        label,
                        "choice",
                        choices,
                        required?.Contains(property.Name) == true);
                }
            }
            else
            {
                var formType = type switch
                {
                    "integer" or "number" => "number",
                    "boolean" => "boolean",
                    "string" => request.Password ? "password" : "text",
                    _ => null,
                };
                if (formType is not null)
                {
                    field = new FrontendUiFormField(
                        property.Name,
                        label,
                        formType,
                        null,
                        required?.Contains(property.Name) == true);
                }
            }

            if (field is not null) fields.Add(field);
        }

        return fields;
    }

    /// <summary>Completes a pending elicitation from the form path. The form itself is
    /// closed by <see cref="WaitAsync" />'s cleanup once the completion resumes.</summary>
    private void Complete(string requestId, string action, string? contentJson)
    {
        TaskCompletionSource<McpElicitationAnswer>? completion;
        lock (gate)
        {
            if (!pending.Remove(requestId, out completion)) return;
        }

        completion.TrySetResult(new McpElicitationAnswer(action, contentJson));
    }

    private ValueTask CloseFormAsync(string requestId)
    {
        FrontendFormModel? model;
        lock (gate)
        {
            if (!forms.Remove(requestId, out model)) return ValueTask.CompletedTask;
        }

        return model.DisposeAsync();
    }

    /// <summary>Completes a pending elicitation from the shared input endpoint. The action
    /// defaults to accept (a plain value answer); accept values must be a JSON object of
    /// field values.</summary>
    public bool TryCompleteInput(string requestId, string? action, string value)
    {
        TaskCompletionSource<McpElicitationAnswer>? completion;
        lock (gate)
        {
            if (!pending.Remove(requestId, out completion)) return false;
        }

        var normalizedAction =
            string.Equals(action, "decline", StringComparison.Ordinal) ? "decline" :
            string.Equals(action, "cancel", StringComparison.Ordinal) ? "cancel" :
            "accept";
        string? contentJson = null;
        if (normalizedAction == "accept")
        {
            // A schema'd form must be answered with a JSON object of field values; malformed
            // or non-object answers are client errors, downgraded to cancel, never an exception.
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    completion.TrySetResult(new McpElicitationAnswer("cancel", null));
                    return true;
                }

                contentJson = document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                completion.TrySetResult(new McpElicitationAnswer("cancel", null));
                return true;
            }
        }

        completion.TrySetResult(new McpElicitationAnswer(normalizedAction, contentJson));
        return true;
    }
}

/// <summary>Deferred composition wrapper: MaieuticsRuntimeConfiguration needs an
/// IMcpElicitationPresenter at construction time, but the real presenter depends on
/// FrontendSessionService, which depends back on the runtime configuration. Deferring the
/// resolution to first use breaks that construction cycle; every caller resolves lazily
/// per elicitation, long after composition has settled.</summary>
internal sealed class DeferredElicitationPresenter(IServiceProvider services) : IMcpElicitationPresenter
{
    public ValueTask<McpElicitationAnswer> PresentAsync(
        McpElicitationRequest request,
        CancellationToken cancellationToken)
    {
        return services.GetRequiredService<FrontendElicitationPresenter>()
            .PresentAsync(request, cancellationToken);
    }
}
