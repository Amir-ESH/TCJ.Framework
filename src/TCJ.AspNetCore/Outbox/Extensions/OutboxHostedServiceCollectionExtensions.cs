using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TCJ.Core.Outbox;

namespace TCJ.AspNetCore.Outbox.Extensions;

/// <summary>Registers optional hosted transactional-outbox polling.</summary>
public static class OutboxHostedServiceCollectionExtensions
{
    /// <summary>
    /// Adds one non-overlapping hosted outbox loop. Manual processing through <see cref="IOutboxProcessor"/> remains available without this service.
    /// </summary>
    /// <param name="services">Service collection that already contains transactional-outbox persistence services.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjOutboxProcessor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(TcjOutboxOptions)))
        {
            throw new InvalidOperationException("Register transactional outbox services before AddTcjOutboxProcessor. The hosted service intentionally does not configure persistence or a database provider.");
        }

        lock (services)
        {
            bool alreadyRegistered = services.Any(static descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(OutboxHostedService));
            if (!alreadyRegistered)
            {
                services.AddHostedService<OutboxHostedService>();
            }
        }

        return services;
    }
}
