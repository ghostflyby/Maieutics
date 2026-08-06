using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StdioMcpTransportDefinition), "stdio")]
[JsonDerivedType(typeof(HttpMcpTransportDefinition), "http")]
internal abstract record McpTransportDefinition
{
    internal abstract McpServerTransportKind Kind { get; }
}

internal sealed record StdioMcpTransportDefinition(
    [property: JsonPropertyName("command")]
    string Command,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Arguments = null,
    [property: JsonPropertyName("workingDirectory")]
    string? WorkingDirectory = null,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string?>? EnvironmentVariables = null)
    : McpTransportDefinition
{
    internal override McpServerTransportKind Kind => McpServerTransportKind.Stdio;
}

internal sealed record HttpMcpTransportDefinition(
    [property: JsonPropertyName("url")] Uri Endpoint,
    [property: JsonPropertyName("headers")]
    IReadOnlyDictionary<string, string>? Headers = null)
    : McpTransportDefinition
{
    internal override McpServerTransportKind Kind => McpServerTransportKind.Http;
}

internal sealed record McpServerDefinition(
    string Id,
    McpTransportDefinition Transport,
    TimeSpan InitializationTimeout,
    TimeSpan RequestTimeout,
    TimeSpan ShutdownTimeout,
    TimeSpan ConnectionTimeout,
    string GenerationKey)
{
    internal static string CreateGenerationKey(
        McpTransportDefinition transport,
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

        Add(transport.Kind.ToString());
        switch (transport)
        {
            case StdioMcpTransportDefinition stdio:
                Add(stdio.Command);
                foreach (var argument in stdio.Arguments ?? []) Add(argument);

                Add(stdio.WorkingDirectory);
                foreach (var pair in (stdio.EnvironmentVariables ?? new Dictionary<string, string?>())
                         .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    Add(pair.Key);
                    Add(pair.Value);
                }

                break;

            case HttpMcpTransportDefinition http:
                Add(http.Endpoint.AbsoluteUri);
                foreach (var pair in (http.Headers ?? new Dictionary<string, string>())
                         .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Add(pair.Key);
                    Add(pair.Value);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transport),
                    transport,
                    "Unknown transport definition.");
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
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly IReadOnlySet<string>? reservedToolNames;
    private readonly List<Task> retiredConnections = [];
    private readonly TimeProvider timeProvider;
    private readonly McpClientTransportFactory transportFactory;
    private McpConnectionGeneration? current;
    private TimeSpan? nextReconnectDelay;
    private Task? retirement;
    private Task supervisor = Task.CompletedTask;

    private McpServerGeneration(
        McpServerDefinition definition,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        McpClientTransportFactory transportFactory,
        IReadOnlySet<string>? reservedToolNames,
        McpConnectionGeneration connection)
    {
        this.definition = definition;
        this.loggerFactory = loggerFactory;
        this.timeProvider = timeProvider;
        this.transportFactory = transportFactory;
        this.reservedToolNames = reservedToolNames;
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
        McpClientTransportFactory? transportFactory = null,
        IReadOnlySet<string>? reservedToolNames = null)
    {
        transportFactory ??= CreateTransportAsync;
        var connection = await CreateConnectionAsync(
            definition,
            loggerFactory,
            transportFactory,
            cancellationToken,
            reservedToolNames).ConfigureAwait(false);
        var generation = new McpServerGeneration(
            definition,
            loggerFactory,
            timeProvider,
            transportFactory,
            reservedToolNames,
            connection);
        generation.supervisor = generation.SuperviseAsync();
        return generation;
    }

    internal McpServerLease? TryAcquire()
    {
        lock (gate)
        {
            if (retirement is not null || current is null || current.Client.Completion.IsCompleted) return null;

            return current.Acquire();
        }
    }

    internal MaieuticsMcpServerInfo GetInfo()
    {
        lock (gate)
        {
            if (current is not { Client.Completion.IsCompleted: false } connection)
                return new MaieuticsMcpServerInfo(
                    definition.Id,
                    definition.Transport.Kind.ToString(),
                    MaieuticsMcpServerState.Reconnecting,
                    nextReconnectDelay,
                    []);
            var tools = connection.GetToolInfo();
            return new MaieuticsMcpServerInfo(
                definition.Id,
                definition.Transport.Kind.ToString(),
                tools.Any(static tool => !tool.Available)
                    ? MaieuticsMcpServerState.Degraded
                    : MaieuticsMcpServerState.Connected,
                null,
                tools);
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

        if (completion is not null) _ = CompleteRetirementAsync(completion);

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
                    if (!await refreshWait.ConfigureAwait(false)) break;

                    connection.DrainRefreshSignals();
                    try
                    {
                        await connection.RefreshToolsAsync(lifetime.Token)
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
                    lifetime.Token,
                    reservedToolNames).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        IReadOnlySet<string>? reservedToolNames)
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
        var connection = new McpConnectionGeneration(client, definition, refreshSignals, reservedToolNames);
        try
        {
            await connection.RefreshToolsAsync(cancellationToken).ConfigureAwait(false);
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
            StdioMcpTransportDefinition stdio => CreateStdioTransport(
                definition.Id,
                stdio,
                definition.ShutdownTimeout,
                loggerFactory),
            HttpMcpTransportDefinition http => CreateHttpTransport(
                definition.Id,
                http,
                definition.ConnectionTimeout,
                loggerFactory),
            _ => throw new ArgumentOutOfRangeException(
                nameof(definition),
                definition.Transport,
                "Unknown transport definition.")
        };
        return ValueTask.FromResult(transport);
    }

    private static StdioClientTransport CreateStdioTransport(
        string id,
        StdioMcpTransportDefinition transport,
        TimeSpan shutdownTimeout,
        ILoggerFactory loggerFactory)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var pair in transport.EnvironmentVariables ?? new Dictionary<string, string?>(StringComparer.Ordinal))
            environment[pair.Key] = pair.Value;

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = id,
                Command = transport.Command,
                Arguments = [.. transport.Arguments ?? []],
                WorkingDirectory = transport.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = shutdownTimeout
            },
            loggerFactory);
    }

    private static HttpClientTransport CreateHttpTransport(
        string id,
        HttpMcpTransportDefinition transport,
        TimeSpan connectionTimeout,
        ILoggerFactory loggerFactory)
    {
        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = id,
                Endpoint = transport.Endpoint,
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = connectionTimeout,
                AdditionalHeaders = transport.Headers is { } headers
                    ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            },
            loggerFactory);
    }

    internal sealed class McpConnectionGeneration(
        McpClient client,
        McpServerDefinition definition,
        Channel<byte> refreshSignals,
        IReadOnlySet<string>? reservedToolNames)
    {
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private int references = 1;
        private bool retired;
        private ImmutableArray<MaieuticsMcpToolInfo> toolInfo = [];
        private ImmutableArray<AIFunction> tools = [];

        internal McpClient Client { get; } = client;

        internal McpServerLease Acquire()
        {
            lock (gate)
            {
                if (retired) throw new ObjectDisposedException(nameof(McpConnectionGeneration));

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

        internal ValueTask<bool> WaitForRefreshAsync(CancellationToken cancellationToken)
        {
            return refreshSignals.Reader.WaitToReadAsync(cancellationToken);
        }

        internal void DrainRefreshSignals()
        {
            while (refreshSignals.Reader.TryRead(out _))
            {
            }
        }

        internal async Task RefreshToolsAsync(CancellationToken cancellationToken)
        {
            var discovered = await Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var exposed = ImmutableArray.CreateBuilder<AIFunction>();
            var info = ImmutableArray.CreateBuilder<MaieuticsMcpToolInfo>();
            foreach (var tool in discovered.OrderBy(static value => value.ProtocolTool.Name, StringComparer.Ordinal))
            {
                var name = tool.ProtocolTool.Name;
                if (reservedToolNames is not null && reservedToolNames.Contains(name))
                {
                    info.Add(new MaieuticsMcpToolInfo(name, name, false));
                    continue;
                }

                exposed.Add(new TimeoutAIFunction(tool, definition.RequestTimeout));
                info.Add(new MaieuticsMcpToolInfo(name, name, true));
            }

            lock (gate)
            {
                if (retired) throw new ObjectDisposedException(nameof(McpConnectionGeneration));

                tools = exposed.ToImmutable();
                toolInfo = info.ToImmutable();
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

            if (dispose) StartDisposeClient();

            return disposed.Task;
        }

        internal ValueTask ReleaseAsync()
        {
            var dispose = false;
            lock (gate)
            {
                if (references > 0) dispose = --references == 0 && retired;
            }

            if (dispose)
            {
                StartDisposeClient();
                return new ValueTask(disposed.Task);
            }

            return ValueTask.CompletedTask;
        }

        private void StartDisposeClient()
        {
            _ = DisposeClientAndSignalAsync();
        }

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

    // ReSharper disable once InconsistentNaming
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

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref disposed, 1) == 0
                ? generation.ReleaseAsync()
                : ValueTask.CompletedTask;
        }
    }
}