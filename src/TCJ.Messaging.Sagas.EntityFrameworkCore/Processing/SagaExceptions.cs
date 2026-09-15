using TCJ.Core.Resilience;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal sealed class SagaRetryException : Exception
{
    internal SagaRetryException(string reason) : base($"Saga processing requested bounded retry ({reason}).") { }
}

internal sealed class SagaValidationException : Exception
{
    internal SagaValidationException(string reason, Exception? innerException = null) : base($"Saga transition is not valid ({reason}).", innerException) { }
}

internal sealed class SagaRetryFailureClassifier : ITransientFailureClassifier
{
    public bool IsTransient(Exception exception) => exception is SagaRetryException;
}
