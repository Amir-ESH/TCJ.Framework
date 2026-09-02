namespace TCJ.EntityFrameworkCore.Inbox.Processing;

internal static class InboxRetrySchedule
{
    internal static DateTimeOffset Calculate(DateTimeOffset now, int attempt, Tcj.Core.Inbox.TcjInboxOptions options)
    {
        double exponent = Math.Pow(2d, Math.Max(0, attempt - 1));
        double milliseconds = Math.Min(options.MaxRetryDelay.TotalMilliseconds, options.BaseRetryDelay.TotalMilliseconds * exponent);
        if (options.UseJitter)
        {
            uint bucket = unchecked((uint)HashCode.Combine(attempt, now.UtcTicks)) % 1000u;
            milliseconds = Math.Min(options.MaxRetryDelay.TotalMilliseconds, milliseconds * (0.8d + (bucket / 2500d)));
        }
        return now + TimeSpan.FromMilliseconds(Math.Max(1d, milliseconds));
    }
}
