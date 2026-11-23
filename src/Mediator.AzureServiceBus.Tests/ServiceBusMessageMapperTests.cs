using Azure.Messaging.ServiceBus;
using Mediator.Transport;

namespace Mediator.AzureServiceBus.Tests;

public class ServiceBusMessageMapperTests
{
    [Fact]
    public void Envelope_ShouldRoundTripThroughAServiceBusMessage()
    {
        var created = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);
        var notBefore = created.AddMinutes(5);
        var envelope = new NotificationEnvelope("msg-1", typeof(Ping).AssemblyQualifiedName!, "{\"text\":\"hi\"}")
        {
            TargetHandlerType = "Some.Handler, Some.Assembly",
            AttemptCount = 2,
            CreatedAt = created,
            NotBefore = notBefore,
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            TraceState = "vendor=value",
        };

        var sent = ServiceBusMessageMapper.ToMessage(envelope);

        sent.MessageId.Should().Be("msg-1");
        sent.ContentType.Should().Be("application/json");
        sent.Subject.Should().Be(typeof(Ping).FullName, "subscriptions can filter on the notification type");
        sent.ScheduledEnqueueTime.Should().Be(notBefore);
        sent.Body.ToString().Should().Be("{\"text\":\"hi\"}");

        var received = ServiceBusMessageMapper.FromMessage(Messages.Received(sent));

        received.Should().BeEquivalentTo(envelope, o => o.Excluding(e => e.NotBefore));
    }

    [Fact]
    public void AnEnvelopeWithoutOptionalFields_ShouldNotSchedule_OrTarget()
    {
        var sent = ServiceBusMessageMapper.ToMessage(new NotificationEnvelope("m", typeof(Ping).AssemblyQualifiedName!, "{}"));

        sent.ScheduledEnqueueTime.Should().Be(default);
        sent.ApplicationProperties.Should().NotContainKey(ServiceBusMessageMapper.TargetHandlerProperty);
        sent.ApplicationProperties.Should().NotContainKey(ServiceBusMessageMapper.SubscriptionProperty);
        ServiceBusMessageMapper.FromMessage(Messages.Received(sent)).TargetHandlerType.Should().BeNull();
    }

    [Fact]
    public void AMessageNotSentByTheMediator_ShouldBeRejected()
    {
        var foreign = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{}"), messageId: "x");

        var read = () => ServiceBusMessageMapper.FromMessage(foreign);

        read.Should().Throw<FormatException>().WithMessage("*mediator-type*");
    }

    [Fact]
    public void CreatedAt_WrittenAsDateTimeByAnotherTool_ShouldBeRead()
    {
        var created = new DateTime(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: "x",
            properties: new Dictionary<string, object>
            {
                [ServiceBusMessageMapper.TypeProperty] = typeof(Ping).AssemblyQualifiedName!,
                [ServiceBusMessageMapper.CreatedAtProperty] = created,
                [ServiceBusMessageMapper.AttemptProperty] = 3L,
            });

        var envelope = ServiceBusMessageMapper.FromMessage(message);

        envelope.CreatedAt.Should().Be(new DateTimeOffset(created));
        envelope.AttemptCount.Should().Be(3);
    }
}
