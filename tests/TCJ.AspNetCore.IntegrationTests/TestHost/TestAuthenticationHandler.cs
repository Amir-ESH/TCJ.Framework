using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TCJ.AspNetCore.IntegrationTests.TestHost;

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "TCJ-Test";
    internal const string AuthenticateHeader = "X-Test-Authenticate";
    internal const string AuthenticationFailureHeader = "X-Test-Authentication-Failure";
    internal const string UserIdHeader = "X-Test-UserId";
    internal const string DuplicateUserIdHeader = "X-Test-Duplicate-UserId";
    internal const string RoleHeader = "X-Test-Roles";
    internal const string ClaimHeader = "X-Test-Claims";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue(AuthenticationFailureHeader, out var failure)
            && string.Equals(failure.ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Deterministic test authentication failure."));
        }

        bool explicitlyAuthenticated = Request.Headers.TryGetValue(AuthenticateHeader, out var authenticate)
                                       && string.Equals(authenticate.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        bool hasUserId = Request.Headers.TryGetValue(UserIdHeader, out var userId);
        string userIdValue = userId.ToString();

        if (!explicitlyAuthenticated && !hasUserId)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>();
        if (hasUserId && !string.IsNullOrWhiteSpace(userIdValue))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userIdValue));
        }

        if (Request.Headers.TryGetValue(DuplicateUserIdHeader, out var duplicateUserId))
        {
            string duplicateUserIdValue = duplicateUserId.ToString();
            if (!string.IsNullOrWhiteSpace(duplicateUserIdValue))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, duplicateUserIdValue));
            }
        }

        if (Request.Headers.TryGetValue(RoleHeader, out var roles))
        {
            foreach (string role in Split(roles.ToString()))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        if (Request.Headers.TryGetValue(ClaimHeader, out var extraClaims))
        {
            foreach (string item in Split(extraClaims.ToString()))
            {
                int separator = item.IndexOf('=');
                if (separator > 0 && separator < item.Length - 1)
                {
                    claims.Add(new Claim(item[..separator], item[(separator + 1)..]));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.NameIdentifier, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static IEnumerable<string> Split(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
