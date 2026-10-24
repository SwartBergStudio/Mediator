using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Dispatch without pipeline behaviors allocates nothing of its own: with a singleton handler that returns a cached
    /// task, Send and CreateStream allocate zero bytes. The pipeline closure used to be allocated on every call, behaviors
    /// or not (40 bytes per Send). Same collection as TelemetryTests, so no listener switches on the observed path.
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public class DispatchAllocationTests
    {
        private const int Calls = 1_000;

        [Fact]
        public void Request_WithoutBehaviors_ShouldNotAllocate()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();
            var request = new ZeroAllocRequest();

            BytesPerCall(() => mediator.Send(request)).Should().Be(0);
        }

        [Fact]
        public void Command_WithoutBehaviors_ShouldNotAllocate()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();
            var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
            var command = new ZeroAllocCommand();

            BytesPerCall(() => mediator.Send(command)).Should().Be(0);
            BytesPerCall(() => dispatcher.Send(command)).Should().Be(0, "the generic overload takes the statically typed path");
        }

        [Fact]
        public void Stream_WithoutBehaviors_ShouldNotAllocate()
        {
            using var provider = Build();
            var mediator = provider.GetRequiredService<IMediator>();
            var request = new ZeroAllocStream();

            BytesPerCall(() => mediator.CreateStream(request)).Should().Be(0);
        }

        /// <summary>Warms up, then measures the average bytes allocated per call on this thread.</summary>
        private static long BytesPerCall(Func<object> call)
        {
            for (var i = 0; i < Calls; i++) call();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Calls; i++) call();
            return (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<IRequestHandler<ZeroAllocRequest, int>, ZeroAllocRequestHandler>();
            services.AddSingleton<IRequestHandler<ZeroAllocCommand>, ZeroAllocCommandHandler>();
            services.AddSingleton<IStreamRequestHandler<ZeroAllocStream, int>, ZeroAllocStreamHandler>();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider();
        }

        public sealed class ZeroAllocRequest : IRequest<int>;

        public sealed class ZeroAllocRequestHandler : IRequestHandler<ZeroAllocRequest, int>
        {
            public Task<int> Handle(ZeroAllocRequest request, CancellationToken cancellationToken) => Task.FromResult(1); // cached by the runtime
        }

        public sealed class ZeroAllocCommand : IRequest;

        public sealed class ZeroAllocCommandHandler : IRequestHandler<ZeroAllocCommand>
        {
            public Task Handle(ZeroAllocCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class ZeroAllocStream : IStreamRequest<int>;

        public sealed class ZeroAllocStreamHandler : IStreamRequestHandler<ZeroAllocStream, int>
        {
            private static readonly IAsyncEnumerable<int> s_items = Items();

            public IAsyncEnumerable<int> Handle(ZeroAllocStream request, CancellationToken cancellationToken) => s_items;

            private static async IAsyncEnumerable<int> Items()
            {
                await Task.CompletedTask;
                yield return 1;
            }
        }
    }
}
