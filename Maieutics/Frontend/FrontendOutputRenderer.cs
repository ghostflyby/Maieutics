using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>Owns the lazy, offline Deno rendering child independently of Agent runs and frontend connections.</summary>
internal sealed class FrontendOutputRenderer(
    FrontendOptions frontend,
    DenoReplOptions deno,
    ApplicationPaths paths,
    PermissionPolicyAcquirer policies,
    ILogger<FrontendOutputRenderer> logger) : IAsyncDisposable
{
    private readonly Lock _disposeGate = new();
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _directory = Path.Combine(paths.OutputRendererStagingRoot, Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 32 * 1024 * 1024 };
    private Generation? _generation;
    private Task? _disposing;
    private Task? _shutdown;
    private int _queued;
    private bool _closed;
    private string? _css;

    internal int? ProcessId
    {
        get
        {
            lock (_stateGate)
                return _generation is { Exited: false, Stopping: false, Process: { } process }
                    ? process.ProcessId : null;
        }
    }

    /// <summary>Observation barrier for the current child's exit or retirement, including invalidation.</summary>
    internal Task? ProcessCompletion
    {
        get { lock (_stateGate) return _generation?.Observation; }
    }

    internal string ModuleDirectory => _directory;

    internal bool RegisterReady(FrontendRendererReady value)
    {
        var supplied = Encoding.UTF8.GetBytes(value.Nonce ?? string.Empty);
        lock (_stateGate)
        {
            return value.Version == 1 && value.Port is > 0 and <= 65535 && !_closed &&
                   _generation is { Stopping: false, Exited: false } generation &&
                   CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(generation.Nonce)) &&
                   generation.Ready.TrySetResult(value.Port);
        }
    }

    private CancellationToken Admit()
    {
        lock (_stateGate)
        {
            if (_closed) throw Unavailable();
            if (_queued >= 8) throw new FrontendFailureException("render_busy", "The renderer request queue is full.");
            _queued++;
            return _lifetime.Token;
        }
    }

    internal async Task<FrontendRenderedMarkdown> RenderAsync(
        FrontendRenderMarkdown request, CancellationToken cancellationToken, bool template = false, bool vue = false, bool assistant = false)
    {
        if (request.Version != 1 || request.DocumentId is not { Length: > 0 and <= 64 } ||
            !char.IsAsciiLetter(request.DocumentId[0]) ||
            request.DocumentId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            request.Source is null || request.Source.Length > 200_000)
            throw new FrontendFailureException("invalid_request", "Invalid Markdown render request.");
        if (template && (request.Template is not { Version: 1, Bindings: { Length: <= 128 } }))
            throw new FrontendFailureException("invalid_request", "Invalid template render request.");
        if (vue && request.Vue is not { Version: 1, Format: "markdown" or "sfc" })
            throw new FrontendFailureException("invalid_request", "Invalid Vue component request.");
        if ((request.Progressive is true && !assistant) || (request.CompileOnly is true && request.Progressive is not true))
            throw new FrontendFailureException("invalid_request", "Invalid progressive rendering request.");
        var lifetime = Admit();
        var entered = false;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lifetime, budget.Token);
            await _requests.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            var generation = await AcquireGenerationAsync(operation.Token).ConfigureAwait(false);
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                operation.Token, generation.Lifetime.Token);
            int port;
            lock (_stateGate)
            {
                if (generation is not { Exited: false, Stopping: false, Port: { } readyPort }) throw Unavailable();
                port = readyPort;
            }
            var endpoint = assistant ? "assistant" : vue ? "vue" : template ? "template" : "markdown";
            using var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/{endpoint}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", generation.Nonce);
            message.Content = JsonContent.Create(request, FrontendJsonContext.Default.FrontendRenderMarkdown);
            using var response = await _http.SendAsync(message, requestLifetime.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var failure = await response.Content.ReadFromJsonAsync(
                    FrontendJsonContext.Default.FrontendError, requestLifetime.Token).ConfigureAwait(false);
                throw new FrontendFailureException("invalid_request", "Invalid render input.",
                    ValidateDiagnostics(failure?.Diagnostics, request.Source));
            }
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync(
                FrontendJsonContext.Default.FrontendRenderedMarkdown, requestLifetime.Token).ConfigureAwait(false);
            if (result is null || result.Version != 1 || result.DocumentId != request.DocumentId ||
                result.Html is null || result.Html.Length > 4_000_000 ||
                (template && result.Expressions is not { Length: <= 256 }))
                throw Unavailable();
            var component = vue || (assistant && result.Vue is not null);
            if (component && (result.Vue is not { Version: 1, Code: { Length: <= 400_000 } } ||
                        result.Vue.ScopeId != $"data-v-{request.DocumentId}" || result.Css is not { Length: <= 48_000 }))
                throw Unavailable();
            if (request.Progressive is true && result.Vue is { } progressiveComponent &&
                (progressiveComponent.SetupKey is not { Length: 64 } ||
                 progressiveComponent.SetupKey.Any(character => !char.IsAsciiHexDigit(character)) ||
                 progressiveComponent.RenderCode is not { Length: <= 400_000 } ||
                 result.RenderedSource is null || !request.Source.StartsWith(result.RenderedSource, StringComparison.Ordinal)))
                throw Unavailable();
            return result with { Css = component ? _css + result.Css : _css, Diagnostics = ValidateDiagnostics(result.Diagnostics, request.Source) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (entered) await RetireCurrentAsync().ConfigureAwait(false);
            throw Unavailable();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or PermissionException)
        {
            logger.LogWarning("Output rendering failed with {FailureType}.", exception.GetType().Name);
            if (entered) await RetireCurrentAsync().ConfigureAwait(false);
            throw Unavailable();
        }
        finally
        {
            if (entered) _requests.Release();
            lock (_stateGate)
            {
                if (--_queued == 0 && _closed) _drained.TrySetResult();
            }
        }
    }

    private async Task<Generation> AcquireGenerationAsync(CancellationToken cancellationToken)
    {
        Generation? previous;
        lock (_stateGate)
        {
            previous = _generation;
            if (previous is { Exited: false, Stopping: false, Port: not null, Process: { } process } &&
                !process.Completion.IsCompleted) return previous;
        }
        if (previous is not null) await RetireAsync(previous).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Generation> StartAsync(CancellationToken cancellationToken)
    {
        var generation = new Generation();
        try
        {
            Directory.CreateDirectory(_directory);
            var entry = Path.Combine(_directory, "service.js");
            await using (var source = typeof(FrontendOutputRenderer).Assembly.GetManifestResourceStream(
                             "Deno/maieutics-output-renderer/service.js") ?? throw Unavailable())
            await using (var target = File.Create(entry))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            using (var styles = typeof(FrontendOutputRenderer).Assembly.GetManifestResourceStream(
                       "Deno/maieutics-output-renderer/styles.css") ?? throw Unavailable())
            using (var reader = new StreamReader(styles))
                _css = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var policy = policies.Acquire(PermissionBaseline.ForOutputRendererLayer(frontend.Port));
            var environment = new Dictionary<string, string>
            {
                ["MAIEUTICS_RENDERER_CALLBACK"] = $"http://127.0.0.1:{frontend.Port}/v1/render/ready",
                ["MAIEUTICS_RENDERER_TOKEN"] = frontend.Token,
                ["MAIEUTICS_RENDERER_NONCE"] = generation.Nonce,
                ["DENO_DIR"] = Path.Combine(_directory, "cache")
            };
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SYSTEMROOT") is { } systemRoot)
                environment["SYSTEMROOT"] = systemRoot;
            var start = new ProcessStartInfo(deno.Executable)
            {
                WorkingDirectory = _directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "run", "--no-prompt", "--no-config", "--no-lock", "--no-npm", "--no-remote", "--cached-only", "--unstable-worker-options" })
                start.ArgumentList.Add(argument);
            foreach (var argument in DenoPermissionRenderer.Render(policy)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(entry);
            start.Environment.Clear();
            foreach (var (name, value) in environment)
            {
                if (!DenoPermissionResolver.Resolve(policy, "env", name).IsAllowed) throw Unavailable();
                start.Environment[name] = value;
            }
            var slot = await InternalDenoProcessGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
            DenoRunProcess process;
            try
            {
                lock (_stateGate)
                {
                    if (_closed) throw Unavailable();
                    _generation = generation;
                }
                process = DenoRunProcess.Start(start, InternalDenoProcessKind.OutputRenderer, logger, slot: slot);
            }
            catch
            {
                slot.Dispose();
                throw;
            }
            lock (_stateGate)
            {
                generation.Process = process;
                generation.Observation = ObserveAsync(generation, process);
            }
            var first = await Task.WhenAny(generation.Ready.Task, process.Completion)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (first != generation.Ready.Task || process.Completion.IsCompleted) throw Unavailable();
            var port = await generation.Ready.Task.ConfigureAwait(false);
            lock (_stateGate)
            {
                if (generation.Exited || generation.Stopping || _closed) throw Unavailable();
                generation.Port = port;
            }
            return generation;
        }
        catch (OperationCanceledException)
        {
            await RetireAsync(generation).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            bool closed;
            lock (_stateGate) closed = _closed;
            if (!closed)
                logger.LogWarning("Output renderer startup failed with {FailureType}; a later request may retry.",
                    exception.GetType().Name);
            await RetireAsync(generation).ConfigureAwait(false);
            throw Unavailable();
        }
    }

    private async Task ObserveAsync(Generation generation, DenoRunProcess process)
    {
        await Task.Yield();
        var completed = false;
        string? failureType = null;
        try
        {
            await process.Completion.WaitAsync(generation.Lifetime.Token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (generation.Lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            completed = true;
            failureType = exception.GetType().Name;
        }
        bool unexpected;
        lock (_stateGate)
        {
            unexpected = completed && !generation.Stopping && !generation.ExitReported && !_closed;
            generation.ExitReported |= unexpected;
            generation.Exited = true;
            generation.Port = null;
        }
        generation.Ready.TrySetCanceled();
        await generation.Lifetime.CancelAsync().ConfigureAwait(false);
        if (unexpected) LogUnexpectedExit(process, failureType);
    }

    private Task RetireCurrentAsync()
    {
        Generation? generation;
        lock (_stateGate) generation = _generation;
        return generation is null ? Task.CompletedTask : RetireAsync(generation);
    }

    private void LogUnexpectedExit(DenoRunProcess process, string? failureType) =>
        logger.LogWarning("Output renderer {ProcessId} exited unexpectedly (exit {ExitCode}, failure {FailureType}); a later request may restart it.",
            process.ProcessId, process.ExitCode, failureType);

    private Task RetireAsync(Generation generation)
    {
        Task retirement;
        DenoRunProcess? exited = null;
        lock (_stateGate)
        {
            // A request can notice Completion before the observer's continuation gets scheduled.
            if (!_closed && !generation.Stopping && !generation.ExitReported &&
                generation.Process is { } process && process.Completion.IsCompleted)
            {
                generation.ExitReported = true;
                exited = process;
            }
            generation.Stopping = true;
            generation.Port = null;
            retirement = generation.Retirement ??= RetireCoreAsync(generation);
        }
        if (exited is not null) LogUnexpectedExit(exited, null);
        return retirement;
    }

    private async Task RetireCoreAsync(Generation generation)
    {
        await Task.Yield();
        generation.Ready.TrySetCanceled();
        try
        {
            await generation.Lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (generation.Process is { } process) await process.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try { await generation.Observation.ConfigureAwait(false); }
                finally
                {
                    lock (_stateGate)
                    {
                        if (ReferenceEquals(_generation, generation)) _generation = null;
                    }
                    generation.Lifetime.Dispose();
                }
            }
        }
    }

    internal void BeginShutdown() => RequestShutdown();

    private Task RequestShutdown()
    {
        lock (_stateGate)
        {
            if (_shutdown is { } shutdown) return shutdown;
            _closed = true;
            if (_queued == 0) _drained.TrySetResult();
            return _shutdown = CancelLifetimeAsync();
        }
    }

    private async Task CancelLifetimeAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await RequestShutdown().ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        try
        {
            await RetireCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _http.Dispose();
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
            finally
            {
                _requests.Dispose();
                _lifetime.Dispose();
            }
        }
    }

    private sealed class Generation
    {
        internal string Nonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        internal TaskCompletionSource<int> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Lifetime { get; } = new();
        internal DenoRunProcess? Process { get; set; }
        internal Task Observation { get; set; } = Task.CompletedTask;
        internal Task? Retirement { get; set; }
        internal int? Port { get; set; }
        internal bool Stopping { get; set; }
        internal bool Exited { get; set; }
        internal bool ExitReported { get; set; }
    }

    private static FrontendRenderDiagnostic[]? ValidateDiagnostics(FrontendRenderDiagnostic[]? diagnostics, string source)
    {
        if (diagnostics is null) return null;
        if (diagnostics.Length > 32) throw Unavailable();
        var lines = source.Split('\n');
        foreach (var item in diagnostics)
            if (item is null || item.Code is not ("unclosed_code_fence" or "component_compile_failed" or "math_invalid" or "math_limit") ||
                item.Line < 1 || item.Line > lines.Length || item.Column < 1 || item.Column > lines[item.Line - 1].Length + 1)
                throw Unavailable();
        return diagnostics;
    }

    private static FrontendFailureException Unavailable() =>
        new("render_unavailable", "The output renderer is unavailable; display the original Markdown.");
}
