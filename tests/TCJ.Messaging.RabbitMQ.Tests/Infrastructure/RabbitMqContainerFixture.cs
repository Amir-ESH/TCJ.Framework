using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace TCJ.Messaging.RabbitMQ.Tests.Infrastructure;

public sealed class RabbitMqContainerFixture : IAsyncLifetime
{
    internal const string ContainerImage = "rabbitmq:4.3.5-alpine";
    private readonly string _username = $"tcj_{Guid.NewGuid():N}";
    private readonly string _password = CreatePassword();
    private readonly string _diagnosticsDirectory;
    private RabbitMqContainer? _container;
    private bool _ready;

    public RabbitMqContainerFixture()
    {
        string configured = Environment.GetEnvironmentVariable("TCJ_RABBITMQ_RESULTS_DIR") ?? string.Empty;
        _diagnosticsDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(FindRepositoryRoot(), "TestResults", "RabbitMQ", "diagnostics")
            : Path.Combine(Path.GetFullPath(configured, FindRepositoryRoot()), "diagnostics");
    }

    internal string HostName => _container?.Hostname ?? throw new InvalidOperationException("RabbitMQ container has not started.");
    internal int Port => _container?.GetMappedPublicPort(RabbitMqBuilder.RabbitMqPort) ?? throw new InvalidOperationException("RabbitMQ container has not started.");
    internal string UserName => _username;
    internal string Password => _password;
    internal bool IsReady => _ready;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_diagnosticsDirectory);
        _container = new RabbitMqBuilder(ContainerImage)
            .WithUsername(_username)
            .WithPassword(_password)
            .WithLabel("tcj.rabbitmq.integration", "true")
            .WithCleanUp(true)
            .Build();
        await StartAndProbeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is null) return;
        await WriteContainerLogsAsync().ConfigureAwait(false);
        try
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _container = null;
            _ready = false;
        }
    }

    internal ConnectionFactory CreateConnectionFactory() => new()
    {
        HostName = HostName,
        Port = Port,
        UserName = _username,
        Password = _password,
        VirtualHost = "/",
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
        NetworkRecoveryInterval = TimeSpan.FromMilliseconds(250)
    };

    internal async Task StopAsync()
    {
        if (_container is null || !_ready) return;
        await _container.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _ready = false;
    }

    internal async Task EnsureRunningAsync()
    {
        if (_ready) return;
        if (_container is null) throw new InvalidOperationException("RabbitMQ container has not been created.");
        await StartAndProbeAsync().ConfigureAwait(false);
    }

    private async Task StartAndProbeAsync()
    {
        if (_container is null) throw new InvalidOperationException("RabbitMQ container has not been created.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await _container.StartAsync(timeout.Token).ConfigureAwait(false);
            await using IConnection connection = await CreateConnectionFactory().CreateConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            _ready = connection.IsOpen && channel.IsOpen;
            if (!_ready) throw new InvalidOperationException("RabbitMQ readiness probe did not open a connection and channel.");
        }
        catch (Exception exception)
        {
            await WriteFailureAsync("startup", exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task WriteContainerLogsAsync()
    {
        if (_container is null) return;
        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
            string content = $"STDOUT{Environment.NewLine}{stdout}{Environment.NewLine}STDERR{Environment.NewLine}{stderr}";
            await File.WriteAllTextAsync(Path.Combine(_diagnosticsDirectory, "container.log"), Sanitize(content)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteFailureAsync("log-collection", exception).ConfigureAwait(false);
        }
    }

    private async Task WriteFailureAsync(string name, Exception exception)
    {
        Directory.CreateDirectory(_diagnosticsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_diagnosticsDirectory, $"{name}.log"), Sanitize(exception.ToString())).ConfigureAwait(false);
    }

    private string Sanitize(string value)
    {
        string result = value.Replace(_username, "<redacted-user>", StringComparison.Ordinal)
            .Replace(_password, "<redacted-password>", StringComparison.Ordinal);
        result = Regex.Replace(result, @"(?i)amqps?://[^\s:/]+:[^\s@]+@", "amqp://<redacted>@", RegexOptions.CultureInvariant);
        result = Regex.Replace(result, @"(?i)(password|secret|access[-_]?token|api[-_]?key)\s*[:=]\s*[^\s;]+", "$1=<redacted>", RegexOptions.CultureInvariant);
        return result;
    }

    private static string CreatePassword() => $"Tcj!Aa1_{Convert.ToHexString(RandomNumberGenerator.GetBytes(18))}";

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TCJ.slnx"))) return current.FullName;
            current = current.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RabbitMqIntegrationCollection : ICollectionFixture<RabbitMqContainerFixture>
{
    public const string Name = "RabbitMQ integration";
}
