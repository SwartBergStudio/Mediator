using BenchmarkDotNet.Attributes;
using System.Collections.Generic;

namespace Mediator.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob]
    public class NotificationBenchmarks
    {
        private ServiceProvider? _serviceProvider;
        private IMediator? _mediator;
        private SimpleNotification? _notification;
        private ComplexNotification? _complexNotification;

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 4;
            }, typeof(SimpleNotificationHandler).Assembly);

            _serviceProvider = services.BuildServiceProvider();
            _mediator = _serviceProvider.GetRequiredService<IMediator>();
            
            _notification = new SimpleNotification { Message = "Test message" };
            _complexNotification = new ComplexNotification 
            { 
                Id = Guid.NewGuid().ToString(),
                Properties = new Dictionary<string, object>
                {
                    ["key1"] = "value1",
                    ["key2"] = 123,
                    ["key3"] = DateTime.UtcNow
                },
                Tags = ["tag1", "tag2", "tag3"]
            };
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _serviceProvider?.Dispose();
        }

        [Benchmark]
        public async Task PublishSimpleNotification()
        {
            await _mediator!.Publish(_notification!);
            await Task.Delay(10);
        }

        [Benchmark]
        public async Task PublishComplexNotification()
        {
            await _mediator!.Publish(_complexNotification!);
            await Task.Delay(10);
        }

        [Benchmark]
        public async Task Publish100Notifications()
        {
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediator!.Publish(new SimpleNotification { Message = $"Message {i}" });
            }
            
            await Task.WhenAll(tasks);
            await Task.Delay(50);
        }

        [Benchmark]
        public async Task Publish1000Notifications()
        {
            var tasks = new Task[1000];
            for (int i = 0; i < 1000; i++)
            {
                tasks[i] = _mediator!.Publish(new SimpleNotification { Message = $"Message {i}" });
            }
            
            await Task.WhenAll(tasks);
            await Task.Delay(100);
        }
    }

    public class SimpleNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
    }

    public class ComplexNotification : INotification
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class SimpleNotificationHandler : INotificationHandler<SimpleNotification>
    {
        public Task Handle(SimpleNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public class ComplexNotificationHandler : INotificationHandler<ComplexNotification>
    {
        public Task Handle(ComplexNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public class AnotherSimpleNotificationHandler : INotificationHandler<SimpleNotification>
    {
        public Task Handle(SimpleNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// PublishAndWait of a struct notification to three synchronous handlers: transient handlers (the default) against
    /// singleton handlers, which allocate nothing.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob]
    public class PublishAndWaitBenchmarks
    {
        private ServiceProvider? _transientProvider;
        private ServiceProvider? _singletonProvider;
        private IMediator? _transient;
        private IMediator? _singleton;

        [GlobalSetup]
        public void Setup()
        {
            _transientProvider = Build(ServiceLifetime.Transient);
            _singletonProvider = Build(ServiceLifetime.Singleton);
            _transient = _transientProvider.GetRequiredService<IMediator>();
            _singleton = _singletonProvider.GetRequiredService<IMediator>();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _transientProvider?.Dispose();
            _singletonProvider?.Dispose();
        }

        [Benchmark(Baseline = true)]
        public Task TransientHandlers() => _transient!.PublishAndWait(new Scored(1));

        [Benchmark]
        public Task SingletonHandlers() => _singleton!.PublishAndWait(new Scored(1));

        private static ServiceProvider Build(ServiceLifetime lifetime)
        {
            IServiceCollection services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.Add(new ServiceDescriptor(typeof(INotificationHandler<Scored>), typeof(ScoredHandlerA), lifetime));
            services.Add(new ServiceDescriptor(typeof(INotificationHandler<Scored>), typeof(ScoredHandlerB), lifetime));
            services.Add(new ServiceDescriptor(typeof(INotificationHandler<Scored>), typeof(ScoredHandlerC), lifetime));
            services.AddMediatorCore(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider();
        }

        public readonly record struct Scored(int Points) : INotification;

        public sealed class ScoredHandlerA : INotificationHandler<Scored>
        {
            public Task Handle(Scored notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class ScoredHandlerB : INotificationHandler<Scored>
        {
            public Task Handle(Scored notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class ScoredHandlerC : INotificationHandler<Scored>
        {
            public Task Handle(Scored notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
