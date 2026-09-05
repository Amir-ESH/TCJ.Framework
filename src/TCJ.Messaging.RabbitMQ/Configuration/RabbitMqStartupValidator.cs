using RabbitMQ.Client.Exceptions;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.RabbitMQ.Topology;

namespace TCJ.Messaging.RabbitMQ.Configuration;

internal sealed class RabbitMqStartupValidator : IMessagingStartupValidator
{
    private readonly MessagingStartupValidator _neutral;
    private readonly RabbitMqTopologyManager _topology;
    private readonly TcjRabbitMqOptions _rabbit;
    private readonly TcjMessagingOptions _messaging;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _validated;

    internal RabbitMqStartupValidator(MessagingStartupValidator neutral, RabbitMqTopologyManager topology,
        TcjRabbitMqOptions rabbit, TcjMessagingOptions messaging)
    {
        _neutral = neutral ?? throw new ArgumentNullException(nameof(neutral));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _rabbit = rabbit ?? throw new ArgumentNullException(nameof(rabbit));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (_validated) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_validated) return;
            _rabbit.Validate();
            if (_messaging.EnableConsumer)
            {
                if (_rabbit.MaximumConcurrentMessages != _messaging.MaximumConcurrentMessages)
                    throw new InvalidOperationException("RabbitMQ MaximumConcurrentMessages must match TCJ Messaging MaximumConcurrentMessages when consumer processing is enabled.");
                if (_rabbit.Topology.RetryTopologies.Count == 0)
                    throw new InvalidOperationException("RabbitMQ consumer processing requires at least one finite retry/dead-letter topology.");
            }
            await _neutral.ValidateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _topology.EnsureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationInterruptedException)
            {
                throw new InvalidOperationException("RabbitMQ topology validation failed (PermanentTopology).");
            }
            catch (AuthenticationFailureException)
            {
                throw new InvalidOperationException("RabbitMQ startup validation failed (PermanentAuthentication).");
            }
            catch (BrokerUnreachableException exception) when (ContainsAuthenticationFailure(exception))
            {
                throw new InvalidOperationException("RabbitMQ startup validation failed (PermanentAuthentication).");
            }
            catch (BrokerUnreachableException)
            {
                throw new InvalidOperationException("RabbitMQ startup validation failed (TransientConnection).");
            }
            _validated = true;
        }
        finally { _gate.Release(); }
    }

    private static bool ContainsAuthenticationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is AuthenticationFailureException) return true;
        return false;
    }
}
