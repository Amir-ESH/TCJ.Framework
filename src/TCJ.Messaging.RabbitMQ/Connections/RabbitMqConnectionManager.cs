using System.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Diagnostics;

namespace TCJ.Messaging.RabbitMQ.Connections;

internal sealed class RabbitMqConnectionManager : IAsyncDisposable
{
    private readonly TcjRabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    internal RabbitMqConnectionManager(TcjRabbitMqOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    internal bool IsOpen => _connection?.IsOpen == true;

    internal async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IConnection? current = Volatile.Read(ref _connection);
        if (current?.IsOpen == true) return current;
        if (current is not null && _options.AutomaticRecoveryEnabled)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(_options.ConnectionTimeout);
            try
            {
                while (!current.IsOpen)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), waitCts.Token).ConfigureAwait(false);
                }
                return current;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Recovery did not complete within the bounded connect window; replace the unusable connection below.
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection?.IsOpen == true) return _connection;
            if (_connection is not null)
            {
                await DisposeConnectionAsync(_connection).ConfigureAwait(false);
                _connection = null;
            }

            using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.ConnectActivity, "connect");
            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                RequestedConnectionTimeout = _options.ConnectionTimeout,
                AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
                TopologyRecoveryEnabled = _options.TopologyRecoveryEnabled,
                NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
                ClientProvidedName = _options.ClientProvidedName,
                ConsumerDispatchConcurrency = 1,
                Ssl = new SslOption
                {
                    Enabled = _options.UseTls,
                    ServerName = _options.TlsServerName ?? _options.HostName
                }
            };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_options.ConnectionTimeout);
            IConnection connection = await factory.CreateConnectionAsync(connectCts.Token).ConfigureAwait(false);
            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
            connection.ConnectionRecoveryErrorAsync += OnRecoveryErrorAsync;
            _connection = connection;
            RabbitMqDiagnostics.ConnectionOpened();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return connection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("RabbitMQ connection establishment exceeded the configured timeout.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.ConnectActivity, "connection.shutdown");
        activity?.SetStatus(ActivityStatusCode.Error, "connection_shutdown");
        return Task.CompletedTask;
    }

    private static Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.RecoverActivity, "recover");
        activity?.SetStatus(ActivityStatusCode.Ok);
        RabbitMqDiagnostics.Recovered();
        return Task.CompletedTask;
    }

    private static Task OnRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs args)
    {
        using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.RecoverActivity, "recover");
        activity?.SetStatus(ActivityStatusCode.Error, "recovery_failed");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await DisposeConnectionAsync(_connection).ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task DisposeConnectionAsync(IConnection connection)
    {
        connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
        connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
        connection.ConnectionRecoveryErrorAsync -= OnRecoveryErrorAsync;
        try
        {
            if (connection.IsOpen)
                await connection.CloseAsync(Constants.ReplySuccess, "TCJ adapter shutdown", _options.ShutdownTimeout, abort: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        await connection.DisposeAsync().ConfigureAwait(false);
        RabbitMqDiagnostics.ConnectionClosed();
    }
}

