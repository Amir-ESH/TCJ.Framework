using TCJ.Messaging.Sagas;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Extensions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Extensions;

var options = new TcjSagaOptions();
options.Validate();
SagaCorrelationKey correlation = SagaCorrelationKey.From("Order-42");

Type[] packageSurface =
[
    typeof(SagaStatus),
    typeof(SagaModelBuilderExtensions),
    typeof(SagaServiceCollectionExtensions),
    typeof(SqlServerSagaModelBuilderExtensions),
    typeof(SqlServerSagaServiceCollectionExtensions),
];

if (packageSurface.Any(static type => type.AssemblyQualifiedName is null) ||
    correlation.ToString() != "[redacted-correlation]" ||
    options.MaximumStatePayloadBytes != 256 * 1024 ||
    options.MaxTimerAttempts != 5 ||
    options.MaxCompensationAttempts != 5)
{
    throw new InvalidOperationException("Saga package surface or bounded defaults are invalid.");
}

Console.WriteLine("TCJ durable Saga SQL Server consumer passed");
