using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Repositories;

namespace TCJ.EntityFrameworkCore.Diagnostics;

internal static class EntityFrameworkCoreTelemetryDiagnostics
{
    internal static readonly string PackageVersion =
        TcjPackageMetadata.GetPackageVersion(typeof(EfReadRepository<,>).Assembly);

    internal static readonly ActivitySource ActivitySource = new(
        TcjDiagnosticNames.Sources.EntityFrameworkCore,
        PackageVersion);

    internal static readonly Meter Meter = new(
        TcjDiagnosticNames.Sources.EntityFrameworkCore,
        PackageVersion);

    internal static readonly Counter<long> RepositoryOperations = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.RepositoryOperations,
        unit: "{operation}",
        description: "TCJ repository operations grouped by bounded operation and outcome dimensions.");

    internal static readonly Histogram<double> RepositoryOperationDuration = Meter.CreateHistogram<double>(
        TcjDiagnosticNames.Metrics.RepositoryOperationDuration,
        unit: "s",
        description: "TCJ logical repository operation duration in seconds.");

    internal static readonly Counter<long> UnitOfWorkCommits = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.UnitOfWorkCommits,
        unit: "{operation}",
        description: "TCJ Unit of Work commit attempts grouped by outcome.");

    internal static readonly Counter<long> UnitOfWorkRollbacks = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.UnitOfWorkRollbacks,
        unit: "{operation}",
        description: "TCJ transaction rollback attempts grouped by outcome.");

    internal static readonly Histogram<double> UnitOfWorkCommitDuration = Meter.CreateHistogram<double>(
        TcjDiagnosticNames.Metrics.UnitOfWorkCommitDuration,
        unit: "s",
        description: "TCJ Unit of Work commit duration in seconds.");

    internal static RepositoryTelemetryState StartRepositoryOperation(
        string activityName,
        string operationName,
        Type repositoryType,
        Type entityType,
        IReadDbContext db)
    {
        Activity? activity = TcjTelemetry.StartActivity(
            ActivitySource,
            activityName,
            TcjDiagnosticNames.Sources.EntityFrameworkCore,
            PackageVersion,
            operationName);

        bool counterEnabled = TcjTelemetry.MetricsEnabled && RepositoryOperations.Enabled;
        bool durationEnabled = TcjTelemetry.MetricsEnabled && RepositoryOperationDuration.Enabled;
        string provider = activity is not null || counterEnabled || durationEnabled
            ? GetProviderName(db)
            : string.Empty;

        if (activity is not null)
        {
            activity.SetTag(TcjDiagnosticNames.Tags.DatabaseProvider, provider);
            activity.SetTag(
                TcjDiagnosticNames.Tags.RepositoryType,
                TcjTelemetry.NormalizeTypeName(repositoryType));

            if (TcjTelemetry.RecordEntityTypeNames)
            {
                activity.SetTag(
                    TcjDiagnosticNames.Tags.EntityType,
                    TcjTelemetry.NormalizeTypeName(entityType));
            }
        }

        return new RepositoryTelemetryState(
            activity,
            operationName,
            provider,
            counterEnabled,
            durationEnabled);
    }

    internal static PersistenceTelemetryState StartPersistenceOperation(
        string activityName,
        string operationName,
        string provider,
        PersistenceMetricKind metricKind)
    {
        Activity? activity = TcjTelemetry.StartActivity(
            ActivitySource,
            activityName,
            TcjDiagnosticNames.Sources.EntityFrameworkCore,
            PackageVersion,
            operationName);

        activity?.SetTag(TcjDiagnosticNames.Tags.DatabaseProvider, provider);

        bool counterEnabled = TcjTelemetry.MetricsEnabled &&
            (metricKind switch
            {
                PersistenceMetricKind.Commit => UnitOfWorkCommits.Enabled,
                PersistenceMetricKind.Rollback => UnitOfWorkRollbacks.Enabled,
                _ => false
            });

        bool durationEnabled = TcjTelemetry.MetricsEnabled &&
            metricKind == PersistenceMetricKind.Commit &&
            UnitOfWorkCommitDuration.Enabled;

        return new PersistenceTelemetryState(
            activity,
            operationName,
            provider,
            metricKind,
            counterEnabled,
            durationEnabled);
    }

    internal static string GetProviderName(IReadDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (db is IWriteDbContext writeDbContext)
        {
            return NormalizeProvider(writeDbContext.Database.ProviderName);
        }

        return TcjDiagnosticNames.Providers.Unknown;
    }

    internal static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return TcjDiagnosticNames.Providers.Unknown;
        }

        return string.Equals(
            provider,
            TcjDiagnosticNames.Providers.SqlServer,
            StringComparison.Ordinal)
            ? TcjDiagnosticNames.Providers.SqlServer
            : provider;
    }
}

internal readonly struct RepositoryTelemetryState
{
    private readonly Activity? _activity;
    private readonly string _operationName;
    private readonly string _provider;
    private readonly bool _counterEnabled;
    private readonly bool _durationEnabled;
    private readonly long _startedAt;

    internal RepositoryTelemetryState(
        Activity? activity,
        string operationName,
        string provider,
        bool counterEnabled,
        bool durationEnabled)
    {
        _activity = activity;
        _operationName = operationName;
        _provider = provider;
        _counterEnabled = counterEnabled;
        _durationEnabled = durationEnabled;
        _startedAt = durationEnabled ? Stopwatch.GetTimestamp() : 0;
    }

    internal bool IsActive => _activity is not null || _counterEnabled || _durationEnabled;

    internal void CompleteSuccess() => Complete(TcjDiagnosticNames.Outcomes.Success, null, canceled: false);

    internal void CompleteCanceled(OperationCanceledException exception) =>
        Complete(TcjDiagnosticNames.Outcomes.Canceled, exception, canceled: true);

    internal void CompleteFailure(Exception exception) =>
        Complete(TcjDiagnosticNames.Outcomes.Failure, exception, canceled: false);

    private void Complete(string outcome, Exception? exception, bool canceled)
    {
        if (canceled)
        {
            TcjTelemetry.CompleteCanceled(_activity);
        }
        else if (exception is not null)
        {
            TcjTelemetry.CompleteFailure(_activity, exception);
        }
        else
        {
            TcjTelemetry.CompleteSuccess(_activity);
        }

        if (_counterEnabled || _durationEnabled)
        {
            TagList tags = new()
            {
                { TcjDiagnosticNames.Tags.OperationName, _operationName },
                { TcjDiagnosticNames.Tags.OperationOutcome, outcome },
                { TcjDiagnosticNames.Tags.DatabaseProvider, _provider }
            };

            if (_counterEnabled)
            {
                EntityFrameworkCoreTelemetryDiagnostics.RepositoryOperations.Add(1, tags);
            }

            if (_durationEnabled)
            {
                EntityFrameworkCoreTelemetryDiagnostics.RepositoryOperationDuration.Record(
                    Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
                    tags);
            }
        }

        _activity?.Dispose();
    }
}

internal enum PersistenceMetricKind
{
    None,
    Commit,
    Rollback
}

internal readonly struct PersistenceTelemetryState
{
    private readonly Activity? _activity;
    private readonly string _operationName;
    private readonly string _provider;
    private readonly PersistenceMetricKind _metricKind;
    private readonly bool _counterEnabled;
    private readonly bool _durationEnabled;
    private readonly long _startedAt;

    internal PersistenceTelemetryState(
        Activity? activity,
        string operationName,
        string provider,
        PersistenceMetricKind metricKind,
        bool counterEnabled,
        bool durationEnabled)
    {
        _activity = activity;
        _operationName = operationName;
        _provider = provider;
        _metricKind = metricKind;
        _counterEnabled = counterEnabled;
        _durationEnabled = durationEnabled;
        _startedAt = durationEnabled ? Stopwatch.GetTimestamp() : 0;
    }

    internal bool IsActive => _activity is not null || _counterEnabled || _durationEnabled;

    internal void CompleteSuccess(int? affectedRows = null, string? transactionOutcome = null) =>
        Complete(TcjDiagnosticNames.Outcomes.Success, null, canceled: false, affectedRows, transactionOutcome);

    internal void CompleteCanceled(OperationCanceledException exception) =>
        Complete(TcjDiagnosticNames.Outcomes.Canceled, exception, canceled: true, null, TcjDiagnosticNames.Outcomes.Canceled);

    internal void CompleteFailure(Exception exception) =>
        Complete(TcjDiagnosticNames.Outcomes.Failure, exception, canceled: false, null, TcjDiagnosticNames.Outcomes.Failure);

    private void Complete(
        string outcome,
        Exception? exception,
        bool canceled,
        int? affectedRows,
        string? transactionOutcome)
    {
        if (affectedRows.HasValue)
        {
            _activity?.SetTag(TcjDiagnosticNames.Tags.AffectedRows, affectedRows.Value);
        }

        if (!string.IsNullOrWhiteSpace(transactionOutcome))
        {
            _activity?.SetTag(TcjDiagnosticNames.Tags.TransactionOutcome, transactionOutcome);
        }

        if (canceled)
        {
            TcjTelemetry.CompleteCanceled(_activity);
        }
        else if (exception is not null)
        {
            TcjTelemetry.CompleteFailure(_activity, exception);
        }
        else
        {
            TcjTelemetry.CompleteSuccess(_activity);
        }

        if (_counterEnabled || _durationEnabled)
        {
            TagList tags = new()
            {
                { TcjDiagnosticNames.Tags.OperationName, _operationName },
                { TcjDiagnosticNames.Tags.OperationOutcome, outcome },
                { TcjDiagnosticNames.Tags.DatabaseProvider, _provider }
            };

            if (_counterEnabled)
            {
                if (_metricKind == PersistenceMetricKind.Commit)
                {
                    EntityFrameworkCoreTelemetryDiagnostics.UnitOfWorkCommits.Add(1, tags);
                }
                else if (_metricKind == PersistenceMetricKind.Rollback)
                {
                    EntityFrameworkCoreTelemetryDiagnostics.UnitOfWorkRollbacks.Add(1, tags);
                }
            }

            if (_durationEnabled)
            {
                EntityFrameworkCoreTelemetryDiagnostics.UnitOfWorkCommitDuration.Record(
                    Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
                    tags);
            }
        }

        _activity?.Dispose();
    }
}
