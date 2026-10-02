using BenchmarkDotNet.Attributes;
using S = Mediator;

namespace Mediator.Benchmarks
{
    /// <summary>
    /// Side-by-side comparison with MediatR 12 using equivalent handlers and registrations.
    /// Run: dotnet run -c Release -- --filter *MediatRComparison*
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 5, iterationCount: 15)]
    public class MediatRComparisonBenchmarks
    {
        private IServiceProvider _provider = null!;
        private S.IMediator _swartBerg = null!;
        private MediatR.IMediator _mediatR = null!;

        private readonly SPing _sPing = new() { Value = 1 };
        private readonly MPing _mPing = new() { Value = 1 };
        private readonly SCommand _sCommand = new();
        private readonly MCommand _mCommand = new();
        private readonly SPingWithBehavior _sPingWithBehavior = new() { Value = 1 };
        private readonly MPingWithBehavior _mPingWithBehavior = new() { Value = 1 };
        private readonly SAsyncPing _sAsyncPing = new() { Value = 1 };
        private readonly MAsyncPing _mAsyncPing = new() { Value = 1 };

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddMediator(options => options.NotificationWorkerCount = 1);
            services.AddTransient<S.IRequestHandler<SPing, int>, SPingHandler>();
            services.AddTransient<S.IRequestHandler<SCommand>, SCommandHandler>();
            services.AddTransient<S.IRequestHandler<SPingWithBehavior, int>, SPingWithBehaviorHandler>();
            services.AddTransient<S.IPipelineBehavior<SPingWithBehavior, int>, SPassThrough<SPingWithBehavior, int>>();
            services.AddTransient<S.IRequestHandler<SAsyncPing, int>, SAsyncPingHandler>();
            services.AddTransient<S.IPipelineBehavior<SAsyncPing, int>, SPassThrough<SAsyncPing, int>>();

            services.AddMediatR(config => config.RegisterServicesFromAssemblyContaining<MediatRComparisonBenchmarks>());
            services.AddTransient<MediatR.IPipelineBehavior<MPingWithBehavior, int>, MPassThrough<MPingWithBehavior, int>>();
            services.AddTransient<MediatR.IPipelineBehavior<MAsyncPing, int>, MPassThrough<MAsyncPing, int>>();

            _provider = services.BuildServiceProvider();
            _swartBerg = _provider.GetRequiredService<S.IMediator>();
            _mediatR = _provider.GetRequiredService<MediatR.IMediator>();
        }

        [GlobalCleanup]
        public void Cleanup() => (_provider as IDisposable)?.Dispose();

        [Benchmark] public Task<int> SwartBerg_Request() => _swartBerg.Send(_sPing);
        [Benchmark] public Task<int> MediatR_Request() => _mediatR.Send(_mPing);

        [Benchmark] public Task SwartBerg_Command() => _swartBerg.Send(_sCommand);
        [Benchmark] public Task MediatR_Command() => _mediatR.Send(_mCommand);

        [Benchmark] public Task<int> SwartBerg_RequestWithBehavior() => _swartBerg.Send(_sPingWithBehavior);
        [Benchmark] public Task<int> MediatR_RequestWithBehavior() => _mediatR.Send(_mPingWithBehavior);

        /// <summary>A new DI scope per call, as in a web request or Blazor circuit.</summary>
        [Benchmark]
        public Task<int> SwartBerg_NewScopeRequest()
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<S.IMediator>().Send(_sPing);
        }

        [Benchmark]
        public Task<int> MediatR_NewScopeRequest()
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(_mPing);
        }

        /// <summary>Typical application call: new scope, handler that really awaits, one pipeline behavior.</summary>
        [Benchmark]
        public async Task<int> SwartBerg_NewScope_AsyncHandlerWithBehavior()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<S.IMediator>().Send(_sAsyncPing);
        }

        [Benchmark]
        public async Task<int> MediatR_NewScope_AsyncHandlerWithBehavior()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(_mAsyncPing);
        }

        public sealed class SPing : S.IRequest<int> { public int Value { get; set; } }
        public sealed class SPingHandler : S.IRequestHandler<SPing, int>
        {
            public Task<int> Handle(SPing request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed class SCommand : S.IRequest;
        public sealed class SCommandHandler : S.IRequestHandler<SCommand>
        {
            public Task Handle(SCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class SPingWithBehavior : S.IRequest<int> { public int Value { get; set; } }
        public sealed class SPingWithBehaviorHandler : S.IRequestHandler<SPingWithBehavior, int>
        {
            public Task<int> Handle(SPingWithBehavior request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed class SAsyncPing : S.IRequest<int> { public int Value { get; set; } }
        public sealed class SAsyncPingHandler : S.IRequestHandler<SAsyncPing, int>
        {
            public async Task<int> Handle(SAsyncPing request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return request.Value;
            }
        }

        public sealed class SPassThrough<TRequest, TResponse> : S.IPipelineBehavior<TRequest, TResponse> where TRequest : S.IRequest<TResponse>
        {
            public Task<TResponse> Handle(TRequest request, S.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
        }

        public sealed class MPing : MediatR.IRequest<int> { public int Value { get; set; } }
        public sealed class MPingHandler : MediatR.IRequestHandler<MPing, int>
        {
            public Task<int> Handle(MPing request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed class MCommand : MediatR.IRequest;
        public sealed class MCommandHandler : MediatR.IRequestHandler<MCommand>
        {
            public Task Handle(MCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class MPingWithBehavior : MediatR.IRequest<int> { public int Value { get; set; } }
        public sealed class MPingWithBehaviorHandler : MediatR.IRequestHandler<MPingWithBehavior, int>
        {
            public Task<int> Handle(MPingWithBehavior request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed class MAsyncPing : MediatR.IRequest<int> { public int Value { get; set; } }
        public sealed class MAsyncPingHandler : MediatR.IRequestHandler<MAsyncPing, int>
        {
            public async Task<int> Handle(MAsyncPing request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return request.Value;
            }
        }

        public sealed class MPassThrough<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
        {
            public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
        }
    }
}
