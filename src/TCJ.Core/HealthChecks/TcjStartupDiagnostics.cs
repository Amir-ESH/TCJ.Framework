using System.Collections.Concurrent;

namespace TCJ.Core.HealthChecks;

/// <summary>Stores sanitized framework startup diagnostics for readiness evaluation.</summary>
public sealed class TcjStartupDiagnostics
{
    private readonly ConcurrentDictionary<string, TcjStartupDiagnostic> _diagnostics = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces a diagnostic by stable code.</summary>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="message">Sanitized actionable diagnostic message.</param>
    /// <param name="severity">Diagnostic severity.</param>
    public void Report(string code, string message, TcjStartupDiagnosticSeverity severity = TcjStartupDiagnosticSeverity.Error)
        => _diagnostics[code] = new TcjStartupDiagnostic(code, message, severity);

    /// <summary>Removes a previously reported diagnostic.</summary>
    /// <param name="code">Stable diagnostic code to remove.</param>
    /// <returns><see langword="true"/> when a diagnostic was removed; otherwise <see langword="false"/>.</returns>
    public bool Clear(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return _diagnostics.TryRemove(code, out _);
    }

    /// <summary>Returns an ordered snapshot of current startup diagnostics.</summary>
    /// <returns>An immutable ordered snapshot of current diagnostics.</returns>
    public IReadOnlyList<TcjStartupDiagnostic> GetSnapshot()
        => _diagnostics.Values.OrderBy(static item => item.Code, StringComparer.Ordinal).ToArray();

    /// <summary>Gets whether an error or fatal startup diagnostic is present.</summary>
    public bool HasErrors => _diagnostics.Values.Any(static item => item.Severity is TcjStartupDiagnosticSeverity.Error or TcjStartupDiagnosticSeverity.Fatal);

    /// <summary>Gets whether a fatal startup diagnostic is present.</summary>
    public bool HasFatalErrors => _diagnostics.Values.Any(static item => item.Severity == TcjStartupDiagnosticSeverity.Fatal);

    /// <summary>Gets whether a warning startup diagnostic is present.</summary>
    public bool HasWarnings => _diagnostics.Values.Any(static item => item.Severity == TcjStartupDiagnosticSeverity.Warning);
}
