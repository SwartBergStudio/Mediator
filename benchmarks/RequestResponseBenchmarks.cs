using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System.Linq;

namespace Mediator.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob]
    public class RequestResponseBenchmarks
    {
        private ServiceProvider? _serviceProvider;
        private IMediator? _mediator;
        private SimpleRequest? _request;
        private ComplexRequest? _complexRequest;

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
            }, typeof(SimpleRequestHandler).Assembly);

            _serviceProvider = services.BuildServiceProvider();
            _mediator = _serviceProvider.GetRequiredService<IMediator>();
            
            _request = new SimpleRequest { Value = 42 };
            _complexRequest = new ComplexRequest 
            { 
                Id = Guid.NewGuid().ToString(),
                Data = new Dictionary<string, object>
                {
                    ["key1"] = "value1",
                    ["key2"] = 123,
                    ["key3"] = DateTime.UtcNow
                }
            };
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _serviceProvider?.Dispose();
        }

        [Benchmark]
        public async Task<int> SimpleRequestResponse()
        {
            return await _mediator!.Send(_request!);
        }

        [Benchmark]
        public async Task<ComplexResponse> ComplexRequestResponse()
        {
            return await _mediator!.Send(_complexRequest!);
        }

        [Benchmark]
        public async Task SimpleRequestWithoutResponse()
        {
            await _mediator!.Send(new SimpleCommand { Value = 42 });
        }

        [Benchmark]
        public async Task<int> RequestWith1000Iterations()
        {
            var tasks = new Task<int>[1000];
            for (int i = 0; i < 1000; i++)
            {
                tasks[i] = _mediator!.Send(new SimpleRequest { Value = i });
            }
            
            var results = await Task.WhenAll(tasks);
            return results.Sum();
        }
    }

    public class SimpleRequest : IRequest<int>
    {
        public int Value { get; set; }
    }

    public class ComplexRequest : IRequest<ComplexResponse>
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class ComplexResponse
    {
        public string ProcessedId { get; set; } = string.Empty;
        public int DataCount { get; set; }
        public DateTime ProcessedAt { get; set; }
    }

    public class SimpleCommand : IRequest
    {
        public int Value { get; set; }
    }

    public class SimpleRequestHandler : IRequestHandler<SimpleRequest, int>
    {
        public Task<int> Handle(SimpleRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Value * 2);
        }
    }

    public class ComplexRequestHandler : IRequestHandler<ComplexRequest, ComplexResponse>
    {
        public Task<ComplexResponse> Handle(ComplexRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ComplexResponse
            {
                ProcessedId = request.Id,
                DataCount = request.Data.Count,
                ProcessedAt = DateTime.UtcNow
            });
        }
    }

    public class SimpleCommandHandler : IRequestHandler<SimpleCommand>
    {
        public Task Handle(SimpleCommand request, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}