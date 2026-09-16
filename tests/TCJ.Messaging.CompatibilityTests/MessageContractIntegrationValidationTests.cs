using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.CompatibilityTests;

[Trait("Category", "MessagingCompatibility")]
[Trait("Transport", "InMemory")]
public sealed class MessageContractIntegrationValidationTests
{
    [Fact]
    public void Messaging_and_inbox_contracts_with_same_identity_pass_integration_validation()
    {
        var services = new ServiceCollection();
        services.AddTcjMessage("integration.contract", 1, IntegrationContractJsonContext.Default.IntegrationContractMessage);
        services.AddTcjInboxMessage<IntegrationContractMessage>("integration.contract", 1);

        ValidateMessagingInboxAlignment(services);
    }

    [Fact]
    public void Messaging_and_inbox_version_mismatch_fails_integration_validation()
    {
        var services = new ServiceCollection();
        services.AddTcjMessage("integration.contract", 1, IntegrationContractJsonContext.Default.IntegrationContractMessage);
        services.AddTcjInboxMessage<IntegrationContractMessage>("integration.contract", 2);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ValidateMessagingInboxAlignment(services));

        Assert.Contains("integration.contract", exception.Message, StringComparison.Ordinal);
        Assert.Contains("v2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Messaging_and_inbox_message_type_mismatch_fails_integration_validation()
    {
        var services = new ServiceCollection();
        services.AddTcjMessage("integration.contract", 1, IntegrationContractJsonContext.Default.IntegrationContractMessage);
        services.AddTcjInboxMessage<IntegrationContractMessage>("integration.other", 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ValidateMessagingInboxAlignment(services));

        Assert.Contains("integration.other", exception.Message, StringComparison.Ordinal);
    }

    private static void ValidateMessagingInboxAlignment(IServiceCollection services)
    {
        MessagingMessageContract[] messagingContracts = services
            .Where(static descriptor => descriptor.ServiceType == typeof(MessagingMessageContract))
            .Select(static descriptor => descriptor.ImplementationInstance)
            .OfType<MessagingMessageContract>()
            .ToArray();

        foreach (ServiceDescriptor descriptor in services.Where(static descriptor => descriptor.ServiceType.Name == "InboxMessageRegistration"))
        {
            object registration = descriptor.ImplementationInstance
                ?? throw new InvalidOperationException("Inbox message registration must be instance-backed for integration validation.");
            Type registrationType = registration.GetType();
            Type clrType = ReadRequiredProperty<Type>(registrationType, registration, "MessageType");
            string messageType = ReadRequiredProperty<string>(registrationType, registration, "MessageName");
            int messageVersion = ReadRequiredProperty<int>(registrationType, registration, "Version");

            MessagingMessageContract[] candidates = messagingContracts
                .Where(contract => contract.ClrType == clrType)
                .ToArray();
            if (candidates.Length == 0)
                continue;

            if (!candidates.Any(contract =>
                    string.Equals(contract.MessageType, messageType, StringComparison.Ordinal)
                    && contract.MessageVersion == messageVersion))
            {
                throw new InvalidOperationException(
                    $"Messaging/Inbox contract mismatch for CLR type '{clrType.FullName}': Inbox uses '{messageType}' v{messageVersion}.");
            }
        }
    }

    private static T ReadRequiredProperty<T>(Type registrationType, object registration, string propertyName)
    {
        PropertyInfo property = registrationType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Inbox registration property '{propertyName}' is unavailable for integration validation.");
        object? value = property.GetValue(registration);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"Inbox registration property '{propertyName}' has an unexpected shape.");
    }
}

internal sealed record IntegrationContractMessage(string Value);

[JsonSerializable(typeof(IntegrationContractMessage))]
internal sealed partial class IntegrationContractJsonContext : JsonSerializerContext;
