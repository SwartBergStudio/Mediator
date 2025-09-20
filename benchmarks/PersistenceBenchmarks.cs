using BenchmarkDotNet.Attributes;
using System.IO;

namespace Mediator.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob]
    public class PersistenceBenchmarks
    {
        private ServiceProvider? _serviceProviderWithPersistence;
        private ServiceProvider? _serviceProviderWithoutPersistence;
        private IMediator? _mediatorWithPersistence;
        private IMediator? _mediatorWithoutPersistence;
        private BenchmarkNotification? _notification;
        private string? _tempDirectory;

        [GlobalSetup]
        public void Setup()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "mediator-benchmarks", Guid.NewGuid().ToString());

            var servicesWithPersistence = new ServiceCollection();
            servicesWithPersistence.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            servicesWithPersistence.AddMediator(options =>
            {
                options.EnablePersistence = true;
                options.NotificationWorkerCount = 2;
                options.ProcessingInterval = TimeSpan.FromSeconds(1);
            }, typeof(BenchmarkNotificationHandler).Assembly);
            servicesWithPersistence.AddSingleton<INotificationPersistence>(provider => 
                new FileNotificationPersistence(_tempDirectory));

            var servicesWithoutPersistence = new ServiceCollection();
            servicesWithoutPersistence.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            servicesWithoutPersistence.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 2;
            }, typeof(BenchmarkNotificationHandler).Assembly);

            _serviceProviderWithPersistence = servicesWithPersistence.BuildServiceProvider();
            _serviceProviderWithoutPersistence = servicesWithoutPersistence.BuildServiceProvider();
            
            _mediatorWithPersistence = _serviceProviderWithPersistence.GetRequiredService<IMediator>();
            _mediatorWithoutPersistence = _serviceProviderWithoutPersistence.GetRequiredService<IMediator>();
            
            _notification = new BenchmarkNotification { Message = "Benchmark test message" };
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _serviceProviderWithPersistence?.Dispose();
            _serviceProviderWithoutPersistence?.Dispose();
            
            if (_tempDirectory != null && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Benchmark]
        public async Task PublishWithoutPersistence()
        {
            await _mediatorWithoutPersistence!.Publish(_notification!);
            await Task.Delay(5);
        }

        [Benchmark]
        public async Task PublishWithPersistence()
        {
            await _mediatorWithPersistence!.Publish(_notification!);
            await Task.Delay(5);
        }

        [Benchmark]
        public async Task Publish100WithoutPersistence()
        {
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediatorWithoutPersistence!.Publish(new BenchmarkNotification { Message = $"Message {i}" });
            }
            
            await Task.WhenAll(tasks);
            await Task.Delay(10);
        }

        [Benchmark]
        public async Task Publish100WithPersistence()
        {
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediatorWithPersistence!.Publish(new BenchmarkNotification { Message = $"Message {i}" });
            }
            
            await Task.WhenAll(tasks);
            await Task.Delay(20);
        }

        [Benchmark]
        public async Task FileOperationsOnly()
        {
            using var persistence = new FileNotificationPersistence(_tempDirectory);
            var workItem = new NotificationWorkItem
            {
                Notification = _notification,
                NotificationType = typeof(BenchmarkNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = "{\"message\":\"test\"}"
            };

            var id = await persistence.PersistAsync(workItem);
            var pending = await persistence.GetPendingAsync(1);
            await persistence.CompleteAsync(id);
        }
    }

    public class BenchmarkNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
    }

    public class BenchmarkNotificationHandler : INotificationHandler<BenchmarkNotification>
    {
        public Task Handle(BenchmarkNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}