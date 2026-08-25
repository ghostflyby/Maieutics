using System.Text.Json;
using FluentAssertions;
using Maieutics.DenoRepl;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoReplExecutionCollectorTests
{
    [Fact(Timeout = 15_000)]
    public async Task OrderedOutputFramesProduceSeparateModelAndNotebookProjections()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink { InputReply = "Ada" };
        var collector = new DenoReplExecutionCollector(
            "default",
            3,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        var display = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = "visible display"
        });
        var update = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = "visible update"
        });
        output.Publish(OutputFrames.Stdout(1, "execution-1", "private stdout"));
        output.Publish(OutputFrames.Display(2, "execution-1", "inner", display, isUpdate: false));
        output.Publish(OutputFrames.Display(3, "execution-1", "inner", update, isUpdate: true));
        output.Publish(OutputFrames.Display(4, "execution-1", "missing", update, isUpdate: true));
        output.Publish(OutputFrames.Clear(5, "execution-1", wait: true));
        output.Publish(OutputFrames.Stderr(6, "execution-1", "shared stderr"));
        output.End();
        var input = new ReplEvalInputRequestEvent(
            "execution-1", 7, "input-1", "Name: ", false);
        execution.Publish(input);
        execution.CompleteResult(42);

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo(new
        {
            SessionId = "default",
            Generation = 3,
            ExecutionStatus = "ok",
            Presentation = new DenoReplPresentationResult(
                1,
                1,
                1,
                RateSkippedCount: 0,
                BundleSkippedCount: 0,
                DisplaySkippedCount: 1,
                DigestTruncated: false,
                // The update folds into the display's digest entry instead of adding a second one.
                Digests: new[]
                {
                    new DenoReplDisplayDigest(
                        new[] { "text/plain" },
                        "visible update",
                        "inner",
                        IsUpdate: true)
                }),
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
    public async Task FramesForOtherExecutionsAreIgnored()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Stdout(1, "other-execution", "stray stdout"));
        output.Publish(OutputFrames.Display(
            2,
            "other-execution",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = "stray display"
            }),
            isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Outputs.Should().BeEmpty();
        sink.Displays.Should().BeEmpty();
        result.Presentation.SkippedCount.Should().Be(0);
        result.Presentation.Digests.Should().BeEmpty();
    }

    [Fact(Timeout = 15_000)]
    public async Task DisplayFrameReconstructsBinaryBuffersFromPlaceholdersIntoBase64()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
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
        output.Publish(frame);
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        sink.BinaryDisplays.Should().ContainSingle().Which.Data["image/png"]
            .GetString().Should().NotBeNull();
        sink.BinaryDisplays.Single().Data["image/png"].GetString()!
            .Should().Be(Convert.ToBase64String(png));
        // The binary mime lists in the digest but never produces a preview.
        result.Presentation.Digests.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            MediaTypes = new[] { "image/png", "text/plain" },
            Preview = "binary display",
            DisplayId = null as string,
            IsUpdate = false
        });
    }

    [Fact(Timeout = 15_000)]
    public async Task BinaryOnlyBundlesDigestTheirMimeKeysWithoutAPreview()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47 };
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["image/png"] = JsonSerializer.SerializeToElement(new Dictionary<string, int>
                {
                    ["$buffer"] = 0
                })
            }),
            isUpdate: false,
            png));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        var digest = result.Presentation.Digests.Should().ContainSingle().Which;
        digest.MediaTypes.Should().Equal("image/png");
        digest.Preview.Should().BeNull();
        sink.BinaryDisplays.Should().ContainSingle().Which.Data["image/png"].GetString()
            .Should().Be(Convert.ToBase64String(png));
    }

    [Fact(Timeout = 15_000)]
    public async Task DigestPreviewPrefersTextPlainOverLaterStringMimes()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/html"] = "<b>html</b>",
                ["application/vnd.vega.v5+json"] = """{"width":200}""",
                ["text/plain"] = "plain text"
            }),
            isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        var digest = result.Presentation.Digests.Should().ContainSingle().Which;
        digest.Preview.Should().Be("plain text");
        digest.MediaTypes.Should().Equal("text/html", "application/vnd.vega.v5+json", "text/plain");
    }

    [Fact(Timeout = 15_000)]
    public async Task DigestPreviewFallsBackToFirstTextOrStructuredStringMime()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["application/vnd.vega.v5+json"] = """{"width":200}"""
            }),
            isUpdate: false));
        output.Publish(OutputFrames.Display(
            2,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/markdown"] = "# title"
            }),
            isUpdate: false));
        output.Publish(OutputFrames.Display(
            3,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["application/json"] = """{"key":"value"}"""
            }),
            isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Presentation.Digests.Select(static digest => digest.Preview).Should()
            .Equal("""{"width":200}""", "# title", null);
    }

    [Fact(Timeout = 15_000)]
    public async Task DigestPreviewIsTruncatedToThePreviewBudget()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxDisplayDigestPreviewBytes = 16 },
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = new string('x', 100)
            }),
            isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);
        var digest = result.Presentation.Digests.Should().ContainSingle().Which;
        digest.Preview.Should().HaveLength(16);
        digest.Preview.Should().Be(new string('x', 16));
        result.Presentation.DigestTruncated.Should().BeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task DigestBudgetExhaustionFlagsTruncationAndCountsLaterDisplays()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxModelDisplayDigestBytes = 180 },
            sink,
            [],
            "execution-1");
        for (var sequence = 1; sequence <= 5; sequence++)
            output.Publish(OutputFrames.Display(
                sequence,
                "execution-1",
                null,
                JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    ["text/plain"] = "d" + new string('x', 50)
                }),
                isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Presentation.DigestTruncated.Should().BeTrue();
        result.Presentation.Digests.Should().NotBeEmpty();
        // The display presentation is independent of the digest budget: every display still shows.
        sink.Displays.Should().HaveCount(5);
        result.Presentation.SkippedCount.Should().Be(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task UpdatesFoldIntoTheOriginalDigestEntry()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            "tracked",
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = "initial"
            }),
            isUpdate: false));
        output.Publish(OutputFrames.Display(
            2,
            "execution-1",
            "tracked",
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/html"] = "<b>updated</b>",
                ["text/plain"] = "updated"
            }),
            isUpdate: true));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Presentation.Digests.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            MediaTypes = new[] { "text/html", "text/plain" },
            Preview = "updated",
            DisplayId = "tracked",
            IsUpdate = true
        });
        result.Presentation.DisplayCount.Should().Be(1);
        result.Presentation.UpdateCount.Should().Be(1);
    }

    [Fact(Timeout = 15_000)]
    public async Task MalformedUpdateIsSkippedWithoutADigestEntry()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            "missing",
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = "stray update"
            }),
            isUpdate: true));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Presentation.UpdateCount.Should().Be(0);
        result.Presentation.DisplaySkippedCount.Should().Be(1);
        result.Presentation.SkippedCount.Should().Be(1);
        result.Presentation.Digests.Should().BeEmpty();
        sink.Updates.Should().BeEmpty();
    }

    [Fact(Timeout = 15_000)]
    public async Task OversizedPresentationBundleIsSkippedButStillDigested()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxPresentationBundleBytes = 64 },
            sink,
            [],
            "execution-1");
        output.Publish(OutputFrames.Display(
            1,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = new string('x', 200)
            }),
            isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        sink.Displays.Should().BeEmpty();
        result.Presentation.BundleSkippedCount.Should().Be(1);
        result.Presentation.SkippedCount.Should().Be(1);
        result.Presentation.Digests.Should().ContainSingle().Which.Preview.Should()
            .HaveLength(200);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false, "error")]
    [InlineData(true, "abort")]
    public async Task ErrorAndCancelledTerminalsProduceTypedStatus(bool cancelled, string expectedStatus)
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "session",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1");

        if (cancelled)
            execution.CompleteCancelled();
        else
            execution.CompleteError("TypeError", "bad value");

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
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
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxModelOutputBytes = 256 },
            sink,
            [],
            "execution-1");

        for (var sequence = 1; sequence <= 20; sequence++)
            output.Publish(OutputFrames.Stdout(sequence, "execution-1", new string('x', 128)));
        output.Publish(OutputFrames.Display(
            21,
            "execution-1",
            null,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["text/plain"] = "after truncation"
            }),
            isUpdate: false));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
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
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxModelOutputBytes = 128 },
            new RecordingPresentationSink(),
            [],
            "execution-1");
        execution.CompleteResult(JsonSerializer.SerializeToElement(new string('x', 1024)));

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
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
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions { MaxPresentationEventsPerExecution = 1 },
            sink,
            [],
            "execution-1");
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = "display"
        });
        output.Publish(OutputFrames.Display(1, "execution-1", null, data, isUpdate: false));
        output.Publish(OutputFrames.Clear(2, "execution-1", wait: false));
        output.Publish(OutputFrames.Stderr(3, "execution-1", "still modeled"));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        result.Presentation.Should().BeEquivalentTo(new DenoReplPresentationResult(
            1,
            0,
            0,
            RateSkippedCount: 0,
            BundleSkippedCount: 0,
            DisplaySkippedCount: 2,
            DigestTruncated: false,
            Digests: new[] { new DenoReplDisplayDigest(new[] { "text/plain" }, "display") }));
        result.Outputs.Should().ContainSingle().Which.Text.Should().Be("still modeled");
        sink.Displays.Should().ContainSingle();
        sink.Clears.Should().BeEmpty();
        sink.Stderr.Should().BeEmpty();
    }

    [Fact(Timeout = 15_000)]
    public async Task RateLimitedDisplaysAreSkippedWithoutDisturbingLaterPresentation()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var limiter = new ReplOutputRateLimiter(
            new DenoReplOptions { MaxDisplayDataRate = 100 },
            timestampProvider: () => 1,
            frequency: 1);
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1",
            limiter);
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["text/plain"] = new string('x', 64)
        });
        output.Publish(OutputFrames.Display(1, "execution-1", null, data, isUpdate: false));
        output.Publish(OutputFrames.Display(2, "execution-1", null, data, isUpdate: false));
        output.Publish(OutputFrames.Stdout(3, "execution-1", "still ordered"));
        output.End();
        execution.CompleteResult();

        var result = await collector.ConsumeAsync(
            connection,
            execution.Execution,
            output,
            TestContext.Current.CancellationToken);

        // The first display fits the budget; the second is rate-limited: it is not presented and
        // has no digest entry. The skip counts on the model result, and later frames (stdout)
        // still present in order.
        result.Presentation.RateSkippedCount.Should().Be(1);
        result.Presentation.DisplayCount.Should().Be(1);
        result.Presentation.SkippedCount.Should().Be(1);
        result.Presentation.Digests.Should().ContainSingle();
        result.Outputs.Should().ContainSingle().Which.Text.Should().Be("still ordered");
        sink.Displays.Should().ContainSingle();
        sink.BinaryDisplays.Should().ContainSingle();
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
            Displays.Add(PlainTextOf(data));
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
            Displays.Add(PlainTextOf(data));
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
            Updates.Add((displayId, PlainTextOf(data)));
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

        private static string PlainTextOf(MimeBundle data)
        {
            return data.Data.TryGetValue("text/plain", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
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
            bool isUpdate,
            params byte[][] buffers)
        {
            return new ReplOutputDisplayFrame(
                seq,
                executionId,
                ToDictionary(data),
                new Dictionary<string, JsonElement>(),
                displayId,
                isUpdate,
                buffers);
        }
    }
}
