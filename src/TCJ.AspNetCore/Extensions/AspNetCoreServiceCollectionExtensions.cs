using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Diagnostics;
using TCJ.AspNetCore.Options;
using TCJ.AspNetCore.Security;
using TCJ.Core.Security;

namespace TCJ.AspNetCore.Extensions;

/// <summary>
/// Registers TCJ services for ASP.NET Core applications.
/// </summary>
public static class AspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers current-user resolution, Problem Details, and the default exception handler.
    /// </summary>
    public static IServiceCollection AddTcjAspNetCore(this IServiceCollection services, Action<TcjAspNetCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<TcjAspNetCoreOptions> optionsBuilder = services.AddOptions<TcjAspNetCoreOptions>();

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.Validate(validation: static options => options.UserIdResolver is not null
                                                           || !string.IsNullOrWhiteSpace(options.UserIdClaimType),
                                failureMessage: $"{nameof(TcjAspNetCoreOptions.UserIdClaimType)} cannot be empty when no custom user-id resolver is configured.")

                      .Validate(validation: static options => !string.IsNullOrWhiteSpace(options.UnexpectedErrorTitle),
                                failureMessage: $"{nameof(TcjAspNetCoreOptions.UnexpectedErrorTitle)} cannot be empty.")

                      .Validate(validation: static options => !string.IsNullOrWhiteSpace(options.UnexpectedErrorDetail),
                                failureMessage: $"{nameof(TcjAspNetCoreOptions.UnexpectedErrorDetail)} cannot be empty.")

                      .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUserProvider, HttpContextCurrentUserProvider>();

        services.AddProblemDetails(problemDetailsOptions =>
        {
            Action<ProblemDetailsContext>? existingCustomizer = problemDetailsOptions.CustomizeProblemDetails;

            problemDetailsOptions.CustomizeProblemDetails = context => 
                                                            { 
                                                                existingCustomizer?.Invoke(context);
                                                                
                                                                if (!context.ProblemDetails.Extensions.ContainsKey("traceId"))
                                                                {
                                                                    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                                                                }
                                                            };
        });

        bool handlerRegistered = services.Any(static descriptor => descriptor.ServiceType == typeof(IExceptionHandler)
                                                                && descriptor.ImplementationType == typeof(TcjExceptionHandler));

        if (!handlerRegistered)
        {
            services.AddExceptionHandler<TcjExceptionHandler>();
        }

        return services;
    }
}
