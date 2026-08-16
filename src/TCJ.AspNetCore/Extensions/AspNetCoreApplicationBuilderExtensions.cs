using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace TCJ.AspNetCore.Extensions;

/// <summary>
/// Adds TCJ middleware to an ASP.NET Core request pipeline.
/// </summary>
public static class AspNetCoreApplicationBuilderExtensions
{
    /// <summary>
    /// Enables the registered exception handlers and Problem Details responses for empty error status codes.
    /// Call this early in the request pipeline.
    /// </summary>
    /// <param name="app">The application builder to configure.</param>
    /// <returns>The same application builder for chaining.</returns>
    public static IApplicationBuilder UseTcjAspNetCore(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();

        app.UseStatusCodePages(static async statusCodeContext =>
                               {
                                   HttpContext httpContext = statusCodeContext.HttpContext;

                                   await HttpResults.Problem(statusCode: httpContext.Response.StatusCode,
                                                             instance: httpContext.Request.Path)
                                                    .ExecuteAsync(httpContext);
                               });

        return app;
    }
}
