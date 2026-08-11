using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TCJ.EntityFrameworkCore.Outbox.Interceptors;

internal sealed class OutboxTransactionInterceptor : DbTransactionInterceptor
{
    private readonly OutboxCaptureTracker _captureTracker;

    public OutboxTransactionInterceptor(OutboxCaptureTracker captureTracker)
    {
        _captureTracker = captureTracker ?? throw new ArgumentNullException(nameof(captureTracker));
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result)
    {
        EnsureSafeToCommit(eventData.Context);
        return base.TransactionCommitting(transaction, eventData, result);
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeToCommit(eventData.Context);
        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        CompleteTransaction(eventData.Context);
        base.TransactionCommitted(transaction, eventData);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        CompleteTransaction(eventData.Context);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        RestoreAfterRollback(eventData.Context);
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RestoreAfterRollback(eventData.Context);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        RestoreAfterRollback(eventData.Context);
        base.TransactionFailed(transaction, eventData);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RestoreAfterRollback(eventData.Context);
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }

    private void EnsureSafeToCommit(DbContext? context)
    {
        if (context is not null &&
            _captureTracker.TryGet(context, out OutboxCaptureState state) &&
            state.HadSaveFailure)
        {
            throw new InvalidOperationException(
                "A SaveChanges operation failed inside the current outbox transaction. Roll the transaction back before retrying so business state and outbox records cannot commit partially.");
        }
    }

    private void CompleteTransaction(DbContext? context)
    {
        if (context is null ||
            !_captureTracker.TryGet(context, out OutboxCaptureState state) ||
            !state.AwaitingExplicitCommit)
        {
            return;
        }

        // EF Core commits the transaction it creates for an implicit SaveChanges before
        // SavedChanges/SavedChangesAsync runs. Finalizing that transaction here would
        // remove the capture state before the save interceptor can mark the messages as
        // persisted. Only caller-managed explicit transactions are finalized here; the
        // normal implicit SaveChanges path is completed by OutboxSaveChangesInterceptor.
        OutboxSaveChangesInterceptor.RecordCommittedPersistence(state);
        OutboxSaveChangesInterceptor.ClearCompletedDomainEvents(state);
        _captureTracker.Remove(context);
    }

    private void RestoreAfterRollback(DbContext? context)
    {
        if (context is null || !_captureTracker.TryGet(context, out OutboxCaptureState state))
        {
            return;
        }

        foreach ((object entity, EntityState originalState) in state.OriginalStates)
        {
            // AcceptAllChanges detaches successfully deleted entities. Re-applying the
            // pre-save state re-attaches them so a rollback can be retried faithfully.
            context.Entry(entity).State = originalState;
        }

        foreach (CapturedOutboxEvent captured in state.Captured.Values)
        {
            var entry = context.Entry(captured.Message);
            if (entry.State != EntityState.Detached)
            {
                entry.State = EntityState.Added;
            }

            captured.Persisted = false;
        }

        state.OriginalStates.Clear();
        state.AwaitingExplicitCommit = false;
        state.HadSaveFailure = false;
    }
}
