using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Requests and commands are not wrapped by the mediator: failures reach the caller unchanged, whether the handler
    /// throws synchronously or after an await.
    /// </summary>
    public class RequestFailureTests
    {
        [Fact]
        public async Task FailingHandlers_ShouldPropagateToCaller()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();

            await mediator.Invoking(m => m.Send(new ThrowingAsyncRequest())).Should().ThrowAsync<InvalidOperationException>().WithMessage("async failure");
            await mediator.Invoking(m => m.Send(new ThrowingSyncCommand())).Should().ThrowAsync<InvalidOperationException>().WithMessage("sync failure");
        }

        [Fact]
        public async Task Send_NullRequest_ShouldReturnFaultedTask()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();

            var task = mediator.Send<int>(null!);

            await task.Invoking(t => t).Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task Send_MissingHandler_ShouldReturnFaultedTaskInsteadOfThrowing()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();

            var task = mediator.Send(new RequestWithoutHandler());

            await task.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddTransient<IRequestHandler<ThrowingAsyncRequest, int>, ThrowingAsyncRequestHandler>();
            services.AddTransient<IRequestHandler<ThrowingSyncCommand>, ThrowingSyncCommandHandler>();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider();
        }

        public sealed class ThrowingAsyncRequest : IRequest<int>;

        public sealed class ThrowingAsyncRequestHandler : IRequestHandler<ThrowingAsyncRequest, int>
        {
            public async Task<int> Handle(ThrowingAsyncRequest request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                throw new InvalidOperationException("async failure");
            }
        }

        public sealed class ThrowingSyncCommand : IRequest;

        public sealed class ThrowingSyncCommandHandler : IRequestHandler<ThrowingSyncCommand>
        {
            public Task Handle(ThrowingSyncCommand request, CancellationToken cancellationToken)
                => throw new InvalidOperationException("sync failure");
        }

        // Intentionally has no handler: the test checks the failure.
        #pragma warning disable MEDGEN005
        public sealed class RequestWithoutHandler : IRequest<int>;
        #pragma warning restore MEDGEN005
    }
}
