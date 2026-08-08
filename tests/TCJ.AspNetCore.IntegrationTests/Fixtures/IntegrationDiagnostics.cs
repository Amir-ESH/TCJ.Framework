using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TCJ.AspNetCore.IntegrationTests.Fixtures;

internal sealed partial class IntegrationDiagnostics : ILoggerProvider
{
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly string _environmentName;
    private readonly string _diagnosticsDirectory;

    public IntegrationDiagnostics(string environmentName)
    {
        _environmentName = environmentName;
        string resultsRoot = Environment.GetEnvironmentVariable("TCJ_ASPNETCORE_RESULTS_DIR")
                             ?? Path.Combine("TestResults", "AspNetCoreIntegration");
        _diagnosticsDirectory = Path.GetFullPath(Path.Combine(resultsRoot, "diagnostics"));
        Directory.CreateDirectory(_diagnosticsDirectory);
        Append("environment-summary.txt",
               $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} environment={_environmentName}");
    }

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(this, categoryName);

    public void Dispose()
    {
    }

    public HttpClient CreateClient(HttpMessageHandler innerHandler)
        => new(new DiagnosticHttpMessageHandler(innerHandler, this), disposeHandler: true)
        {
            BaseAddress = new Uri("http://localhost", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(20),
        };

    public void RecordEndpoint(string? endpoint)
        => Append("host.log", $"environment={_environmentName} endpoint={Sanitize(endpoint ?? "<none>")}");

    private void RecordLog(LogLevel level, string category, string message, Exception? exception)
    {
        string safeMessage = Sanitize(message);
        string safeException = exception is null ? string.Empty : $" exception={Sanitize(exception.GetType().Name + ": " + exception.Message)}";
        _entries.Enqueue(new LogEntry(level, category, safeMessage));
        Append("host.log", $"environment={_environmentName} level={level} category={Sanitize(category)} message={safeMessage}{safeException}");
    }

    private void RecordHttp(HttpRequestMessage request, HttpStatusCode? statusCode, string? body, Exception? exception)
    {
        string status = statusCode is null ? "exception" : ((int)statusCode.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string exceptionText = exception is null ? string.Empty : $" exception={Sanitize(exception.GetType().Name)}";
        Append("http.log", $"environment={_environmentName} method={request.Method} path={Sanitize(request.RequestUri?.PathAndQuery ?? "/")} status={status} body={Sanitize(body ?? string.Empty)}{exceptionText}");
    }

    private void Append(string fileName, string line)
    {
        lock (_fileLock)
        {
            File.AppendAllText(Path.Combine(_diagnosticsDirectory, fileName), line + Environment.NewLine, Encoding.UTF8);
        }
    }

    internal static string Sanitize(string value)
    {
        string sanitized = AuthorizationRegex().Replace(value, "$1<redacted>");
        sanitized = BearerRegex().Replace(sanitized, "Bearer <redacted>");
        sanitized = CookieRegex().Replace(sanitized, "$1<redacted>");
        sanitized = PasswordRegex().Replace(sanitized, "$1=<redacted>");
        return sanitized.Replace('\r', ' ').Replace('\n', ' ');
    }

    [GeneratedRegex("(?i)(Authorization\\s*[:=]\\s*)[^;\\s]+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)((?:Cookie|Set-Cookie)\\s*[:=]\\s*)[^;\\r\\n]+")]
    private static partial Regex CookieRegex();

    [GeneratedRegex("(?i)(Password|Pwd)\\s*=\\s*[^;\\s]+")]
    private static partial Regex PasswordRegex();

    internal sealed record LogEntry(LogLevel Level, string Category, string Message);

    private sealed class DiagnosticLogger(IntegrationDiagnostics owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.RecordLog(logLevel, category, formatter(state, exception), exception);
            }
        }
    }

    private sealed class DiagnosticHttpMessageHandler(HttpMessageHandler innerHandler, IntegrationDiagnostics owner)
        : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string body = response.Content is null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                owner.RecordHttp(request, response.StatusCode, body, exception: null);
                return response;
            }
            catch (Exception exception)
            {
                owner.RecordHttp(request, statusCode: null, body: null, exception);
                throw;
            }
        }
    }
}
