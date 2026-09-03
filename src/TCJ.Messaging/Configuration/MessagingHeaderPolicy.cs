using System.Collections.ObjectModel;

namespace TCJ.Messaging.Configuration;

/// <summary>Filters and validates transport-neutral headers using an explicit allowlist.</summary>
public sealed class MessagingHeaderPolicy
{
    private static readonly HashSet<string> FrameworkAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "tcj-message-id", "tcj-message-type", "tcj-message-version", "tcj-correlation-id",
        "tcj-causation-id", "tcj-created-at", "traceparent", "tracestate", "content-type"
    };

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "api-key", "x-api-key",
        "access-token", "refresh-token", "password", "connection-string"
    };

    private readonly TcjMessagingOptions _options;

    /// <summary>Creates a bounded header policy from messaging options.</summary>
    /// <param name="options">Messaging size limits and additional allowlisted headers.</param>
    public MessagingHeaderPolicy(TcjMessagingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <summary>Filters one header set, removing forbidden, unallowlisted, or malformed trace-context values.</summary>
    /// <param name="headers">Input headers.</param>
    /// <returns>A copied read-only dictionary containing only allowed safe headers.</returns>
    public IReadOnlyDictionary<string, string> Filter(IReadOnlyDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                MessagingValidation.ValidateHeaderName(name, nameof(headers), _options.MaximumHeaderNameLength);
                MessagingValidation.ValidateHeaderValue(value, nameof(headers), _options.MaximumHeaderValueLength);
                if (IsForbiddenHeader(name) || (!FrameworkAllowed.Contains(name) && !_options.AdditionalAllowedHeaders.Contains(name)))
                    continue;
                if (string.Equals(name, "traceparent", StringComparison.OrdinalIgnoreCase) && !MessagingValidation.IsValidW3CTraceParent(value))
                    continue;
                if (string.Equals(name, "tracestate", StringComparison.OrdinalIgnoreCase) && !MessagingValidation.IsValidTraceState(value))
                    continue;
                result[name] = value;
            }
        }

        // tracestate is meaningless and potentially unsafe without a valid traceparent.
        if (result.ContainsKey("tracestate") && !result.ContainsKey("traceparent"))
            result.Remove("tracestate");
        ValidateLimits(result);
        return new ReadOnlyDictionary<string, string>(result);
    }

    /// <summary>Returns whether a header is forbidden by the framework security policy.</summary>
    /// <param name="headerName">Header name.</param>
    /// <returns><see langword="true"/> when the header is forbidden.</returns>
    public static bool IsForbiddenHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        return Forbidden.Contains(headerName) ||
               headerName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("connection-string", StringComparison.OrdinalIgnoreCase);
    }

    internal void ValidateLimits(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count > _options.MaximumHeaderCount)
            throw new ArgumentException($"Message headers exceed the configured {_options.MaximumHeaderCount}-header limit.", nameof(headers));
        int bytes = MessagingValidation.GetHeaderByteCount(headers);
        if (bytes > _options.MaximumHeaderBytes)
            throw new ArgumentException($"Message headers exceed the configured {_options.MaximumHeaderBytes}-byte limit.", nameof(headers));
    }
}
