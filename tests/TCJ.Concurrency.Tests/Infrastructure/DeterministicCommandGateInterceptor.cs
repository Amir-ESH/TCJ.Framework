using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TCJ.Concurrency.Tests.Infrastructure;

internal sealed class DeterministicCommandGateInterceptor : DbCommandInterceptor
{
    private const string CommandMarker = "TCJ_CONCURRENCY_GATE";
    private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blocked;

    public Task WaitUntilBlockedAsync(CancellationToken cancellationToken) =>
        _entered.Task.WaitAsync(cancellationToken);

    public void Release() => _release.TrySetResult(true);

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        bool targetCommand = command.CommandText.Contains(CommandMarker, StringComparison.Ordinal);
        if (targetCommand && Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
        {
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
