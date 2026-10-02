namespace Mediator
{
    /// <summary>
    /// Mediator interface for sending requests and publishing notifications.
    /// </summary>
    public interface IMediator
    {
        /// <summary>
        /// Sends a request and returns a response asynchronously.
        /// </summary>
        /// <typeparam name="TResponse">The response type.</typeparam>
        /// <param name="request">The request to send.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The response from the request handler.</returns>
        Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Sends a request without expecting a response.
        /// </summary>
        /// <param name="request">The request to send.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task Send(IRequest request, CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Publishes a notification for background processing: the returned task completes once the notification is
        /// queued (and persisted, when persistence is enabled). Handlers run later, in their own DI scope.
        /// </summary>
        /// <typeparam name="TNotification">The notification type.</typeparam>
        /// <param name="notification">The notification to publish.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes when the notification is queued.</returns>
        Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification;

        /// <summary>
        /// Invokes all handlers of the notification now, one after another in registration order, in the caller's DI
        /// scope, and completes when they have all finished. The first handler exception stops the remaining handlers
        /// and is thrown to the caller. Persistence is not used.
        /// </summary>
        /// <remarks>
        /// Use this when the work must succeed or fail with the caller, for example domain events that update data in
        /// the same <c>DbContext</c> or transaction, or handlers that need scoped services such as the current user's
        /// session. Use <see cref="Publish{TNotification}"/> for side effects that should not delay the caller.
        /// </remarks>
        /// <typeparam name="TNotification">The notification type.</typeparam>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">Cancellation token passed to the handlers.</param>
        /// <returns>A task that completes when every handler has completed.</returns>
        Task PublishAndWait<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(PublishAndWait)}.");

        /// <summary>
        /// Sends a streaming request and returns an async enumerable of response items.
        /// </summary>
        /// <typeparam name="TResponse">The type of each streamed response item.</typeparam>
        /// <param name="request">The streaming request to send.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of response items from the stream handler.</returns>
        IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);
    }
}
