namespace TCJ.EntityFrameworkCore.Inbox.Processing;

internal interface IInboxStartupValidator
{
    Task ValidateAsync(CancellationToken cancellationToken = default);
}
