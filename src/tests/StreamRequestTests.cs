using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Mediator.Tests
{
    // --- Test types ---

    public class TestStreamRequest : IStreamRequest<string>
    {
        public string Prefix { get; set; } = string.Empty;
        public int ItemCount { get; set; } = 3;
        public bool ShouldFailValidation { get; set; }
    }

    public class TestStreamRequestHandler : IStreamRequestHandler<TestStreamRequest, string>
    {
        public async IAsyncEnumerable<string> Handle(TestStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < request.ItemCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken);
                yield return $"{request.Prefix}-{i}";
            }
        }
    }

    public class TestDelayedStreamRequest : IStreamRequest<int>
    {
        public int DelayMs { get; set; } = 50;
        public int ItemCount { get; set; } = 5;
    }

    public class TestDelayedStreamRequestHandler : IStreamRequestHandler<TestDelayedStreamRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(TestDelayedStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < request.ItemCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(request.DelayMs, cancellationToken);
                yield return i;
            }
        }
    }

    public class UnhandledStreamRequest : IStreamRequest<string>
    {
        public string Message { get; set; } = string.Empty;
    }

    // --- Stream Pipeline Behaviors ---

    public class TestStreamLoggingBehavior : IStreamPipelineBehavior<TestStreamRequest, string>
    {
        private readonly TestTracker _tracker;

        public TestStreamLoggingBehavior(TestTracker tracker) => _tracker = tracker;

        public async IAsyncEnumerable<string> Handle(TestStreamRequest request, StreamHandlerDelegate<string> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _tracker.AddEvent("StreamLogging: Before stream");
            _tracker.IncrementCounter("StreamLoggingBehavior");

            var itemCount = 0;
            await foreach (var item in next().WithCancellation(cancellationToken))
            {
                itemCount++;
                yield return item;
            }

            _tracker.AddEvent($"StreamLogging: After stream, yielded {itemCount} items");
        }
    }

    public class TestStreamValidationBehavior : IStreamPipelineBehavior<TestStreamRequest, string>
    {
        private readonly TestTracker _tracker;

        public TestStreamValidationBehavior(TestTracker tracker) => _tracker = tracker;

        public IAsyncEnumerable<string> Handle(TestStreamRequest request, StreamHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _tracker.AddEvent("StreamValidation: Validating request");
            _tracker.IncrementCounter("StreamValidationBehavior");

            if (request.ShouldFailValidation)
            {
                _tracker.AddEvent("StreamValidation: Request failed validation");
                throw new ArgumentException("Stream request failed validation");
            }

            _tracker.AddEvent("StreamValidation: Request passed validation");
            return next();
        }
    }

    // --- Tests ---

    public class StreamRequestTests
    {
        private static (IMediator mediator, ServiceProvider serviceProvider) CreateMediatorWithStreamHandlers()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            services.Configure<MediatorOptions>(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
            });

            services.AddTransient<IStreamRequestHandler<TestStreamRequest, string>, TestStreamRequestHandler>();
            services.AddTransient<IStreamRequestHandler<TestDelayedStreamRequest, int>, TestDelayedStreamRequestHandler>();

            services.AddMediator();

            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            return (mediator, serviceProvider);
        }

        private static (IMediator mediator, ServiceProvider serviceProvider, TestTracker tracker) CreateMediatorWithStreamBehaviors()
        {
            var services = new ServiceCollection();
            var tracker = new TestTracker();

            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton(tracker);

            services.AddTransient<IStreamRequestHandler<TestStreamRequest, string>, TestStreamRequestHandler>();
            services.AddTransient<IStreamPipelineBehavior<TestStreamRequest, string>, TestStreamLoggingBehavior>();
            services.AddTransient<IStreamPipelineBehavior<TestStreamRequest, string>, TestStreamValidationBehavior>();

            services.Configure<MediatorOptions>(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
            });

            services.AddMediator();

            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            return (mediator, serviceProvider, tracker);
        }

        [Fact]
        public async Task CreateStream_ShouldYieldAllItems()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "item", ItemCount = 5 };
                var results = new List<string>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().HaveCount(5);
                results.Should().BeEquivalentTo(["item-0", "item-1", "item-2", "item-3", "item-4"]);
            }
        }

        [Fact]
        public async Task CreateStream_WithDefaultCount_ShouldYieldThreeItems()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "test" };
                var results = new List<string>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().HaveCount(3);
                results[0].Should().Be("test-0");
                results[1].Should().Be("test-1");
                results[2].Should().Be("test-2");
            }
        }

        [Fact]
        public async Task CreateStream_WithCancellation_ShouldStopYielding()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request = new TestDelayedStreamRequest { DelayMs = 50, ItemCount = 100 };
                var cts = new CancellationTokenSource();
                var results = new List<int>();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var item in mediator.CreateStream(request, cts.Token))
                    {
                        results.Add(item);
                        if (results.Count >= 3)
                        {
                            cts.Cancel();
                        }
                    }
                });

                results.Count.Should().BeGreaterThanOrEqualTo(3);
                results.Count.Should().BeLessThan(100);
            }
        }

        [Fact]
        public async Task CreateStream_WithUnregisteredHandler_ShouldThrowException()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request = new UnhandledStreamRequest { Message = "No handler" };

                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await foreach (var _ in mediator.CreateStream(request))
                    {
                        // Should never reach here
                    }
                });
            }
        }

        [Fact]
        public async Task CreateStream_WithZeroItems_ShouldYieldNothing()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "empty", ItemCount = 0 };
                var results = new List<string>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().BeEmpty();
            }
        }

        [Fact]
        public async Task CreateStream_MultipleConsumptions_ShouldWorkIndependently()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request1 = new TestStreamRequest { Prefix = "a", ItemCount = 2 };
                var request2 = new TestStreamRequest { Prefix = "b", ItemCount = 3 };

                var results1 = new List<string>();
                var results2 = new List<string>();

                await foreach (var item in mediator.CreateStream(request1))
                {
                    results1.Add(item);
                }

                await foreach (var item in mediator.CreateStream(request2))
                {
                    results2.Add(item);
                }

                results1.Should().BeEquivalentTo(["a-0", "a-1"]);
                results2.Should().BeEquivalentTo(["b-0", "b-1", "b-2"]);
            }
        }

        [Fact]
        public async Task CreateStream_IntResponseType_ShouldWork()
        {
            var (mediator, sp) = CreateMediatorWithStreamHandlers();

            using (sp)
            {
                var request = new TestDelayedStreamRequest { DelayMs = 10, ItemCount = 4 };
                var results = new List<int>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().BeEquivalentTo([0, 1, 2, 3]);
            }
        }

        // --- Pipeline Behavior Tests ---

        [Fact]
        public async Task CreateStream_WithBehaviors_ShouldExecuteInOrder()
        {
            var (mediator, sp, tracker) = CreateMediatorWithStreamBehaviors();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "pipe", ItemCount = 3 };
                var results = new List<string>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().HaveCount(3);
                results.Should().BeEquivalentTo(["pipe-0", "pipe-1", "pipe-2"]);

                tracker.GetCounter("StreamLoggingBehavior").Should().Be(1);
                tracker.GetCounter("StreamValidationBehavior").Should().Be(1);

                var events = tracker.GetEvents();
                events.Should().Contain("StreamValidation: Validating request");
                events.Should().Contain("StreamValidation: Request passed validation");
                events.Should().Contain("StreamLogging: Before stream");
                events.Should().Contain("StreamLogging: After stream, yielded 3 items");
            }
        }

        [Fact]
        public async Task CreateStream_WithValidationFailure_ShouldThrowException()
        {
            var (mediator, sp, tracker) = CreateMediatorWithStreamBehaviors();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "fail", ItemCount = 3, ShouldFailValidation = true };

                var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
                {
                    await foreach (var _ in mediator.CreateStream(request))
                    {
                        // Should never reach here
                    }
                });

                exception.Message.Should().Be("Stream request failed validation");
                tracker.GetEvents().Should().Contain("StreamValidation: Request failed validation");
            }
        }

        [Fact]
        public async Task CreateStream_WithBehaviors_LoggingBehaviorCountsItems()
        {
            var (mediator, sp, tracker) = CreateMediatorWithStreamBehaviors();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "count", ItemCount = 5 };
                var results = new List<string>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().HaveCount(5);
                tracker.GetEvents().Should().Contain("StreamLogging: After stream, yielded 5 items");
            }
        }

        [Fact]
        public async Task CreateStream_WithBehaviors_EmptyStream_ShouldStillExecuteBehaviors()
        {
            var (mediator, sp, tracker) = CreateMediatorWithStreamBehaviors();

            using (sp)
            {
                var request = new TestStreamRequest { Prefix = "empty", ItemCount = 0 };
                var results = new List<string>();

                await foreach (var item in mediator.CreateStream(request))
                {
                    results.Add(item);
                }

                results.Should().BeEmpty();
                tracker.GetCounter("StreamLoggingBehavior").Should().Be(1);
                tracker.GetCounter("StreamValidationBehavior").Should().Be(1);
                tracker.GetEvents().Should().Contain("StreamLogging: After stream, yielded 0 items");
            }
        }
    }
}
