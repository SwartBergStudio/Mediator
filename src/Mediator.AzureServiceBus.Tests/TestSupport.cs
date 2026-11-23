global using FluentAssertions;
global using Xunit;
global using Mediator.Core;
global using Mediator.Serialization;
global using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Mediator.Transport;

namespace Mediator.AzureServiceBus.Tests;

public class Ping : INotification
{
    public string Text { get; set; } = "";
    public bool FailA { get; set; }
    public bool FailB { get; set; }
}

public sealed class Recorder
{
    private readonly ConcurrentQueue<string> _handled = new();
    public IReadOnlyCollection<string> Handled => _handled;
    public void Record(string handler) => _handled.Enqueue(handler);
}

public sealed class PingHandlerA(Recorder recorder) : INotificationHandler<Ping>
{
    public Task Handle(Ping notification, CancellationToken cancellationToken)
    {
        recorder.Record(nameof(PingHandlerA));
        return notification.FailA ? Task.FromException(new InvalidOperationException("A failed")) : Task.CompletedTask;
    }
}

public sealed class PingHandlerB(Recorder recorder) : INotificationHandler<Ping>
{
    public Task Handle(Ping notification, CancellationToken cancellationToken)
    {
        recorder.Record(nameof(PingHandlerB));
        return notification.FailB ? Task.FromException(new InvalidOperationException("B failed")) : Task.CompletedTask;
    }
}

/// <summary>A sender that records messages instead of sending them (the SDK's mocking constructor).</summary>
internal sealed class RecordingSender : ServiceBusSender
{
    public ConcurrentQueue<ServiceBusMessage> Sent { get; } = new();
    public Exception? Failure { get; set; }

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        if (Failure != null) return Task.FromException(Failure);
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records how a message was settled.</summary>
internal sealed class RecordingSettler : IMessageSettler
{
    public List<string> Calls { get; } = new();
    public string? DeadLetterReason { get; private set; }
    public string? DeadLetterDescription { get; private set; }
    public IDictionary<string, object>? DeadLetterProperties { get; private set; }

    public Task CompleteAsync(CancellationToken cancellationToken) { Calls.Add("Complete"); return Task.CompletedTask; }

    public Task AbandonAsync(CancellationToken cancellationToken) { Calls.Add("Abandon"); return Task.CompletedTask; }

    public Task DeadLetterAsync(string reason, string description, IDictionary<string, object>? propertiesToModify, CancellationToken cancellationToken)
    {
        Calls.Add("DeadLetter");
        DeadLetterReason = reason;
        DeadLetterDescription = description;
        DeadLetterProperties = propertiesToModify;
        return Task.CompletedTask;
    }
}

internal static class Messages
{
    /// <summary>What a receiver gets for a message that was sent.</summary>
    public static ServiceBusReceivedMessage Received(ServiceBusMessage sent) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sent.Body,
            messageId: sent.MessageId,
            contentType: sent.ContentType,
            subject: sent.Subject,
            properties: new Dictionary<string, object>(sent.ApplicationProperties),
            enqueuedTime: DateTimeOffset.UtcNow);

    public static ServiceBusReceivedMessage For(NotificationEnvelope envelope, string? retryForSubscription = null)
        => Received(ServiceBusMessageMapper.ToMessage(envelope, retryForSubscription));
}
