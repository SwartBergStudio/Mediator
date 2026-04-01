using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Mediator.Tests
{
    // --- Test types ---

    public class TestStreamRequest : IStreamRequest<string>
    {
        public string Prefix { get; set; } = string.Empty;
        public int ItemCount { get; set; } = 3;
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

            services.AddMediator(typeof(TestStreamRequestHandler).Assembly);

            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            return (mediator, serviceProvider);
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
    }
}
