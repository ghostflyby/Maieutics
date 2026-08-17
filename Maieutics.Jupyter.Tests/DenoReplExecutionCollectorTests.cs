using System.Text.Json;
using FluentAssertions;
using Maieutics.DenoRepl;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoReplExecutionCollectorTests
{
    [Fact(Timeout = 15_000)]
    public async Task OrderedEvalEventsProduceSeparateModelAndNotebookProjections()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var sink = new RecordingPresentationSink { InputReply = "Ada" };
        var collector = new DenoReplExecutionCollector(
            "default",
            3,
            new DenoReplOptions(),
            sink,
            []);
        var display = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = "visible display"
        });
        var update = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = "visible update"
        });

        execution.Publish(new ReplEvalConsoleEvent("execution-1", 1, "stdout", "private stdout"));
        execution.Publish(new ReplEvalDisplayEvent(
            "execution-1", 2, false, "inner", display, null));
        execution.Publish(new ReplEvalDisplayEvent(
            "execution-1", 3, true, "inner", update, null));
        execution.Publish(new ReplEvalDisplayEvent(
            "execution-1", 4, true, "missing", update, null));
        execution.Publish(new ReplEvalClearOutputEvent("execution-1", 5, true));
        execution.Publish(new ReplEvalConsoleEvent("execution-1", 6, "stderr", "shared stderr"));
        var input = new ReplEvalInputRequestEvent(
            "execution-1", 7, "input-1", "Name: ", false);
        execution.Publish(input);
        execution.CompleteResult(42);

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo(new
        {
            SessionId = "default",
            Generation = 3,
            ExecutionStatus = "ok",
            Presentation = new DenoReplPresentationResult(1, 1, 1, 1),
            Truncated = false,
            OmittedBytes = 0
        });
        result.Outputs.Select(static output => output.Kind).Should().Equal("stdout", "stderr", "result");
        result.Outputs[0].Text.Should().Be("private stdout");
        result.Outputs[1].Text.Should().Be("shared stderr");
        result.Outputs[2].Value?.GetInt32().Should().Be(42);
        sink.Displays.Should().Equal("visible display");
        sink.Updates.Should().ContainSingle().Which.Text.Should().Be("visible update");
        sink.Clears.Should().Equal(true);
        sink.Stderr.Should().Equal("shared stderr");
        connection.InputReplies.Should().ContainSingle().Which.Should().Be((input, "Ada"));
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false, "error")]
    [InlineData(true, "abort")]
    public async Task ErrorAndCancelledTerminalsProduceTypedStatus(bool cancelled, string expectedStatus)
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "session",
            1,
            new DenoReplOptions(),
            sink,
            []);

        if (cancelled)
            execution.CompleteCancelled();
        else
            execution.CompleteError("TypeError", "bad value");

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            TestContext.Current.CancellationToken);

        result.ExecutionStatus.Should().Be(expectedStatus);
        if (cancelled)
        {
            result.Outputs.Should().BeEmpty();
            sink.Errors.Should().BeEmpty();
        }
        else
        {
            result.Outputs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                Kind = "error",
                Text = "bad value",
                Name = "TypeError"
            });
            sink.Errors.Should().Equal(("TypeError", "bad value"));
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task ModelTruncationStillDrainsAndPresentsLaterDisplay()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxModelOutputBytes = 256 },
            sink,
            []);

        for (var sequence = 1; sequence <= 20; sequence++)
            execution.Publish(new ReplEvalConsoleEvent(
                "execution-1", sequence, "stdout", new string('x', 128)));
        execution.Publish(new ReplEvalDisplayEvent(
            "execution-1",
            21,
            false,
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = "after truncation"
            }),
            null));
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            TestContext.Current.CancellationToken);

        result.Truncated.Should().BeTrue();
        result.OmittedBytes.Should().BeGreaterThan(0);
        sink.Displays.Should().Equal("after truncation");
    }

    [Fact(Timeout = 15_000)]
    public async Task OversizedJsonResultFallsBackToBoundedTextPlain()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxModelOutputBytes = 128 },
            new RecordingPresentationSink(),
            []);
        execution.CompleteResult(JsonSerializer.SerializeToElement(new string('x', 1024)));

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            TestContext.Current.CancellationToken);

        result.Outputs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = "result",
            MediaType = "text/plain"
        });
        result.Outputs[0].Text.Should().HaveLength(64);
        result.Truncated.Should().BeTrue();
        result.OmittedBytes.Should().BeGreaterThan(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task PresentationEventLimitSkipsExcessEventsButContinuesDraining()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxPresentationEventsPerExecution = 1 },
            sink,
            []);
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = "display"
        });
        execution.Publish(new ReplEvalDisplayEvent("execution-1", 1, false, null, data, null));
        execution.Publish(new ReplEvalClearOutputEvent("execution-1", 2, false));
        execution.Publish(new ReplEvalConsoleEvent("execution-1", 3, "stderr", "still modeled"));
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            TestContext.Current.CancellationToken);

        result.Presentation.Should().Be(new DenoReplPresentationResult(1, 0, 0, 2));
        result.Outputs.Should().ContainSingle().Which.Text.Should().Be("still modeled");
        sink.Displays.Should().ContainSingle();
        sink.Clears.Should().BeEmpty();
        sink.Stderr.Should().BeEmpty();
    }

    private sealed class RecordingPresentationSink : IDenoReplPresentationSink
    {
        internal string InputReply { get; init; } = string.Empty;

        internal List<string> Displays { get; } = [];

        internal List<(JupyterDisplayId Id, string Text)> Updates { get; } = [];

        internal List<bool> Clears { get; } = [];

        internal List<string> Stderr { get; } = [];

        internal List<(string Name, string Value)> Errors { get; } = [];

        public ValueTask DisplayAsync(
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Displays.Add(data.Data["text/plain"].GetString() ?? string.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
            MimeBundle data,
            JupyterDisplayId displayId,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Displays.Add(data.Data["text/plain"].GetString() ?? string.Empty);
            return ValueTask.FromResult(displayId);
        }

        public ValueTask UpdateDisplayAsync(
            JupyterDisplayId displayId,
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Updates.Add((displayId, data.Data["text/plain"].GetString() ?? string.Empty));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clears.Add(wait);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stderr.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishErrorAsync(
            string name,
            string value,
            IReadOnlyList<string> traceback,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Errors.Add((name, value));
            return ValueTask.CompletedTask;
        }

        public Task<string> RequestInputAsync(
            string prompt,
            bool password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(InputReply);
        }
    }
}
