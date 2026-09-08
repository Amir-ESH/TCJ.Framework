# TCJ.Messaging.Kafka

Production Apache Kafka transport adapter for `TCJ.Messaging` on `net10.0`.

```bash
dotnet add package TCJ.Messaging.Kafka --prerelease
```

Primary registration API: `KafkaServiceCollectionExtensions.AddTcjKafka`.

The package references `TCJ.Messaging` and keeps `Confluent.Kafka` isolated from neutral TCJ packages and public/protected TCJ APIs. It provides confirmed idempotent publishing, manual consumer offsets, sequential per-partition processing, bounded cross-partition concurrency/backpressure, retry/dead-letter topics, health checks, observability, and explicit topology ownership.

See [Apache Kafka messaging transport](../messaging-kafka.md) for production configuration and guarantees.
