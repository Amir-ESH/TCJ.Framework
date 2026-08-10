namespace TCJ.Core.HealthChecks;

/// <summary>Represents one sanitized and actionable startup diagnostic.</summary>
public sealed record TcjStartupDiagnostic
{
    /// <summary>Creates a startup diagnostic.</summary>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="message">Sanitized actionable diagnostic message.</param>
    /// <param name="severity">Diagnostic severity.</param>
    public TcjStartupDiagnostic(string code, string message, TcjStartupDiagnosticSeverity severity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Code = code.Trim();
        Message = message.Trim();
        Severity = severity;
    }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }
    /// <summary>Gets the sanitized actionable diagnostic message.</summary>
    public string Message { get; }
    /// <summary>Gets the diagnostic severity.</summary>
    public TcjStartupDiagnosticSeverity Severity { get; }
}
