using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Maieutics.Mcp;

internal delegate ValueTask<IClientTransport> McpClientTransportFactory(
    McpServerDefinition definition,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken);

internal enum McpServerTransportKind
{
    Stdio,
    Http
}

internal sealed record McpServerDefinition(
    string Id,
    McpServerTransportKind Transport,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    Uri? Endpoint,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Tools,
    TimeSpan InitializationTimeout,
    TimeSpan RequestTimeout,
    TimeSpan ShutdownTimeout,
    TimeSpan ConnectionTimeout,
    string GenerationKey)
{
    internal static string CreateGenerationKey(
        McpServerTransportKind transport,
        string? command,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IEnumerable<KeyValuePair<string, string?>> environmentVariables,
        Uri? endpoint,
        IEnumerable<KeyValuePair<string, string>> headers,
        IEnumerable<KeyValuePair<string, string>> tools,
        TimeSpan initializationTimeout,
        TimeSpan requestTimeout,
        TimeSpan shutdownTimeout,
        TimeSpan connectionTimeout)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Add(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        Add(transport.ToString());
        Add(command);
        foreach (var argument in arguments)
        {
            Add(argument);
        }

        Add(workingDirectory);
        foreach (var pair in environmentVariables.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Add(pair.Key);
            Add(pair.Value);
        }

        Add(endpoint?.AbsoluteUri);
        foreach (var pair in headers.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Add(pair.Key);
            Add(pair.Value);
        }

        foreach (var pair in tools.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Add(pair.Key);
            Add(pair.Value);
        }

        Add(initializationTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        Add(requestTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        Add(shutdownTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        Add(connectionTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal sealed class McpServerGeneration
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly McpServerDefinition definition;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly McpClientTransportFactory transportFactory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private readonly List<Task> retiredConnections = [];
    private McpConnectionGeneration? current;
    private Task supervisor = Task.CompletedTask;
    private Task? retirement;
    private TimeSpan? nextReconnectDelay;

    private McpServerGeneration(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        McpClientTransportFactory transportFactory,
        McpConnectionGeneration connection)
    {
        this.definition = definition;
        this.loggerFactory = loggerFactory;
        this.timeProvider = timeProvider;
        this.transportFactory = transportFactory;
        logger = loggerFactory.CreateLogger($"Maieutics.Mcp.{definition.Id}");
        current = connection;
    }

    internal string Id => definition.Id;

    internal string GenerationKey => definition.GenerationKey;

    internal static async Task<McpServerGeneration> CreateAsync(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        McpClientTransportFactory? transportFactory = null)
    {
        transportFactory ??= CreateTransportAsync;
        var connection = await CreateConnectionAsync(
            definition,
            loggerFactory,
            transportFactory,
            requireAllTools: true,
            cancellationToken).ConfigureAwait(false);
        var generation = new McpServerGeneration(
            definition,
            loggerFactory,
            timeProvider,
            transportFactory,
            connection);
        generation.supervisor = generation.SuperviseAsync();
        return generation;
    }

    internal McpServerLease? TryAcquire()
    {
        lock (gate)
        {
            if (retirement is not null || current is null || current.Client.Completion.IsCompleted)
            {
                return null;
            }

            return current.Acquire();
        }
    }

    internal MaieuticsMcpServerInfo GetInfo()
    {
        lock (gate)
        {
            if (current is { } connection && !connection.Client.Completion.IsCompleted)
            {
                var tools = connection.GetToolInfo();
                return new MaieuticsMcpServerInfo(
                    definition.Id,
                    definition.Transport.ToString(),
                    tools.Any(static tool => !tool.Available)
                        ? MaieuticsMcpServerState.Degraded
                        : MaieuticsMcpServerState.Connected,
                    null,
                    tools);
            }

            return new MaieuticsMcpServerInfo(
                definition.Id,
                definition.Transport.ToString(),
                MaieuticsMcpServerState.Reconnecting,
                nextReconnectDelay,
                definition.Tools
                    .OrderBy(static pair => pair.Value, StringComparer.Ordinal)
                    .Select(static pair => new MaieuticsMcpToolInfo(pair.Key, pair.Value, false))
                    .ToArray());
        }
    }

    internal Task Retire()
    {
        TaskCompletionSource? completion = null;
        Task result;
        lock (gate)
        {
            if (retirement is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                retirement = completion.Task;
            }

            result = retirement;
        }

        if (completion is not null)
        {
            _ = CompleteRetirementAsync(completion);
        }

        return result;
    }

    private async Task CompleteRetirementAsync(TaskCompletionSource completion)
    {
        try
        {
            await RetireCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task RetireCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await supervisor.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        McpConnectionGeneration? connection;
        Task[] retired;
        lock (gate)
        {
            connection = current;
            current = null;
            retired = retiredConnections.ToArray();
        }

        var currentRetirement = connection?.Retire() ?? Task.CompletedTask;
        await Task.WhenAll(retired.Append(currentRetirement)).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task SuperviseAsync()
    {
        var reconnectDelay = InitialReconnectDelay;
        while (!lifetime.IsCancellationRequested)
        {
            McpConnectionGeneration? connection;
            lock (gate)
            {
                connection = current;
            }

            if (connection is not null)
            {
                using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var refreshWait = connection.WaitForRefreshAsync(refreshCancellation.Token).AsTask();
                var completed = await Task.WhenAny(connection.Client.Completion, refreshWait).ConfigureAwait(false);
                if (completed == refreshWait)
                {
                    if (!await refreshWait.ConfigureAwait(false))
                    {
                        break;
                    }

                    connection.DrainRefreshSignals();
                    try
                    {
                        await connection.RefreshToolsAsync(requireAllTools: false, lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            "MCP server {ServerId} rejected a tool-list refresh ({FailureType}); the previous tool list remains active.",
                            definition.Id,
                            exception.GetType().Name);
                    }

                    continue;
                }

                await refreshCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await refreshWait.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
                {
                }

                var details = await connection.Client.Completion.ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(current, connection))
                    {
                        current = null;
                        retiredConnections.Add(connection.Retire());
                    }
                }

                logger.LogWarning(
                    "MCP server {ServerId} disconnected ({FailureType}); its tools are unavailable until reconnection.",
                    definition.Id,
                    details.Exception?.GetType().Name ?? "Closed");
            }

            lock (gate)
            {
                nextReconnectDelay = reconnectDelay;
            }

            try
            {
                await Task.Delay(reconnectDelay, timeProvider, lifetime.Token).ConfigureAwait(false);
                var replacement = await CreateConnectionAsync(
                    definition,
                    loggerFactory,
                    transportFactory,
                    requireAllTools: false,
                    lifetime.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (retirement is null)
                    {
                        current = replacement;
                        nextReconnectDelay = null;
                    }
                    else
                    {
                        retiredConnections.Add(replacement.Retire());
                    }
                }

                reconnectDelay = InitialReconnectDelay;
                logger.LogInformation("Reconnected MCP server {ServerId}.", definition.Id);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Could not reconnect MCP server {ServerId} ({FailureType}); another attempt will be made.",
                    definition.Id,
                    exception.GetType().Name);
                reconnectDelay = TimeSpan.FromTicks(Math.Min(
                    checked(reconnectDelay.Ticks * 2),
                    MaximumReconnectDelay.Ticks));
            }
        }
    }

    private static async Task<McpConnectionGeneration> CreateConnectionAsync(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory,
        McpClientTransportFactory transportFactory,
        bool requireAllTools,
        CancellationToken cancellationToken)
    {
        var refreshSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        var handlers = new McpClientHandlers
        {
            NotificationHandlers =
            [
                new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                    NotificationMethods.ToolListChangedNotification,
                    (_, _) =>
                    {
                        refreshSignals.Writer.TryWrite(0);
                        return ValueTask.CompletedTask;
                    })
            ]
        };
        var clientOptions = new McpClientOptions
        {
            InitializationTimeout = definition.InitializationTimeout,
            Handlers = handlers
        };

        var transport = await transportFactory(definition, loggerFactory, cancellationToken).ConfigureAwait(false);
        var client = await McpClient.CreateAsync(
            transport,
            clientOptions,
            loggerFactory,
            cancellationToken).ConfigureAwait(false);
        var connection = new McpConnectionGeneration(client, definition, refreshSignals);
        try
        {
            await connection.RefreshToolsAsync(requireAllTools, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.Retire().ConfigureAwait(false);
            throw;
        }
    }

    private static ValueTask<IClientTransport> CreateTransportAsync(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IClientTransport transport = definition.Transport switch
        {
            McpServerTransportKind.Stdio => CreateStdioTransport(definition, loggerFactory),
            McpServerTransportKind.Http => CreateHttpTransport(definition, loggerFactory),
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Transport, null)
        };
        return ValueTask.FromResult(transport);
    }

    private static StdioClientTransport CreateStdioTransport(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var pair in definition.EnvironmentVariables)
        {
            environment[pair.Key] = pair.Value;
        }

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = definition.Id,
                Command = definition.Command!,
                Arguments = definition.Arguments.ToArray(),
                WorkingDirectory = definition.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = definition.ShutdownTimeout
            },
            loggerFactory);
    }

    private static HttpClientTransport CreateHttpTransport(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory) =>
        new(
            new HttpClientTransportOptions
            {
                Name = definition.Id,
                Endpoint = definition.Endpoint!,
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = definition.ConnectionTimeout,
                AdditionalHeaders = new Dictionary<string, string>(
                    definition.Headers,
                    StringComparer.OrdinalIgnoreCase)
            },
            loggerFactory);

    internal sealed class McpConnectionGeneration(
        McpClient client,
        McpServerDefinition definition,
        Channel<byte> refreshSignals)
    {
        private readonly Lock gate = new();
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ImmutableArray<AIFunction> tools = [];
        private ImmutableArray<MaieuticsMcpToolInfo> toolInfo = [];
        private int references = 1;
        private bool retired;

        internal McpClient Client { get; } = client;

        internal McpServerLease Acquire()
        {
            lock (gate)
            {
                if (retired)
                {
                    throw new ObjectDisposedException(nameof(McpConnectionGeneration));
                }

                references = checked(references + 1);
                return new McpServerLease(this, tools);
            }
        }

        internal ImmutableArray<MaieuticsMcpToolInfo> GetToolInfo()
        {
            lock (gate)
            {
                return toolInfo;
            }
        }

        internal ValueTask<bool> WaitForRefreshAsync(CancellationToken cancellationToken) =>
            refreshSignals.Reader.WaitToReadAsync(cancellationToken);

        internal void DrainRefreshSignals()
        {
            while (refreshSignals.Reader.TryRead(out _))
            {
            }
        }

        internal async Task RefreshToolsAsync(bool requireAllTools, CancellationToken cancellationToken)
        {
            var discovered = await Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var byName = discovered.ToDictionary(static tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
            var exposed = ImmutableArray.CreateBuilder<AIFunction>(definition.Tools.Count);
            var info = ImmutableArray.CreateBuilder<MaieuticsMcpToolInfo>(definition.Tools.Count);
            foreach (var pair in definition.Tools.OrderBy(static pair => pair.Value, StringComparer.Ordinal))
            {
                if (byName.TryGetValue(pair.Key, out var tool))
                {
                    exposed.Add(new TimeoutAIFunction(tool.WithName(pair.Value), definition.RequestTimeout));
                    info.Add(new MaieuticsMcpToolInfo(pair.Key, pair.Value, true));
                }
                else
                {
                    info.Add(new MaieuticsMcpToolInfo(pair.Key, pair.Value, false));
                }
            }

            if (requireAllTools && info.Any(static tool => !tool.Available))
            {
                var missing = string.Join(", ", info.Where(static tool => !tool.Available)
                    .Select(static tool => $"'{tool.RemoteName}'"));
                throw new InvalidOperationException(
                    $"MCP server '{definition.Id}' does not expose configured tools: {missing}.");
            }

            lock (gate)
            {
                if (retired)
                {
                    throw new ObjectDisposedException(nameof(McpConnectionGeneration));
                }

                tools = exposed.MoveToImmutable();
                toolInfo = info.MoveToImmutable();
            }
        }

        internal Task Retire()
        {
            var dispose = false;
            lock (gate)
            {
                if (!retired)
                {
                    retired = true;
                    dispose = --references == 0;
                }
            }

            if (dispose)
            {
                StartDisposeClient();
            }

            return disposed.Task;
        }

        internal ValueTask ReleaseAsync()
        {
            var dispose = false;
            lock (gate)
            {
                if (references > 0)
                {
                    dispose = --references == 0 && retired;
                }
            }

            if (dispose)
            {
                StartDisposeClient();
                return new ValueTask(disposed.Task);
            }

            return ValueTask.CompletedTask;
        }

        private void StartDisposeClient() => _ = DisposeClientAndSignalAsync();

        private async Task DisposeClientAndSignalAsync()
        {
            try
            {
                refreshSignals.Writer.TryComplete();
                await Client.DisposeAsync().ConfigureAwait(false);
                disposed.TrySetResult();
            }
            catch (Exception exception)
            {
                disposed.TrySetException(exception);
            }
        }
    }

    private sealed class TimeoutAIFunction(AIFunction innerFunction, TimeSpan timeout)
        : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            return await InnerFunction.InvokeAsync(arguments, timeoutCancellation.Token).ConfigureAwait(false);
        }
    }

    internal sealed class McpServerLease(
        McpConnectionGeneration generation,
        ImmutableArray<AIFunction> tools) : IAsyncDisposable
    {
        private int disposed;

        internal IReadOnlyList<AIFunction> Tools { get; } = tools;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref disposed, 1) == 0
                ? generation.ReleaseAsync()
                : ValueTask.CompletedTask;
    }
}