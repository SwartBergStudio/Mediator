using Azure.Messaging.ServiceBus;

namespace Mediator.AzureServiceBus;

/// <summary>
/// Settles the message being handled. A seam over <see cref="ProcessMessageEventArgs"/>, so the settling decisions can be
/// tested without a broker.
/// </summary>
internal interface IMessageSettler
{
    Task CompleteAsync(CancellationToken cancellationToken);

    Task AbandonAsync(CancellationToken cancellationToken);

    Task DeadLetterAsync(string reason, string description, IDictionary<string, object>? propertiesToModify, CancellationToken cancellationToken);
}

internal sealed class ProcessMessageEventArgsSettler(ProcessMessageEventArgs args) : IMessageSettler
{
    public Task CompleteAsync(CancellationToken cancellationToken)
        => args.CompleteMessageAsync(args.Message, cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken)
        => args.AbandonMessageAsync(args.Message, propertiesToModify: null, cancellationToken);

    public Task DeadLetterAsync(string reason, string description, IDictionary<string, object>? propertiesToModify, CancellationToken cancellationToken)
        => args.DeadLetterMessageAsync(args.Message, propertiesToModify, reason, description, cancellationToken);
}
