# Samples

| Sample | What it shows |
|---|---|
| [`Mediator.AotSample`](Mediator.AotSample) | Native AOT end to end: the source generator, declared behaviors, persistence with a source-generated JSON context, and tracing. CI publishes it with Native AOT and runs it. |
| [`Mediator.Persistence.Redis`](Mediator.Persistence.Redis) | `INotificationPersistence` for Redis, safe for several app instances. |
| [`Mediator.Persistence.EfCore`](Mediator.Persistence.EfCore) | `INotificationPersistence` for any EF Core relational provider, safe for several app instances. |
| [`Mediator.Persistence.Samples.Tests`](Mediator.Persistence.Samples.Tests) | One set of contract tests run against both persistence samples. Set `REDIS_CONNECTION` to a Redis server to include Redis; without one, those tests are skipped. |

The persistence samples are meant to be copied into your app and adapted, for example table or key names and the lease duration. They aren't published as packages.

To publish notifications through Azure Service Bus instead of the in-process queue, use the `SwartBerg.Mediator.AzureServiceBus` package; see [Notifications over a message broker](../README.md#notifications-over-a-message-broker-azure-service-bus) in the main README.
