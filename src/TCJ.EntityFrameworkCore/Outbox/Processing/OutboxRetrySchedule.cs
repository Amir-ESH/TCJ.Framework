using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox.Processing;

internal static class OutboxRetrySchedule
{
    internal static TimeSpan GetDelay(Guid messageId, int attempt, TcjOutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        int exponent = Math.Min(attempt - 1, 30);
        double multiplier = Math.Pow(2d, exponent);
        double milliseconds = Math.Min(
            options.MaxRetryDelay.TotalMilliseconds,
            options.BaseRetryDelay.TotalMilliseconds * multiplier);

        if (options.UseJitter && milliseconds > 0d)
        {
            Span<byte> bytes = stackalloc byte[16];
            messageId.TryWriteBytes(bytes);
            uint seed = BitConverter.ToUInt32(bytes) ^ unchecked((uint)attempt * 2654435761u);
            double jitter = 0.8d + ((seed % 4001u) / 10000d); // [0.8, 1.2]
            milliseconds = Math.Min(options.MaxRetryDelay.TotalMilliseconds, milliseconds * jitter);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
