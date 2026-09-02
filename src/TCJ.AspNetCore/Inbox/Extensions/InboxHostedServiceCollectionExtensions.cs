using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TCJ.Core.Inbox;

namespace TCJ.AspNetCore.Inbox.Extensions;

/// <summary>Registers optional hosted deferred Inbox processing.</summary>
public static class InboxHostedServiceCollectionExtensions
{
    /// <summary>Adds one hosted deferred Inbox polling loop without configuring persistence or a transport.</summary>
    /// <param name="services">Service collection that already contains transactional Inbox persistence.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjInboxProcessor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(TcjInboxOptions))) throw new InvalidOperationException("Register transactional Inbox services before AddTcjInboxProcessor.");
        lock (services)
        {
            bool exists = services.Any(static descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(InboxHostedService));
            if (!exists) services.AddHostedService<InboxHostedService>();
        }
        return services;
    }
}
