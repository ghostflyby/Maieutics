using System.Text.Json;
using Maieutics.DenoRepl;

namespace Maieutics.Product.Tests;

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

        Assert.Equivalent(new
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
                Digests:
                [
                    new DenoReplDisplayDigest(
                        ["text/plain"],
                        "visible update",
                        "inner",
                        IsUpdate: true),
                ]),
            Truncated = false,
            OmittedBytes = 0
        }, result);
        Assert.Equal(["stdout", "stderr", "result"], result.Outputs.Select(static output => output.Kind));
        Assert.Equal("private stdout", result.Outputs[0].Text);
        Assert.Equal("shared stderr", result.Outputs[1].Text);
        Assert.Equal(42, result.Outputs[2].Value?.GetInt32());
        Assert.Equal(new[] { "visible display" }, sink.Displays);
        Assert.Equal("visible update", Assert.Single(sink.Updates).Text);
        Assert.Equal(new[] { true }, sink.Clears);
        Assert.Equal(new[] { "shared stderr" }, sink.Stderr);
        Assert.Equal((input, "Ada"), Assert.Single(connection.InputReplies));
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

        Assert.Empty(result.Outputs);
        Assert.Empty(sink.Displays);
        Assert.Equal(0, result.Presentation.SkippedCount);
        Assert.Empty(result.Presentation.Digests);
    }

    [Fact(Timeout = 15_000)]
    public async Task DisplayFrameStoresBinaryBuffersAsContentAddressedObjectReferences()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var objectStore = new InMemoryDisplayObjectStore();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1",
            displayObjectStore: objectStore);
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

        // The bundle carries an object reference, never base64 (invariant 26).
        var item = Assert.Single(sink.BinaryDisplays);
        var reference = item.Data["image/png"];
        Assert.Equal(JsonValueKind.Object, reference.ValueKind);
        var url = reference.GetProperty("$object").GetString()!;
        Assert.StartsWith("/v1/objects/", url);
        Assert.Equal(png.Length, reference.GetProperty("byteLength").GetInt64());
        // The referenced bytes are exactly the display payload.
        Assert.Equal(png, objectStore.Get(url));
        // The binary mime lists in the digest but never produces a preview.
        Assert.Equivalent(new
        {
            MediaTypes = new[] { "image/png", "text/plain" },
            Preview = "binary display",
            DisplayId = null as string,
            IsUpdate = false
        }, Assert.Single(result.Presentation.Digests));
    }

    [Fact(Timeout = 15_000)]
    public async Task BinaryOnlyBundlesDigestTheirMimeKeysWithoutAPreview()
    {
        var connection = new DenoReplSessionTests.ControlledConnection();
        var execution = new DenoReplSessionTests.ControlledEval("execution-1");
        var output = new DenoReplSessionTests.ControlledOutputConnection();
        var sink = new RecordingPresentationSink();
        var objectStore = new InMemoryDisplayObjectStore();
        var collector = new DenoReplExecutionCollector(
            "default",
            1,
            new DenoReplOptions(),
            sink,
            [],
            "execution-1",
            displayObjectStore: objectStore);
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

        var digest = Assert.Single(result.Presentation.Digests);
        Assert.Equal(["image/png"], digest.MediaTypes);
        Assert.Null(digest.Preview);
        var reference = sink.BinaryDisplays.Single().Data["image/png"];
        var url = reference.GetProperty("$object").GetString()!;
        Assert.Equal(png, objectStore.Get(url));
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

        var digest = Assert.Single(result.Presentation.Digests);
        Assert.Equal("plain text", digest.Preview);
        Assert.Equal(["text/html", "application/vnd.vega.v5+json", "text/plain"], digest.MediaTypes);
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

        Assert.Equal(["""{"width":200}""", "# title", null], result.Presentation.Digests.Select(static digest => digest.Preview));
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
        var digest = Assert.Single(result.Presentation.Digests);
        Assert.Equal(16, (digest.Preview ?? throw new InvalidOperationException("the assertion subject is null")).Length);
        Assert.Equal(new string('x', 16), digest.Preview);
        Assert.False(result.Presentation.DigestTruncated);
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

        Assert.True(result.Presentation.DigestTruncated);
        Assert.NotEmpty(result.Presentation.Digests);
        // The display presentation is independent of the digest budget: every display still shows.
        Assert.Equal(5, sink.Displays.Count());
        Assert.Equal(0, result.Presentation.SkippedCount);
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

        Assert.Equivalent(new
        {
            MediaTypes = new[] { "text/html", "text/plain" },
            Preview = "updated",
            DisplayId = "tracked",
            IsUpdate = true
        }, Assert.Single(result.Presentation.Digests));
        Assert.Equal(1, result.Presentation.DisplayCount);
        Assert.Equal(1, result.Presentation.UpdateCount);
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

        Assert.Equal(0, result.Presentation.UpdateCount);
        Assert.Equal(1, result.Presentation.DisplaySkippedCount);
        Assert.Equal(1, result.Presentation.SkippedCount);
        Assert.Empty(result.Presentation.Digests);
        Assert.Empty(sink.Updates);
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

        Assert.Empty(sink.Displays);
        Assert.Equal(1, result.Presentation.BundleSkippedCount);
        Assert.Equal(1, result.Presentation.SkippedCount);
        Assert.Equal(200, (Assert.Single(result.Presentation.Digests).Preview ?? throw new InvalidOperationException("the assertion subject is null")).Length);
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

        Assert.Equal(expectedStatus, result.ExecutionStatus);
        if (cancelled)
        {
            Assert.Empty(result.Outputs);
            Assert.Empty(sink.Errors);
        }
        else
        {
            Assert.Equivalent(new
            {
                Kind = "error",
                Text = "bad value",
                Name = "TypeError"
            }, Assert.Single(result.Outputs));
            Assert.Equal(new (string Name, string Value)[] { ("TypeError", "bad value") }, sink.Errors);
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

        Assert.True(result.Truncated);
        Assert.True(result.OmittedBytes > 0);
        Assert.Equal(new[] { "after truncation" }, sink.Displays);
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

        Assert.Equivalent(new
        {
            Kind = "result",
            MediaType = "text/plain"
        }, Assert.Single(result.Outputs));
        Assert.Equal(64, (result.Outputs[0].Text ?? throw new InvalidOperationException("the assertion subject is null")).Length);
        Assert.True(result.Truncated);
        Assert.True(result.OmittedBytes > 0);
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

        Assert.Equivalent(new DenoReplPresentationResult(
            1,
            0,
            0,
            RateSkippedCount: 0,
            BundleSkippedCount: 0,
            DisplaySkippedCount: 2,
            DigestTruncated: false,
            Digests: [new DenoReplDisplayDigest(["text/plain"], "display")]), result.Presentation);
        Assert.Equal("still modeled", Assert.Single(result.Outputs).Text);
        Assert.Single(sink.Displays);
        Assert.Empty(sink.Clears);
        Assert.Empty(sink.Stderr);
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
        Assert.Equal(1, result.Presentation.RateSkippedCount);
        Assert.Equal(1, result.Presentation.DisplayCount);
        Assert.Equal(1, result.Presentation.SkippedCount);
        Assert.Single(result.Presentation.Digests);
        Assert.Equal("still ordered", Assert.Single(result.Outputs).Text);
        Assert.Single(sink.Displays);
        Assert.Single(sink.BinaryDisplays);
    }

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonElement element)
    {
        return element.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    internal sealed class RecordingPresentationSink : IDenoReplPresentationSink
    {
        internal string InputReply { get; init; } = string.Empty;

        internal List<string> Displays { get; } = [];

        internal List<(ReplDisplayId Id, string Text)> Updates { get; } = [];

        internal List<bool> Clears { get; } = [];

        internal List<string> Stderr { get; } = [];

        internal List<(string Name, string Value)> Errors { get; } = [];

        internal List<ReplDisplayBundle> BinaryDisplays { get; } = [];

        public ValueTask DisplayAsync(
            ReplDisplayBundle data,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Displays.Add(PlainTextOf(data));
            BinaryDisplays.Add(data);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReplDisplayId> DisplayTrackedAsync(
            ReplDisplayBundle data,
            ReplDisplayId displayId,
            IReadOnlyDictionary<string, JsonElement> metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Displays.Add(PlainTextOf(data));
            BinaryDisplays.Add(data);
            return ValueTask.FromResult(displayId);
        }

        public ValueTask UpdateDisplayAsync(
            ReplDisplayId displayId,
            ReplDisplayBundle data,
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
    public bool TryCompleteInput(string requestId, string value) => false;

        private static string PlainTextOf(ReplDisplayBundle data)
        {
            return data.Data.TryGetValue("text/plain", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
    }


    public bool TryCompleteInput(string requestId, string value) => false;

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
