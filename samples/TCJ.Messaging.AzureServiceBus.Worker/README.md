# TCJ.Messaging.AzureServiceBus.Worker

Minimal registration sample for the Azure Service Bus adapter using `DefaultAzureCredential`.

Set `TCJ_SERVICE_BUS_NAMESPACE` to the fully-qualified Service Bus namespace. Authentication is resolved by Azure Identity (managed identity/workload identity/service principal/developer credential according to the environment). The sample does not embed a connection string or secret.
