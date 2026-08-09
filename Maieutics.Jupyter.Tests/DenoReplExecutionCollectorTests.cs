using System.Text.Json;
using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoReplExecutionCollectorTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyMetadata =
        new Dictionary<string, JsonElement>();

    [Fact]
    public async Task OutputTypesProduceSeparateModelAndNotebookProjections()
    {
        var requestId = JupyterMessageId.Create();
        var innerDisplayId = new JupyterDisplayId("inner");
        var outputs = new JupyterOutput[]
        {
            new JupyterExecuteInputOutput(requestId, "code", 7),
            new JupyterExecutionStatusChanged(requestId, JupyterKernelState.Busy),
            new JupyterStdout(requestId, "private stdout"),
            new JupyterDisplayOutput(requestId, TextBundle("visible display"), EmptyMetadata)
            {
                DisplayId = innerDisplayId
            },
            new JupyterDisplayUpdateOutput(
                requestId,
                TextBundle("visible update"),
                EmptyMetadata,
                JupyterDisplayTransient.Create(innerDisplayId),
                innerDisplayId),
            new JupyterMalformedOutput(requestId, "update_display_data", "missing_display_id"),
            new JupyterClearOutput(requestId, true),
            new JupyterStderr(requestId, "shared stderr"),
            new JupyterExecuteResultOutput(requestId, JsonBundle(42), EmptyMetadata, 7),
            new JupyterInputRequest(requestId, JupyterMessageId.Create(), "Name: ", false),
            new JupyterExecutionError(requestId, "TypeError", "shared error", ["frame"])
        };
        var execution = new TestExecution(
            requestId,
            outputs,
            new JupyterExecuteReply("error", 7, ErrorName: "TypeError", ErrorValue: "shared error"));
        var sink = new RecordingPresentationSink();
        var outerDisplayId = new JupyterDisplayId("outer");
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            _ => outerDisplayId,
            _ => outerDisplayId);

        var result = await collector.ConsumeAsync(execution, TestContext.Current.CancellationToken);

        result.ExecutionStatus.Should().Be("error");
        result.Outputs.Select(static output => output.Kind).Should()
            .Equal("stdout", "stderr", "result", "error");
        result.Outputs.Single(output => output.Kind == "stdout").Text.Should().Be("private stdout");
        var resultValue = result.Outputs.Single(output => output.Kind == "result").Value
                          ?? throw new InvalidOperationException("The result execution has no value.");
        resultValue.GetInt32().Should().Be(42);
        JsonSerializer.Serialize(result, DenoReplJsonSerializerContext.Default.DenoReplExecutionResult)
            .Should().NotContain("visible display").And.NotContain("visible update");
        result.Presentation.Should().Be(new DenoReplPresentationResult(1, 1, 1, 1));
        sink.Displays.Should().ContainSingle().Which.Should().Be("visible display");
        sink.Updates.Should().ContainSingle().Which.Should().Be((outerDisplayId, "visible update"));
        sink.Clears.Should().Equal(true);
        sink.Stderr.Should().Equal("shared stderr");
        sink.Errors.Should().ContainSingle().Which.Should().Be(("TypeError", "shared error"));
        execution.InputReplies.Should().ContainSingle().Which.Should().Be("Ada");
    }

    [Fact]
    public async Task ModelTruncationStillDrainsAndPublishesLaterDisplay()
    {
        var requestId = JupyterMessageId.Create();
        var outputs = Enumerable.Range(0, 20)
            .Select(_ => (JupyterOutput)new JupyterStdout(requestId, new string('x', 128)))
            .Append(new JupyterDisplayOutput(requestId, TextBundle("after truncation"), EmptyMetadata))
            .ToArray();
        var execution = new TestExecution(requestId, outputs, new JupyterExecuteReply("ok", 1));
        var sink = new RecordingPresentationSink();
        var options = new DenoReplOptions { MaxModelOutputBytes = 256 };
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            options,
            sink,
            static id => id,
            static id => id);

        var result = await collector.ConsumeAsync(execution, TestContext.Current.CancellationToken);

        result.Truncated.Should().BeTrue();
        result.OmittedBytes.Should().BeGreaterThan(0);
        execution.ObservedOutputCount.Should().Be(outputs.Length);
        sink.Displays.Should().Equal("after truncation");
    }

    [Fact]
    public async Task OversizedJsonResultFallsBackToBoundedTextPlain()
    {
        var requestId = JupyterMessageId.Create();
        var bundle = new MimeBundle(new Dictionary<string, JsonElement>
        {
            ["application/json"] = JsonSerializer.SerializeToElement(new string('x', 1024)),
            ["text/plain"] = JsonSerializer.SerializeToElement("fallback")
        });
        var execution = new TestExecution(
            requestId,
            [new JupyterExecuteResultOutput(requestId, bundle, EmptyMetadata, 1)],
            new JupyterExecuteReply("ok", 1));
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxModelOutputBytes = 256 },
            new RecordingPresentationSink(),
            static id => id,
            static id => id);

        var result = await collector.ConsumeAsync(execution, TestContext.Current.CancellationToken);

        result.Outputs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = "result",
            Text = "fallback",
            MediaType = "text/plain"
        });
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task BinaryOnlyResultReportsMediaTypesWithoutCopyingPayload()
    {
        var requestId = JupyterMessageId.Create();
        const string payload = "aGVsbG8=";
        var bundle = new MimeBundle(new Dictionary<string, JsonElement>
        {
            ["image/png"] = JsonSerializer.SerializeToElement(payload),
            ["application/octet-stream"] = JsonSerializer.SerializeToElement(payload)
        });
        var execution = new TestExecution(
            requestId,
            [new JupyterExecuteResultOutput(requestId, bundle, EmptyMetadata, 1)],
            new JupyterExecuteReply("ok", 1));
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            new RecordingPresentationSink(),
            static id => id,
            static id => id);

        var result = await collector.ConsumeAsync(execution, TestContext.Current.CancellationToken);

        result.Outputs.Should().ContainSingle().Which.MediaTypes.Should()
            .Equal("application/octet-stream", "image/png");
        JsonSerializer.Serialize(result, DenoReplJsonSerializerContext.Default.DenoReplExecutionResult)
            .Should().NotContain(payload);
    }

    private static MimeBundle TextBundle(string text)
    {
        return new MimeBundle(new Dictionary<string, JsonElement>
        {
            ["text/plain"] = JsonSerializer.SerializeToElement(text)
        });
    }

    private static MimeBundle JsonBundle(int value)
    {
        return new MimeBundle(new Dictionary<string, JsonElement>
        {
            ["application/json"] = JsonSerializer.SerializeToElement(value)
        });
    }

    private sealed class TestExecution(
        JupyterMessageId requestId,
        IReadOnlyList<JupyterOutput> outputs,
        JupyterExecuteReply reply) : IJupyterExecution
    {
        public List<string> InputReplies { get; } = [];

        public int ObservedOutputCount { get; private set; }
        public JupyterMessageId RequestId { get; } = requestId;

        public IAsyncEnumerable<JupyterOutput> Outputs => ReadOutputsAsync();

        public Task<JupyterExecutionResult> Completion { get; } = Task.FromResult(
            new JupyterExecutionResult(
                reply,
                JupyterMessage.Create(
                    "execute_reply",
                    reply,
                    JupyterJsonContext.Default.JupyterExecuteReply,
                    new JupyterSessionIdentity("test", "tester"))));

        public Task ReplyInputAsync(
            JupyterInputRequest request,
            string value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputReplies.Add(value);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<JupyterOutput> ReadOutputsAsync()
        {
            await Task.Yield();
            foreach (var output in outputs)
            {
                ObservedOutputCount++;
                yield return output;
            }
        }
    }

    private sealed class RecordingPresentationSink : IDenoReplPresentationSink
    {
        public List<string> Displays { get; } = [];

        public List<(JupyterDisplayId Id, string Text)> Updates { get; } = [];

        public List<bool> Clears { get; } = [];

        public List<string> Stderr { get; } = [];

        public List<(string Name, string Value)> Errors { get; } = [];

        public ValueTask DisplayAsync(
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            Displays.Add(data.Data["text/plain"].GetString() ?? string.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
            MimeBundle data,
            JupyterDisplayId displayId,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            Displays.Add(data.Data["text/plain"].GetString() ?? string.Empty);
            return ValueTask.FromResult(displayId);
        }

        public ValueTask UpdateDisplayAsync(
            JupyterDisplayId displayId,
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            Updates.Add((displayId, data.Data["text/plain"].GetString() ?? string.Empty));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken)
        {
            Clears.Add(wait);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken)
        {
            Stderr.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishErrorAsync(
            string name,
            string value,
            IReadOnlyList<string> traceback,
            CancellationToken cancellationToken)
        {
            Errors.Add((name, value));
            return ValueTask.CompletedTask;
        }

        public Task<string> RequestInputAsync(
            string prompt,
            bool password,
            CancellationToken cancellationToken)
        {
            return Task.FromResult("Ada");
        }
    }
}
