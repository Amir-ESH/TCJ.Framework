namespace TCJ.Messaging.HealthChecks;

/// <summary>Optional adapter readiness probe returning sanitized bounded readiness only.</summary>
public interface IMessagingTransportHealthProbe
{
    /// <summary>Checks transport readiness.</summary><param name="cancellationToken">Caller token.</param><returns>Readiness.</returns>
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
