using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

internal sealed class SagaMessageRegistrar<TDbContext>(IServiceCollection services) : ISagaMessageRegistrar
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    public void Register<TMessage>(string sagaType, string messageType, int messageVersion)
    {
        lock (services)
        {
            SagaMessageBinding? existing = services.Where(static d => d.ServiceType == typeof(SagaMessageBinding))
                .Select(static d => d.ImplementationInstance).OfType<SagaMessageBinding>()
                .FirstOrDefault(binding => binding.MessageType == typeof(TMessage));
            if (existing is not null && !string.Equals(existing.SagaType, sagaType, StringComparison.Ordinal))
                throw new InvalidOperationException($"Saga message CLR type '{typeof(TMessage).FullName}' is already bound to Saga '{existing.SagaType}'. Multiple Saga Inbox handlers are not allowed.");
            if (existing is null) services.AddSingleton(new SagaMessageBinding(typeof(TMessage), sagaType));
        }
        services.AddTcjInboxMessage<TMessage>(messageType, messageVersion);
        services.AddTcjInboxHandler<TMessage, SagaInboxMessageHandler<TDbContext, TMessage>>();
    }
}
