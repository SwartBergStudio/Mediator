using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    public class CommandPipelineBehaviorTests
    {
        [Fact]
        public async Task Behaviors_ShouldWrapCommandHandler_InRegistrationOrder_ForBothSendOverloads()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();
            var commandDispatcher = provider.GetRequiredService<ICommandDispatcher>();

            var viaInterface = new SaveSomething();
            await mediator.Send(viaInterface);                    // Send(IRequest)

            var viaGeneric = new SaveSomething();
            await commandDispatcher.Send<SaveSomething>(viaGeneric); // Send<TRequest>(TRequest)

            viaInterface.Trail.Should().Equal("outer:before", "inner:before", "handler", "inner:after", "outer:after");
            viaGeneric.Trail.Should().Equal(viaInterface.Trail);
        }

        [Fact]
        public async Task Behavior_ShouldBeAbleToShortCircuitAndThrow()
        {
            using var provider = Build();
            var command = new SaveSomething { Invalid = true };

            await provider.GetRequiredService<IMediator>().Invoking(m => m.Send(command))
                .Should().ThrowAsync<ArgumentException>().WithMessage("invalid");
            command.Trail.Should().Equal("outer:before", "inner:before");
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddTransient<IRequestHandler<SaveSomething>, SaveSomethingHandler>();
            services.AddTransient<IPipelineBehavior<SaveSomething>, OuterBehavior>();
            services.AddTransient<IPipelineBehavior<SaveSomething>, InnerBehavior>();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider();
        }

        public sealed class SaveSomething : IRequest
        {
            public bool Invalid { get; set; }
            public List<string> Trail { get; } = new();
        }

        public sealed class SaveSomethingHandler : IRequestHandler<SaveSomething>
        {
            public async Task Handle(SaveSomething request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                request.Trail.Add("handler");
            }
        }

        public sealed class OuterBehavior : IPipelineBehavior<SaveSomething>
        {
            public async Task Handle(SaveSomething request, RequestHandlerDelegate next, CancellationToken cancellationToken)
            {
                request.Trail.Add("outer:before");
                await next();
                request.Trail.Add("outer:after");
            }
        }

        public sealed class InnerBehavior : IPipelineBehavior<SaveSomething>
        {
            public async Task Handle(SaveSomething request, RequestHandlerDelegate next, CancellationToken cancellationToken)
            {
                request.Trail.Add("inner:before");
                if (request.Invalid) throw new ArgumentException("invalid");
                await next();
                request.Trail.Add("inner:after");
            }
        }
    }

    public class PublishAndWaitTests
    {
        [Fact]
        public async Task PublishAndWait_ShouldRunHandlersSequentially_InCallersScope_BeforeReturning()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<Session>().UserId = "erin";

            var notification = new OrderShipped();
            await scope.ServiceProvider.GetRequiredService<IMediator>().PublishAndWait(notification);

            // Completed before PublishAndWait returned, in registration order, with the caller's scoped session.
            notification.Trail.Should().Equal("first:erin:start", "first:erin:end", "second:erin");
        }

        [Fact]
        public async Task PublishAndWait_FirstFailure_ShouldStopRemainingHandlersAndReachCaller()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();
            var notification = new OrderShipped { FailIn = "first" };

            await scope.ServiceProvider.GetRequiredService<IMediator>().Invoking(m => m.PublishAndWait(notification))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("first failed");
            notification.Trail.Should().Equal("first::start");
        }

        [Fact]
        public async Task PublishAndWait_WithoutHandlers_ShouldComplete()
        {
            using var provider = Build();
            await provider.GetRequiredService<IMediator>().PublishAndWait(new NobodyListens());
        }

        [Fact]
        public async Task PublishAndWait_Null_ShouldReturnFaultedTask()
        {
            using var provider = Build();
            var task = provider.GetRequiredService<IMediator>().PublishAndWait<OrderShipped>(null!);
            await task.Invoking(t => t).Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task CustomMediatorWithoutPublishAndWait_ShouldStillCompile_AndThrowNotSupported()
        {
            IMediator custom = new MinimalMediator();
            await custom.Invoking(m => m.PublishAndWait(new NobodyListens())).Should().ThrowAsync<NotSupportedException>();
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddScoped<Session>();
            services.AddTransient<INotificationHandler<OrderShipped>, FirstHandler>();
            services.AddTransient<INotificationHandler<OrderShipped>, SecondHandler>();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public sealed class Session { public string? UserId { get; set; } }

        public sealed class OrderShipped : INotification
        {
            public string? FailIn { get; set; }
            public List<string> Trail { get; } = new();
        }

        public sealed class NobodyListens : INotification;

        public sealed class FirstHandler(Session session) : INotificationHandler<OrderShipped>
        {
            public async Task Handle(OrderShipped notification, CancellationToken cancellationToken)
            {
                notification.Trail.Add($"first:{session.UserId}:start");
                if (notification.FailIn == "first") throw new InvalidOperationException("first failed");
                await Task.Delay(20, cancellationToken);
                notification.Trail.Add($"first:{session.UserId}:end");
            }
        }

        public sealed class SecondHandler(Session session) : INotificationHandler<OrderShipped>
        {
            public Task Handle(OrderShipped notification, CancellationToken cancellationToken)
            {
                notification.Trail.Add($"second:{session.UserId}");
                return Task.CompletedTask;
            }
        }

        /// <summary>An IMediator implemented outside the library (e.g. a hand-written test double) before PublishAndWait existed.</summary>
        private sealed class MinimalMediator : IMediator
        {
            public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task Send(IRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
            public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        }
    }
}
