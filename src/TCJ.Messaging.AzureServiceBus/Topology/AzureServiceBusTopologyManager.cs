using System.Diagnostics;
using Azure;
using Azure.Messaging.ServiceBus.Administration;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.Diagnostics;

namespace TCJ.Messaging.AzureServiceBus.Topology;

internal sealed class AzureServiceBusTopologyManager
{
    private readonly TcjAzureServiceBusOptions _options;
    private readonly AzureServiceBusClientManager _clients;

    internal AzureServiceBusTopologyManager(TcjAzureServiceBusOptions options, AzureServiceBusClientManager clients)
    { _options = options; _clients = clients; }

    internal async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_options.TopologyMode == AzureServiceBusTopologyMode.Disabled) return;
        using Activity? activity = AzureServiceBusDiagnostics.Start(TcjAzureServiceBusDiagnosticNames.TopologyValidateActivity, "topology.validate");
        ServiceBusAdministrationClient admin = _clients.CreateAdministrationClient();
        try
        {
            foreach (AzureServiceBusQueueOptions queue in _options.Topology.Queues)
                await EnsureQueueAsync(admin, queue, cancellationToken).ConfigureAwait(false);
            foreach (AzureServiceBusTopicOptions topic in _options.Topology.Topics)
                await EnsureTopicAsync(admin, topic, cancellationToken).ConfigureAwait(false);
            foreach (AzureServiceBusSubscriptionOptions subscription in _options.Topology.Subscriptions)
                await EnsureSubscriptionAsync(admin, subscription, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error, "PermanentTopology");
            throw;
        }
    }

    private async Task EnsureQueueAsync(ServiceBusAdministrationClient admin, AzureServiceBusQueueOptions expected, CancellationToken token)
    {
        Response<bool> exists = await admin.QueueExistsAsync(expected.Name, token).ConfigureAwait(false);
        if (!exists.Value)
        {
            if (_options.TopologyMode == AzureServiceBusTopologyMode.ValidateOnly) throw PermanentTopology($"Queue '{expected.Name}' is missing.");
            var create = new CreateQueueOptions(expected.Name)
            {
                RequiresSession = expected.RequiresSession,
                RequiresDuplicateDetection = expected.RequiresDuplicateDetection,
                EnablePartitioning = expected.EnablePartitioning,
                DeadLetteringOnMessageExpiration = expected.EnableDeadLetteringOnMessageExpiration
            };
            if (expected.DuplicateDetectionHistoryTimeWindow is { } duplicateWindow) create.DuplicateDetectionHistoryTimeWindow = duplicateWindow;
            if (expected.DefaultMessageTimeToLive is { } ttl) create.DefaultMessageTimeToLive = ttl;
            if (expected.MaxDeliveryCount is { } max) create.MaxDeliveryCount = max;
            _ = await admin.CreateQueueAsync(create, token).ConfigureAwait(false);
            return;
        }
        QueueProperties actual = (await admin.GetQueueAsync(expected.Name, token).ConfigureAwait(false)).Value;
        if (actual.RequiresSession != expected.RequiresSession || actual.RequiresDuplicateDetection != expected.RequiresDuplicateDetection ||
            actual.EnablePartitioning != expected.EnablePartitioning ||
            (expected.MaxDeliveryCount is { } maxCount && actual.MaxDeliveryCount != maxCount) ||
            actual.DeadLetteringOnMessageExpiration != expected.EnableDeadLetteringOnMessageExpiration ||
            (expected.DuplicateDetectionHistoryTimeWindow is { } window && actual.DuplicateDetectionHistoryTimeWindow != window) ||
            (expected.DefaultMessageTimeToLive is { } ttl && actual.DefaultMessageTimeToLive != ttl))
            throw PermanentTopology($"Queue '{expected.Name}' properties conflict with TCJ expectations.");
    }

    private async Task EnsureTopicAsync(ServiceBusAdministrationClient admin, AzureServiceBusTopicOptions expected, CancellationToken token)
    {
        Response<bool> exists = await admin.TopicExistsAsync(expected.Name, token).ConfigureAwait(false);
        if (!exists.Value)
        {
            if (_options.TopologyMode == AzureServiceBusTopologyMode.ValidateOnly) throw PermanentTopology($"Topic '{expected.Name}' is missing.");
            var create = new CreateTopicOptions(expected.Name)
            {
                RequiresDuplicateDetection = expected.RequiresDuplicateDetection,
                EnablePartitioning = expected.EnablePartitioning
            };
            if (expected.DuplicateDetectionHistoryTimeWindow is { } duplicateWindow) create.DuplicateDetectionHistoryTimeWindow = duplicateWindow;
            if (expected.DefaultMessageTimeToLive is { } ttl) create.DefaultMessageTimeToLive = ttl;
            _ = await admin.CreateTopicAsync(create, token).ConfigureAwait(false);
            return;
        }
        TopicProperties actual = (await admin.GetTopicAsync(expected.Name, token).ConfigureAwait(false)).Value;
        if (actual.RequiresDuplicateDetection != expected.RequiresDuplicateDetection ||
            actual.EnablePartitioning != expected.EnablePartitioning ||
            (expected.DuplicateDetectionHistoryTimeWindow is { } window && actual.DuplicateDetectionHistoryTimeWindow != window) ||
            (expected.DefaultMessageTimeToLive is { } ttl && actual.DefaultMessageTimeToLive != ttl))
            throw PermanentTopology($"Topic '{expected.Name}' properties conflict with TCJ expectations.");
    }

    private async Task EnsureSubscriptionAsync(ServiceBusAdministrationClient admin, AzureServiceBusSubscriptionOptions expected, CancellationToken token)
    {
        Response<bool> exists = await admin.SubscriptionExistsAsync(expected.TopicName, expected.SubscriptionName, token).ConfigureAwait(false);
        bool created = false;
        if (!exists.Value)
        {
            if (_options.TopologyMode == AzureServiceBusTopologyMode.ValidateOnly)
                throw PermanentTopology($"Subscription '{expected.TopicName}/{expected.SubscriptionName}' is missing.");
            var create = new CreateSubscriptionOptions(expected.TopicName, expected.SubscriptionName)
            {
                RequiresSession = expected.RequiresSession,
                DeadLetteringOnMessageExpiration = expected.EnableDeadLetteringOnMessageExpiration,
                EnableDeadLetteringOnFilterEvaluationExceptions = expected.EnableDeadLetteringOnFilterEvaluationExceptions
            };
            if (expected.MaxDeliveryCount is { } max) create.MaxDeliveryCount = max;
            _ = await admin.CreateSubscriptionAsync(create, token).ConfigureAwait(false);
            created = true;
        }
        else
        {
            SubscriptionProperties actual = (await admin.GetSubscriptionAsync(expected.TopicName, expected.SubscriptionName, token).ConfigureAwait(false)).Value;
            if (actual.RequiresSession != expected.RequiresSession ||
                (expected.MaxDeliveryCount is { } maxCount && actual.MaxDeliveryCount != maxCount) ||
                actual.DeadLetteringOnMessageExpiration != expected.EnableDeadLetteringOnMessageExpiration ||
                actual.EnableDeadLetteringOnFilterEvaluationExceptions != expected.EnableDeadLetteringOnFilterEvaluationExceptions)
                throw PermanentTopology($"Subscription '{expected.TopicName}/{expected.SubscriptionName}' conflicts with TCJ expectations.");
        }
        if (expected.Rules.Count == 0) return;
        if (created)
        {
            try { await admin.DeleteRuleAsync(expected.TopicName, expected.SubscriptionName, CreateRuleOptions.DefaultRuleName, token).ConfigureAwait(false); }
            catch (RequestFailedException exception) when (exception.Status == 404) { }
        }
        foreach (AzureServiceBusRuleOptions rule in expected.Rules)
        {
            Response<bool> ruleExists = await admin.RuleExistsAsync(expected.TopicName, expected.SubscriptionName, rule.Name, token).ConfigureAwait(false);
            RuleFilter filter = CreateFilter(rule);
            if (ruleExists.Value)
            {
                RuleProperties actualRule = (await admin.GetRuleAsync(expected.TopicName, expected.SubscriptionName, rule.Name, token).ConfigureAwait(false)).Value;
                if (!actualRule.Filter.Equals(filter))
                    throw PermanentTopology($"Subscription rule '{expected.TopicName}/{expected.SubscriptionName}/{rule.Name}' conflicts with TCJ expectations.");
                continue;
            }
            if (_options.TopologyMode == AzureServiceBusTopologyMode.ValidateOnly)
                throw PermanentTopology($"Subscription rule '{expected.TopicName}/{expected.SubscriptionName}/{rule.Name}' is missing.");
            _ = await admin.CreateRuleAsync(expected.TopicName, expected.SubscriptionName, new CreateRuleOptions(rule.Name, filter), token).ConfigureAwait(false);
        }
    }

    private static RuleFilter CreateFilter(AzureServiceBusRuleOptions rule)
    {
        if (rule.FilterType == AzureServiceBusRuleFilterType.Sql) return new SqlRuleFilter(rule.SqlExpression!);
        var filter = new CorrelationRuleFilter { CorrelationId = rule.CorrelationId, Subject = rule.Subject };
        foreach ((string key, string value) in rule.Properties) filter.ApplicationProperties[key] = value;
        return filter;
    }

    private static InvalidOperationException PermanentTopology(string message) => new($"{message} (PermanentTopology)");
}
