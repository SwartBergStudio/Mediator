using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: Mediator.MediatorPipelineBehaviors(
    typeof(Mediator.Tests.CountingBehavior<,>),
    typeof(Mediator.Tests.MarkedOnlyBehavior<,>),
    typeof(Mediator.Tests.CountingStreamBehavior<,>),
    typeof(Mediator.Tests.CountingCommandBehavior<>))]

namespace Mediator.Tests
{
    /// <summary>
    /// Behaviors declared with [assembly: MediatorPipelineBehaviors] are closed over every request in this assembly,
    /// by the source generator (AddMediatorHandlers) and by assembly scanning alike. They are pass-through so the
    /// rest of the suite is unaffected, and they record invocations per request type.
    /// </summary>
    public class DeclaredBehaviorTests
    {
        [Fact]
        public void Scanning_ShouldCloseDeclaredBehaviorsOverRequests_HonouringConstraintsAndOrder()
        {
            var services = new ServiceCollection().AddMediator(typeof(DeclaredBehaviorTests).Assembly);

            Implementations<IPipelineBehavior<MarkedValueRequest, int>>(services)
                .Should().Equal(typeof(CountingBehavior<MarkedValueRequest, int>), typeof(MarkedOnlyBehavior<MarkedValueRequest, int>));

            Implementations<IPipelineBehavior<TestRequest, string>>(services)
                .Should().Equal(typeof(CountingBehavior<TestRequest, string>));

            Implementations<IStreamPipelineBehavior<TestDelayedStreamRequest, int>>(services)
                .Should().Contain(typeof(CountingStreamBehavior<TestDelayedStreamRequest, int>));
        }

        [Fact]
        public void Scanning_ShouldCloseDeclaredCommandBehaviorsOverCommands()
        {
            var services = new ServiceCollection().AddMediator(typeof(DeclaredBehaviorTests).Assembly);

            Implementations<IPipelineBehavior<TestRequestWithoutResponse>>(services)
                .Should().Equal(typeof(CountingCommandBehavior<TestRequestWithoutResponse>));
            Implementations<IPipelineBehavior<TestRequest, string>>(services)
                .Should().NotContain(typeof(CountingCommandBehavior<TestRequestWithoutResponse>));
        }

        [Fact]
        public async Task Send_Command_ShouldRunDeclaredCommandBehavior()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddMediator(o => o.NotificationWorkerCount = 1, typeof(DeclaredBehaviorTests).Assembly);
            using var provider = services.BuildServiceProvider();
            var before = BehaviorCounters.Get(typeof(MarkedCommand));

            var command = new MarkedCommand();
            await provider.GetRequiredService<IMediator>().Send(command);

            command.Trail.Should().Equal("command-behavior", "handler");
            BehaviorCounters.Get(typeof(MarkedCommand)).Should().Be(before + 1);
        }

        [Fact]
        public void Scanning_Twice_ShouldNotDuplicateDeclaredBehaviors()
        {
            var services = new ServiceCollection();
            services.AddMediator(typeof(DeclaredBehaviorTests).Assembly);
            services.AddMediator(typeof(DeclaredBehaviorTests).Assembly);

            Implementations<IPipelineBehavior<MarkedValueRequest, int>>(services).Should().HaveCount(2);
        }

        [Fact]
        public async Task Send_ValueTypeResponse_ShouldRunDeclaredBehaviorsInOrder()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddMediator(o => o.NotificationWorkerCount = 1, typeof(DeclaredBehaviorTests).Assembly);
            services.AddSingleton(new TestTracker());
            services.AddSingleton(new SimpleNotificationTracker());
            services.AddSingleton<ITestNotificationTracker>(new TestNotificationTracker());
            using var provider = services.BuildServiceProvider();

            var request = new MarkedValueRequest { Value = 20 };
            var result = await provider.GetRequiredService<IMediator>().Send(request);

            result.Should().Be(21);
            request.Trail.Should().Equal("counting", "marked-only", "handler");
            BehaviorCounters.Get(typeof(MarkedValueRequest)).Should().BeGreaterThan(0);
        }

        private static IEnumerable<Type?> Implementations<TService>(IServiceCollection services)
            => services.Where(d => d.ServiceType == typeof(TService)).Select(d => d.ImplementationType).ToList();
    }

    public interface IMarkedRequest
    {
        List<string> Trail { get; }
    }

    public sealed class MarkedValueRequest : IRequest<int>, IMarkedRequest
    {
        public int Value { get; set; }
        public List<string> Trail { get; } = new();
    }

    public sealed class MarkedValueRequestHandler : IRequestHandler<MarkedValueRequest, int>
    {
        public Task<int> Handle(MarkedValueRequest request, CancellationToken cancellationToken)
        {
            request.Trail.Add("handler");
            return Task.FromResult(request.Value + 1);
        }
    }

    public static class BehaviorCounters
    {
        private static readonly ConcurrentDictionary<Type, int> s_counts = new();
        public static void Increment(Type requestType) => s_counts.AddOrUpdate(requestType, 1, (_, c) => c + 1);
        public static int Get(Type requestType) => s_counts.TryGetValue(requestType, out var c) ? c : 0;
    }

    public sealed class CountingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            BehaviorCounters.Increment(typeof(TRequest));
            (request as IMarkedRequest)?.Trail.Add("counting");
            return next();
        }
    }

    /// <summary>Only applies to requests implementing <see cref="IMarkedRequest"/> (generic constraint).</summary>
    public sealed class MarkedOnlyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>, IMarkedRequest
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            request.Trail.Add("marked-only");
            return next();
        }
    }

    public sealed class MarkedCommand : IRequest
    {
        public List<string> Trail { get; } = new();
    }

    public sealed class MarkedCommandHandler : IRequestHandler<MarkedCommand>
    {
        public Task Handle(MarkedCommand request, CancellationToken cancellationToken)
        {
            request.Trail.Add("handler");
            return Task.CompletedTask;
        }
    }

    /// <summary>Declared behavior for requests without a response (commands).</summary>
    public sealed class CountingCommandBehavior<TRequest> : IPipelineBehavior<TRequest>
        where TRequest : IRequest
    {
        public Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
        {
            BehaviorCounters.Increment(typeof(TRequest));
            (request as MarkedCommand)?.Trail.Add("command-behavior");
            return next();
        }
    }

    public sealed class CountingStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            BehaviorCounters.Increment(typeof(TRequest));
            return next();
        }
    }
}
