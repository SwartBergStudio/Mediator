using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Verifies the source-generated AddMediatorHandlers() (emitted for this test assembly) behaves exactly like
    /// reflection-based assembly scanning.
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public class SourceGeneratedRegistrationTests
    {
        [Fact]
        public void AddMediatorHandlers_ShouldRegisterSameHandlersAsAssemblyScanning()
        {
            var scanned = new ServiceCollection().AddMediator(typeof(SourceGeneratedRegistrationTests).Assembly);
            var generated = new ServiceCollection().AddMediatorHandlers();

            HandlerPairs(generated).Should().BeEquivalentTo(HandlerPairs(scanned));
            generated.Where(d => IsMediatorInterface(d.ServiceType)).Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Transient);
        }

        [Fact]
        public void AddMediatorHandlers_CombinedWithScanning_ShouldNotDuplicateHandlers()
        {
            var services = new ServiceCollection();
            services.AddMediatorHandlers();
            var handlersAfterGenerated = HandlerPairs(services).Count();

            services.AddMediator(typeof(SourceGeneratedRegistrationTests).Assembly);
            services.AddMediatorHandlers();

            HandlerPairs(services).Should().HaveCount(handlersAfterGenerated);
        }

        [Fact]
        public async Task AddMediatorHandlers_ShouldDispatchRequestsCommandsStreamsAndNotifications()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            var tracker = new TestNotificationTracker();
            services.AddSingleton<ITestNotificationTracker>(tracker);
            services.AddSingleton(new SimpleNotificationTracker());
            services.AddSingleton(new TestTracker());
            services.AddMediatorCore(options => options.NotificationWorkerCount = 1);
            services.AddMediatorHandlers();

            using var provider = services.BuildServiceProvider();
            var mediator = provider.GetRequiredService<IMediator>();

            var response = await mediator.Send(new TestRequest { Message = "generated" });
            response.Should().NotBeNullOrEmpty();

            var command = new TestRequestWithoutResponse { Message = "generated" };
            await mediator.Send(command);
            command.Handled.Should().BeTrue();

            var items = new List<string>();
            await foreach (var item in mediator.CreateStream(new TestStreamRequest { Prefix = "g", ItemCount = 2 }))
            {
                items.Add(item);
            }
            items.Should().Equal("g-0", "g-1");

            await mediator.Publish(new TestNotification { Message = "generated" });
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (tracker.GetHandleCount("TestNotificationHandler") == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            tracker.GetHandleCount("TestNotificationHandler").Should().Be(1);
        }

        private static IEnumerable<(Type, Type?)> HandlerPairs(IServiceCollection services)
            => services.Where(d => IsMediatorInterface(d.ServiceType)).Select(d => (d.ServiceType, d.ImplementationType)).ToList();

        private static bool IsMediatorInterface(Type type)
            => type.IsGenericType && type.Namespace == "Mediator" && type.IsInterface;
    }
}
