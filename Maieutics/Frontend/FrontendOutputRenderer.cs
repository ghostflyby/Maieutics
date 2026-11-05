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
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private readonly string _directory = Path.Combine(paths.OutputRendererStagingRoot, Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 32 * 1024 * 1024 };
    private DenoRunProcess? _process;
    private Task? _disposing;
    private int? _port;
    private int _queued;
    private bool _failed;
    private string? _css;

    internal int? ProcessId => _process?.ProcessId;
    internal string ModuleDirectory => _directory;

    internal bool RegisterReady(FrontendRendererReady value)
    {
        var supplied = Encoding.UTF8.GetBytes(value.Nonce ?? string.Empty);
        return value.Version == 1 && value.Port is > 0 and <= 65535 &&
               CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(_nonce)) &&
               !_lifetime.IsCancellationRequested && _ready.TrySetResult(value.Port);
    }

    internal async Task<FrontendRenderedMarkdown> RenderAsync(
        FrontendRenderMarkdown request, CancellationToken cancellationToken, bool template = false)
    {
        if (request.Version != 1 || request.DocumentId is not { Length: > 0 and <= 64 } ||
            !char.IsAsciiLetter(request.DocumentId[0]) ||
            request.DocumentId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            request.Source is null || request.Source.Length > 200_000)
            throw new FrontendFailureException("invalid_request", "Invalid Markdown render request.");
        if (template && (request.Template is not { Version: 1, Bindings: { Length: <= 128 } }))
            throw new FrontendFailureException("invalid_request", "Invalid template render request.");
        if (Interlocked.Increment(ref _queued) > 8)
        {
            Interlocked.Decrement(ref _queued);
            throw new FrontendFailureException("render_busy", "The renderer request queue is full.");
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token, budget.Token);
        var entered = false;
        try
        {
            await _requests.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            if (_failed) throw Unavailable();
            var port = _port ??= await StartAsync(operation.Token).ConfigureAwait(false);
            var endpoint = template ? "template" : "markdown";
            using var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/{endpoint}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _nonce);
            message.Content = JsonContent.Create(request, FrontendJsonContext.Default.FrontendRenderMarkdown);
            using var response = await _http.SendAsync(message, operation.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new FrontendFailureException("invalid_request", "Invalid render input.");
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync(
                FrontendJsonContext.Default.FrontendRenderedMarkdown, operation.Token).ConfigureAwait(false);
            if (result is null || result.Version != 1 || result.DocumentId != request.DocumentId ||
                result.Html is null || result.Html.Length > 4_000_000 ||
                (template && result.Expressions is not { Length: <= 256 }))
                throw Unavailable();
            return result with { Css = _css };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or PermissionException)
        {
            logger.LogWarning("Output rendering failed with {FailureType}.", exception.GetType().Name);
            throw Unavailable();
        }
        finally
        {
            if (entered) _requests.Release();
            Interlocked.Decrement(ref _queued);
        }
    }

    private async Task<int> StartAsync(CancellationToken cancellationToken)
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
            ["MAIEUTICS_RENDERER_NONCE"] = _nonce,
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
        foreach (var argument in new[] { "run", "--no-prompt", "--no-config", "--no-lock", "--no-npm", "--no-remote", "--cached-only" })
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
        try
        {
            _process = DenoRunProcess.Start(start, InternalDenoProcessKind.OutputRenderer, logger, slot: slot);
        }
        catch
        {
            slot.Dispose();
            _failed = true;
            throw Unavailable();
        }
        try
        {
            var first = await Task.WhenAny(_ready.Task, _process.Completion).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (first != _ready.Task) throw Unavailable();
            return await _ready.Task.ConfigureAwait(false);
        }
        catch
        {
            _failed = true;
            await _process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void BeginShutdown() => _lifetime.Cancel();

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _requests.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is { } process) await process.DisposeAsync().ConfigureAwait(false);
            _http.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        finally
        {
            _requests.Release();
            _requests.Dispose();
            _lifetime.Dispose();
        }
    }

    private static FrontendFailureException Unavailable() =>
        new("render_unavailable", "The output renderer is unavailable; display the original Markdown.");
}
