using System.Security.Claims;

namespace TCJ.AspNetCore.Options;

/// <summary>
/// Configures TCJ integrations for ASP.NET Core applications.
/// </summary>
public sealed class TcjAspNetCoreOptions
{
    /// <summary>
    /// Gets or sets the claim type containing the numeric current-user identifier.
    /// </summary>
    public string UserIdClaimType { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>
    /// Gets or sets an optional custom resolver for the numeric current-user identifier.
    /// When supplied, this resolver takes precedence over <see cref="UserIdClaimType"/>.
    /// </summary>
    public Func<ClaimsPrincipal, long?>? UserIdResolver { get; set; }

    /// <summary>
    /// Gets or sets whether unhandled-exception messages may be returned to clients.
    /// The default is <see langword="false"/> to avoid disclosing server details.
    /// </summary>
    public bool IncludeExceptionDetails { get; set; }

    /// <summary>
    /// Gets or sets the title returned for an unhandled server exception.
    /// </summary>
    public string UnexpectedErrorTitle { get; set; } = "An unexpected server error occurred.";

    /// <summary>
    /// Gets or sets the safe detail returned when exception details are disabled.
    /// </summary>
    public string UnexpectedErrorDetail { get; set; } = "The server could not process the request.";
}
