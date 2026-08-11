namespace TCJ.EntityFrameworkCore.Outbox;

/// <summary>Validates outbox registration, provider selection, and Entity Framework model mapping.</summary>
internal interface IOutboxStartupValidator
{
    /// <summary>Throws an actionable configuration exception when the outbox is not safe to process.</summary>
    internal Task ValidateAsync(CancellationToken cancellationToken = default);
}
