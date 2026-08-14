using System.Data.Common;
using TCJ.Core.Resilience;

namespace TCJ.Resilience.Tests.Infrastructure;

internal sealed class InjectedTransientException : Exception
{
    internal InjectedTransientException(string marker = "transient") : base(marker) { }
}

internal sealed class InjectedTransientClassifier : ITransientFailureClassifier
{
    public bool IsTransient(Exception exception) => exception is InjectedTransientException;
}

internal sealed class TestDbException(bool transient) : DbException("database failure")
{
    public override bool IsTransient => transient;
}
