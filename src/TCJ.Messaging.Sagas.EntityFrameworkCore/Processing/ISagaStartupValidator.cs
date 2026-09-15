namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal interface ISagaStartupValidator
{
    Task ValidateAsync(CancellationToken cancellationToken = default);
}
