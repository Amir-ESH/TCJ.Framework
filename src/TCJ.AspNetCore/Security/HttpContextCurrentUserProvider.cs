using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Options;
using TCJ.Core.Security;

namespace TCJ.AspNetCore.Security;

/// <summary>
/// Resolves the current numeric user identifier from the active HTTP request.
/// </summary>
public sealed class HttpContextCurrentUserProvider(IHttpContextAccessor httpContextAccessor, IOptions<TcjAspNetCoreOptions> options)
    : ICurrentUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor
                                                              ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    private readonly TcjAspNetCoreOptions _options = options.Value
                                                  ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public long? UserId
    {
        get
        {
            ClaimsPrincipal? principal = _httpContextAccessor.HttpContext?.User;

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            if (_options.UserIdResolver is not null)
            {
                return _options.UserIdResolver(principal);
            }

            string? value = principal.FindFirst(_options.UserIdClaimType)?.Value;

            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) ? userId : null;
        }
    }
}
