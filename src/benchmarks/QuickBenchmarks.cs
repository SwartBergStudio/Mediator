using BenchmarkDotNet.Attributes;

namespace Mediator.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob]
    public class QuickBenchmarks
    {
        private ServiceProvider? _provider;
        private IMediator? _mediator;
        private QuickRequest? _request;
        private QuickNotification? _notification;

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 2;
            }, typeof(QuickBenchmarks).Assembly);

            _provider = services.BuildServiceProvider();
            _mediator = _provider.GetRequiredService<IMediator>();
            
            _request = new QuickRequest { Value = 42 };
            _notification = new QuickNotification { Message = "Test" };
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _provider?.Dispose();
        }

        [Benchmark]
        public async Task<int> SingleRequest()
        {
            return await _mediator!.Send(_request!);
        }

        [Benchmark]
        public async Task SingleNotification()
        {
            await _mediator!.Publish(_notification!);
        }

        [Benchmark]
        public async Task<int> BatchRequests100()
        {
            var tasks = new Task<int>[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediator!.Send(new QuickRequest { Value = i });
            }
            var results = await Task.WhenAll(tasks);
            return results[0];
        }

        [Benchmark]
        public async Task BatchNotifications100()
        {
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediator!.Publish(new QuickNotification { Message = $"Test {i}" });
            }
            await Task.WhenAll(tasks);
        }
    }

    public class QuickRequest : IRequest<int>
    {
        public int Value { get; set; }
    }

    public class QuickNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
    }

    public class QuickRequestHandler : IRequestHandler<QuickRequest, int>
    {
        public Task<int> Handle(QuickRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Value * 2);
        }
    }

    public class QuickNotificationHandler : INotificationHandler<QuickNotification>
    {
        public Task Handle(QuickNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}