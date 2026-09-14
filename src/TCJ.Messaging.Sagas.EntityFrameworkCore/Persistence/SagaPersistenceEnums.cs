namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

internal enum SagaTimerStatus
{
    Scheduled = 0,
    Completed = 1,
    Canceled = 2,
    Failed = 3
}

internal enum SagaCompensationStatus
{
    None = 0,
    Pending = 1,
    Completed = 2,
    Exhausted = 3
}
