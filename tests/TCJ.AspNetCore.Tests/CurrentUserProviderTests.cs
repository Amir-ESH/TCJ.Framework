using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TCJ.AspNetCore.Options;
using TCJ.AspNetCore.Security;

namespace TCJ.AspNetCore.Tests;

public sealed class CurrentUserProviderTests
{
    [Fact]
    public void Authenticated_numeric_claim_is_resolved()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "42")], authenticationType: "Test");
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
            },
        };

        var provider = new HttpContextCurrentUserProvider(accessor, Microsoft.Extensions.Options.Options.Create(new TcjAspNetCoreOptions()));

        Assert.Equal(42L, provider.UserId);
    }

    [Fact]
    public void Non_numeric_claim_returns_null()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(type: ClaimTypes.NameIdentifier, "not-a-number")], authenticationType: "Test"))
            },
        };

        var provider = new HttpContextCurrentUserProvider(accessor, Microsoft.Extensions.Options.Options.Create(new TcjAspNetCoreOptions()));

        Assert.Null(provider.UserId);
    }

    [Fact]
    public void Custom_resolver_takes_precedence_over_claim_type()
    {
        var identity = new ClaimsIdentity([], authenticationType: "Test");
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
            }
        };
        var options = new TcjAspNetCoreOptions
        {
            UserIdClaimType = string.Empty,
            UserIdResolver = _ => 99
        };

        var provider = new HttpContextCurrentUserProvider(accessor, Microsoft.Extensions.Options.Options.Create(options));

        Assert.Equal(99L, provider.UserId);
    }
}
