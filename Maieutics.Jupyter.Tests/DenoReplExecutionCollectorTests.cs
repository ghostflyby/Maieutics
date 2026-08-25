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

        await collector.ObserveAsync(connection, OutputFrames.Stdout(1, "execution-1", "private stdout"), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Display(2, "execution-1", "inner", display, isUpdate: false), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Display(3, "execution-1", "inner", update, isUpdate: true), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Display(4, "execution-1", "missing", update, isUpdate: true), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Clear(5, "execution-1", wait: true), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Stderr(6, "execution-1", "shared stderr"), CancellationToken.None);
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

    [Fact(Timeout = 15_000)]
    public async Task DisplayFrameReconstructsBinaryBuffersFromPlaceholders()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            []);
        var data = JsonDocument.Parse(
            """{"image/png":{"$buffer":0},"text/plain":"binary display"}""").RootElement.Clone();
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01, 0x02 };
        var frame = new ReplOutputDisplayFrame(
            1,
            "execution-1",
            ToDictionary(data),
            new Dictionary<string, JsonElement>(),
            null,
            false,
            new List<byte[]> { png });
        await collector.ObserveAsync(connection, frame, CancellationToken.None);
        execution.CompleteResult();

        await collector.ConsumeAsync(
            connection,
            execution.Execution,
            TestContext.Current.CancellationToken);

        sink.BinaryDisplays.Should().ContainSingle().Which.Data["image/png"]
            .GetString().Should().NotBeNull();
        sink.BinaryDisplays.Single().Data["image/png"].GetString()!
            .Should().Be(Convert.ToBase64String(png));
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
            await collector.ObserveAsync(
                connection,
                OutputFrames.Stdout(sequence, "execution-1", new string('x', 128)),
                CancellationToken.None);
        await collector.ObserveAsync(
            connection,
            OutputFrames.Display(
                21,
                "execution-1",
                null,
                JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    ["text/plain"] = "after truncation"
                }),
                isUpdate: false),
            CancellationToken.None);
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
        await collector.ObserveAsync(connection, OutputFrames.Display(1, "execution-1", null, data, isUpdate: false), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Clear(2, "execution-1", wait: false), CancellationToken.None);
        await collector.ObserveAsync(connection, OutputFrames.Stderr(3, "execution-1", "still modeled"), CancellationToken.None);
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

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonElement element)
    {
        return element.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private sealed class RecordingPresentationSink : IDenoReplPresentationSink
    {
        internal string InputReply { get; init; } = string.Empty;

        internal List<string> Displays { get; } = [];

        internal List<(JupyterDisplayId Id, string Text)> Updates { get; } = [];

        internal List<bool> Clears { get; } = [];

        internal List<string> Stderr { get; } = [];

        internal List<(string Name, string Value)> Errors { get; } = [];

        internal List<MimeBundle> BinaryDisplays { get; } = [];

        public ValueTask DisplayAsync(
            MimeBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Displays.Add(data.Data["text/plain"].GetString() ?? string.Empty);
            BinaryDisplays.Add(data);
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
            BinaryDisplays.Add(data);
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
            BinaryDisplays.Add(data);
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

    /// <summary>Hand-builds output frames as the TS encoder would produce them (byte layout matches
    /// <c>output_protocol.ts</c>).</summary>
    private static class OutputFrames
    {
        internal static ReplOutputConsoleFrame Stdout(long seq, string executionId, string text)
        {
            return new ReplOutputConsoleFrame(seq, executionId, "stdout", text);
        }

        internal static ReplOutputConsoleFrame Stderr(long seq, string executionId, string text)
        {
            return new ReplOutputConsoleFrame(seq, executionId, "stderr", text);
        }

        internal static ReplOutputClearOutputFrame Clear(long seq, string executionId, bool wait)
        {
            return new ReplOutputClearOutputFrame(seq, executionId, wait);
        }

        internal static ReplOutputDisplayFrame Display(
            long seq,
            string executionId,
            string? displayId,
            JsonElement data,
            bool isUpdate)
        {
            return new ReplOutputDisplayFrame(
                seq,
                executionId,
                ToDictionary(data),
                new Dictionary<string, JsonElement>(),
                displayId,
                isUpdate,
                []);
        }
    }
}
