using BenchmarkDotNet.Attributes;

namespace Mediator.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob]
    public class PipelineBenchmarks
    {
        private ServiceProvider? _serviceProvider;
        private IMediator? _mediator;
        private PipelineRequest? _request;

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
            }, typeof(PipelineRequestHandler).Assembly);

            services.AddTransient<IPipelineBehavior<PipelineRequest, string>, LoggingPipelineBehavior>();
            services.AddTransient<IPipelineBehavior<PipelineRequest, string>, TimingPipelineBehavior>();
            services.AddTransient<IPipelineBehavior<PipelineRequest, string>, ValidationPipelineBehavior>();

            _serviceProvider = services.BuildServiceProvider();
            _mediator = _serviceProvider.GetRequiredService<IMediator>();
            _request = new PipelineRequest { Value = "test" };
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _serviceProvider?.Dispose();
        }

        [Benchmark]
        public async Task<string> RequestWithoutPipeline()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
            }, typeof(PipelineRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            
            return await mediator.Send(new PipelineRequest { Value = "test" });
        }

        [Benchmark]
        public async Task<string> RequestWith3Pipelines()
        {
            return await _mediator!.Send(_request!);
        }

        [Benchmark]
        public async Task<string> Request100TimesWithPipelines()
        {
            var tasks = new Task<string>[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediator!.Send(new PipelineRequest { Value = $"test{i}" });
            }
            
            var results = await Task.WhenAll(tasks);
            return results[0];
        }
    }

    public class PipelineRequest : IRequest<string>
    {
        public string Value { get; set; } = string.Empty;
    }

    public class PipelineRequestHandler : IRequestHandler<PipelineRequest, string>
    {
        public Task<string> Handle(PipelineRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult($"Processed: {request.Value}");
        }
    }

    public class LoggingPipelineBehavior : IPipelineBehavior<PipelineRequest, string>
    {
        public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            var result = await next();
            return result;
        }
    }

    public class TimingPipelineBehavior : IPipelineBehavior<PipelineRequest, string>
    {
        public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await next();
            stopwatch.Stop();
            return result;
        }
    }

    public class ValidationPipelineBehavior : IPipelineBehavior<PipelineRequest, string>
    {
        public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Value))
                throw new ArgumentException("Value is required");
                
            return await next();
        }
    }
}