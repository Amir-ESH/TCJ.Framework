using TCJ.Messaging.AzureServiceBus.Configuration;

namespace TCJ.Messaging.AzureServiceBus.Topology;

/// <summary>Controls whether TCJ declares, validates, or does not manage Azure Service Bus topology.</summary>
public enum AzureServiceBusTopologyMode { Declare = 0, ValidateOnly = 1, Disabled = 2 }

/// <summary>Expected queue properties required by the adapter.</summary>
public sealed class AzureServiceBusQueueOptions
{
    /// <summary>Queue name.</summary>
    public required string Name { get; set; }
    /// <summary>Whether the queue requires sessions.</summary>
    public bool RequiresSession { get; set; }
    /// <summary>Whether broker duplicate detection is expected.</summary>
    public bool RequiresDuplicateDetection { get; set; }
    /// <summary>Whether broker-side partitioning is expected. The local emulator does not support partitioned entities.</summary>
    public bool EnablePartitioning { get; set; }
    /// <summary>Expected duplicate-detection window.</summary>
    public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; set; }
    /// <summary>Expected default message TTL.</summary>
    public TimeSpan? DefaultMessageTimeToLive { get; set; }
    /// <summary>Expected maximum delivery count.</summary>
    public int? MaxDeliveryCount { get; set; }
    /// <summary>Dead-letter expired messages.</summary>
    public bool EnableDeadLetteringOnMessageExpiration { get; set; }

    internal void Validate()
    {
        AzureServiceBusValidation.ValidateEntityName(Name, nameof(Name));
        ValidateDuplicateDetection(RequiresDuplicateDetection, DuplicateDetectionHistoryTimeWindow, nameof(DuplicateDetectionHistoryTimeWindow));
        if (DefaultMessageTimeToLive is { } ttl) AzureServiceBusValidation.ValidatePositiveTimeout(ttl, nameof(DefaultMessageTimeToLive), TimeSpan.FromDays(365));
        if (MaxDeliveryCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(MaxDeliveryCount));
    }

    internal static void ValidateDuplicateDetection(bool required, TimeSpan? window, string parameterName)
    {
        if (window is null) return;
        if (!required) throw new ArgumentException("DuplicateDetectionHistoryTimeWindow requires RequiresDuplicateDetection=true.", parameterName);
        if (window < TimeSpan.FromSeconds(20) || window > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(parameterName, "Duplicate detection window must be between 20 seconds and 7 days.");
    }
}

/// <summary>Expected topic properties required by the adapter.</summary>
public sealed class AzureServiceBusTopicOptions
{
    /// <summary>Topic name.</summary>
    public required string Name { get; set; }
    /// <summary>Whether broker duplicate detection is expected.</summary>
    public bool RequiresDuplicateDetection { get; set; }
    /// <summary>Whether broker-side partitioning is expected. The local emulator does not support partitioned entities.</summary>
    public bool EnablePartitioning { get; set; }
    /// <summary>Expected duplicate-detection window.</summary>
    public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; set; }
    /// <summary>Expected default message TTL.</summary>
    public TimeSpan? DefaultMessageTimeToLive { get; set; }

    internal void Validate()
    {
        AzureServiceBusValidation.ValidateEntityName(Name, nameof(Name));
        AzureServiceBusQueueOptions.ValidateDuplicateDetection(RequiresDuplicateDetection, DuplicateDetectionHistoryTimeWindow, nameof(DuplicateDetectionHistoryTimeWindow));
        if (DefaultMessageTimeToLive is { } ttl) AzureServiceBusValidation.ValidatePositiveTimeout(ttl, nameof(DefaultMessageTimeToLive), TimeSpan.FromDays(365));
    }
}

/// <summary>Expected subscription properties required by the adapter.</summary>
public sealed class AzureServiceBusSubscriptionOptions
{
    /// <summary>Owning topic.</summary>
    public required string TopicName { get; set; }
    /// <summary>Subscription name.</summary>
    public required string SubscriptionName { get; set; }
    /// <summary>Whether the subscription requires sessions.</summary>
    public bool RequiresSession { get; set; }
    /// <summary>Expected maximum delivery count.</summary>
    public int? MaxDeliveryCount { get; set; }
    /// <summary>Dead-letter expired messages.</summary>
    public bool EnableDeadLetteringOnMessageExpiration { get; set; }
    /// <summary>Dead-letter messages whose filter evaluation fails.</summary>
    public bool EnableDeadLetteringOnFilterEvaluationExceptions { get; set; } = true;
    /// <summary>Optional explicit infrastructure-owned subscription rules.</summary>
    public IList<AzureServiceBusRuleOptions> Rules { get; } = new List<AzureServiceBusRuleOptions>();

    internal void Validate()
    {
        AzureServiceBusValidation.ValidateEntityName(TopicName, nameof(TopicName));
        AzureServiceBusValidation.ValidateEntityName(SubscriptionName, nameof(SubscriptionName), 50);
        if (MaxDeliveryCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(MaxDeliveryCount));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AzureServiceBusRuleOptions rule in Rules)
        {
            rule.Validate();
            if (!names.Add(rule.Name)) throw new ArgumentException($"Duplicate subscription rule '{rule.Name}'.", nameof(Rules));
        }
    }
}

/// <summary>Safe explicit Service Bus subscription rule declaration.</summary>
public sealed class AzureServiceBusRuleOptions
{
    /// <summary>Rule name.</summary>
    public required string Name { get; set; }
    /// <summary>Rule kind.</summary>
    public AzureServiceBusRuleFilterType FilterType { get; set; }
    /// <summary>Explicit infrastructure SQL expression when <see cref="FilterType"/> is Sql.</summary>
    public string? SqlExpression { get; set; }
    /// <summary>Correlation identifier when <see cref="FilterType"/> is Correlation.</summary>
    public string? CorrelationId { get; set; }
    /// <summary>Subject when <see cref="FilterType"/> is Correlation.</summary>
    public string? Subject { get; set; }
    /// <summary>Additional bounded string correlation properties.</summary>
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    internal void Validate()
    {
        AzureServiceBusValidation.ValidateEntityName(Name, nameof(Name), 50);
        if (!Enum.IsDefined(FilterType)) throw new ArgumentOutOfRangeException(nameof(FilterType));
        if (FilterType == AzureServiceBusRuleFilterType.Sql)
        {
            if (string.IsNullOrWhiteSpace(SqlExpression) || SqlExpression.Length > 1024 || SqlExpression.Any(char.IsControl))
                throw new ArgumentException("SqlExpression must be an explicit bounded infrastructure expression.", nameof(SqlExpression));
            if (CorrelationId is not null || Subject is not null || Properties.Count != 0)
                throw new ArgumentException("SQL rules cannot also declare correlation properties.");
        }
        else
        {
            if (SqlExpression is not null) throw new ArgumentException("Correlation rules cannot set SqlExpression.", nameof(SqlExpression));
            if (string.IsNullOrWhiteSpace(CorrelationId) && string.IsNullOrWhiteSpace(Subject) && Properties.Count == 0)
                throw new ArgumentException("Correlation rules require at least one bounded matching property.");
            if (CorrelationId is { Length: > 128 } || CorrelationId?.Any(char.IsControl) == true) throw new ArgumentException("CorrelationId is invalid.", nameof(CorrelationId));
            if (Subject is { Length: > 128 } || Subject?.Any(char.IsControl) == true) throw new ArgumentException("Subject is invalid.", nameof(Subject));
            foreach ((string key, string value) in Properties)
            {
                AzureServiceBusValidation.ValidateEntityName(key, nameof(Properties), 128);
                if (value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("Correlation rule property values must be bounded and safe.", nameof(Properties));
            }
        }
    }
}

/// <summary>Supported safe subscription rule kinds.</summary>
public enum AzureServiceBusRuleFilterType { Correlation = 0, Sql = 1 }

/// <summary>Collection of configured Azure Service Bus topology expectations.</summary>
public sealed class AzureServiceBusTopologyOptions
{
    /// <summary>Queues.</summary>
    public IList<AzureServiceBusQueueOptions> Queues { get; } = new List<AzureServiceBusQueueOptions>();
    /// <summary>Topics.</summary>
    public IList<AzureServiceBusTopicOptions> Topics { get; } = new List<AzureServiceBusTopicOptions>();
    /// <summary>Subscriptions.</summary>
    public IList<AzureServiceBusSubscriptionOptions> Subscriptions { get; } = new List<AzureServiceBusSubscriptionOptions>();

    internal void Validate()
    {
        var entityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AzureServiceBusQueueOptions queue in Queues)
        {
            queue.Validate();
            if (!entityNames.Add(queue.Name)) throw new ArgumentException($"Conflicting queue/topic declaration '{queue.Name}'.", nameof(Queues));
        }
        foreach (AzureServiceBusTopicOptions topic in Topics)
        {
            topic.Validate();
            if (!entityNames.Add(topic.Name)) throw new ArgumentException($"Conflicting queue/topic declaration '{topic.Name}'.", nameof(Topics));
        }
        var subscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AzureServiceBusSubscriptionOptions subscription in Subscriptions)
        {
            subscription.Validate();
            if (!Topics.Any(t => string.Equals(t.Name, subscription.TopicName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Subscription '{subscription.SubscriptionName}' references undeclared topic '{subscription.TopicName}'.", nameof(Subscriptions));
            if (!subscriptions.Add($"{subscription.TopicName}/{subscription.SubscriptionName}"))
                throw new ArgumentException($"Duplicate subscription declaration '{subscription.TopicName}/{subscription.SubscriptionName}'.", nameof(Subscriptions));
        }
    }

    internal bool TryGetQueue(string name, out AzureServiceBusQueueOptions? queue)
    {
        queue = Queues.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return queue is not null;
    }

    internal bool TryGetSubscription(string topic, string subscription, out AzureServiceBusSubscriptionOptions? value)
    {
        value = Subscriptions.FirstOrDefault(x => string.Equals(x.TopicName, topic, StringComparison.OrdinalIgnoreCase) && string.Equals(x.SubscriptionName, subscription, StringComparison.OrdinalIgnoreCase));
        return value is not null;
    }
}

/// <summary>Fluent topology builder kept broker-specific and intentionally smaller than the Azure management SDK.</summary>
public sealed class AzureServiceBusTopologyBuilder
{
    private readonly AzureServiceBusTopologyOptions _options;
    internal AzureServiceBusTopologyBuilder(AzureServiceBusTopologyOptions options) => _options = options;
    /// <summary>Adds one queue expectation.</summary>
    public AzureServiceBusTopologyBuilder Queue(AzureServiceBusQueueOptions options) { ArgumentNullException.ThrowIfNull(options); _options.Queues.Add(options); return this; }
    /// <summary>Adds one topic expectation.</summary>
    public AzureServiceBusTopologyBuilder Topic(AzureServiceBusTopicOptions options) { ArgumentNullException.ThrowIfNull(options); _options.Topics.Add(options); return this; }
    /// <summary>Adds one subscription expectation.</summary>
    public AzureServiceBusTopologyBuilder Subscription(AzureServiceBusSubscriptionOptions options) { ArgumentNullException.ThrowIfNull(options); _options.Subscriptions.Add(options); return this; }
}
