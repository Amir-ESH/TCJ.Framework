namespace TCJ.Messaging.AsyncApi.Tests;

internal static class MessagingCatalogTestData
{
    public static MessagingCatalog CreateValidCatalog() => new()
    {
        SchemaVersion = MessagingCatalog.CurrentSchemaVersion,
        Application = new MessagingApplication { Name = "orders-api", Version = "1.4.0", Owner = "orders-team" },
        Document = new MessagingDocumentInfo { Title = "Orders Messaging API", Version = "1.4.0" },
        SecuritySchemes =
        [
            new MessagingSecurityScheme { Id = "managed-identity", Mechanism = MessagingSecurityMechanism.ManagedIdentity }
        ],
        Transports =
        [
            new MessagingTransport
            {
                Id = "events",
                Kind = "broker",
                Protocol = "amqp",
                DeliverySemantics = MessagingDeliverySemantics.AtLeastOnce,
                OrderingSemantics = MessagingOrderingSemantics.PerSession,
                DeadLetterSemantics = MessagingDeadLetterSemantics.Native,
                SecuritySchemeId = "managed-identity"
            }
        ],
        Channels =
        [
            new MessagingChannel
            {
                Id = "orders-created",
                Address = "orders.created",
                TransportId = "events",
                Messages = [new MessagingMessageReference { Type = "orders.created", Version = 1 }],
                DataClassifications = [MessagingDataClassification.Internal]
            }
        ],
        Producers =
        [
            new MessagingProducer
            {
                Id = "orders-publisher",
                Component = "orders-api",
                Message = new MessagingMessageReference { Type = "orders.created", Version = 1 },
                ChannelId = "orders-created",
                TransportId = "events",
                Outbox = new MessagingOutboxDeclaration
                {
                    Enabled = true,
                    DeduplicationModel = "message-identity",
                    TransactionalBoundary = MessagingTransactionalBoundary.Application,
                    DurablePublicationModel = MessagingDurablePublicationModel.ApplicationManaged
                },
                PartitionKeyStrategy = "order-id",
                OrderingKeyStrategy = "order-id"
            }
        ],
        Consumers =
        [
            new MessagingConsumer
            {
                Id = "billing-consumer",
                Component = "billing",
                MessageType = "orders.created",
                AcceptedMessageVersions = [1],
                ChannelId = "orders-created",
                TransportId = "events",
                SubscriptionOrGroup = "billing",
                Inbox = new MessagingInboxDeclaration { Enabled = true, DeduplicationModel = "message-identity" }
            }
        ],
        Relationships =
        [
            new MessagingRelationship
            {
                Id = "orders-publishes-created",
                Kind = MessagingRelationshipKind.Publishes,
                Source = new MessagingEntityReference { Kind = MessagingEntityKind.Producer, Id = "orders-publisher" },
                Target = new MessagingEntityReference { Kind = MessagingEntityKind.Channel, Id = "orders-created" }
            }
        ]
    };
}
