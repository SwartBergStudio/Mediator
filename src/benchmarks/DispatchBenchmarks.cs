using BenchmarkDotNet.Attributes;

namespace Mediator.Benchmarks
{
    /// <summary>
    /// Dispatch overhead for the scenarios shown in the README.
    /// Run: dotnet run -c Release -- --filter *DispatchBenchmarks*
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 5, iterationCount: 15)]
    public class DispatchBenchmarks
    {
        private IServiceProvider _provider = null!;
        private IMediator _mediator = null!;

        private readonly Ping _ping = new() { Value = 1 };
        private readonly DoWork _command = new();
        private readonly PingWithBehavior _pingWithBehavior = new() { Value = 1 };
        private readonly AsyncPing _asyncPing = new() { Value = 1 };

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            services.AddTransient<IRequestHandler<Ping, int>, PingHandler>();
            services.AddTransient<IRequestHandler<DoWork>, DoWorkHandler>();
            services.AddTransient<IRequestHandler<PingWithBehavior, int>, PingWithBehaviorHandler>();
            services.AddTransient<IPipelineBehavior<PingWithBehavior, int>, PassThrough<PingWithBehavior, int>>();
            services.AddTransient<IRequestHandler<AsyncPing, int>, AsyncPingHandler>();
            services.AddTransient<IPipelineBehavior<AsyncPing, int>, PassThrough<AsyncPing, int>>();

            _provider = services.BuildServiceProvider();
            _mediator = _provider.GetRequiredService<IMediator>();
        }

        [GlobalCleanup]
        public void Cleanup() => (_provider as IDisposable)?.Dispose();

        [Benchmark] public Task<int> Request() => _mediator.Send(_ping);

        [Benchmark] public Task Command() => _mediator.Send(_command);

        [Benchmark] public Task<int> RequestWithBehavior() => _mediator.Send(_pingWithBehavior);

        /// <summary>A new DI scope per call, as in a web request or Blazor circuit.</summary>
        [Benchmark]
        public Task<int> NewScopeRequest()
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IMediator>().Send(_ping);
        }

        /// <summary>Typical application call: new scope, handler that really awaits, one pipeline behavior.</summary>
        [Benchmark]
        public async Task<int> NewScope_AsyncHandlerWithBehavior()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(_asyncPing);
        }

        public sealed class Ping : IRequest<int> { public int Value { get; set; } }
        public sealed class PingHandler : IRequestHandler<Ping, int>
        {
            public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed class DoWork : IRequest;
        public sealed class DoWorkHandler : IRequestHandler<DoWork>
        {
            public Task Handle(DoWork request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class PingWithBehavior : IRequest<int> { public int Value { get; set; } }
        public sealed class PingWithBehaviorHandler : IRequestHandler<PingWithBehavior, int>
        {
            public Task<int> Handle(PingWithBehavior request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed class AsyncPing : IRequest<int> { public int Value { get; set; } }
        public sealed class AsyncPingHandler : IRequestHandler<AsyncPing, int>
        {
            public async Task<int> Handle(AsyncPing request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return request.Value;
            }
        }

        public sealed class PassThrough<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
        {
            public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
        }
    }
}
