using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// [MediatorHandlerLifetime] on a handler class: the same result from assembly scanning, from the source-generated
    /// AddMediatorHandlers() and from both together. Handlers without the attribute stay transient.
    /// (The assembly-level attribute is covered in the generator tests, which compile a separate assembly.)
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public class HandlerLifetimeTests
    {
        [Theory]
        [InlineData("scan")]
        [InlineData("generated")]
        [InlineData("scan+generated")]
        public void SingletonHandler_ShouldBeOneObjectForEveryInterfaceItImplements(string registration)
        {
            using var provider = Build(registration);

            var first = provider.GetServices<INotificationHandler<LifetimeEventA>>().Should().ContainSingle("registering twice must not duplicate it").Subject;
            var again = provider.GetServices<INotificationHandler<LifetimeEventA>>().Single();
            var other = provider.GetServices<INotificationHandler<LifetimeEventB>>().Should().ContainSingle().Subject;
            var request = provider.GetRequiredService<IRequestHandler<LifetimeQuery, int>>();

            first.Should().BeOfType<SingletonLifetimeHandler>();
            again.Should().BeSameAs(first);
            other.Should().BeSameAs(first, "a singleton handler class is one object, not one per handler interface");
            request.Should().BeSameAs(first);
        }

        [Theory]
        [InlineData("scan")]
        [InlineData("generated")]
        public async Task SingletonHandler_ShouldKeepItsStateAcrossMessages(string registration)
        {
            using var provider = Build(registration);
            var mediator = provider.GetRequiredService<IMediator>();

            await mediator.PublishAndWait(new LifetimeEventA());
            await mediator.PublishAndWait(new LifetimeEventB());
            await mediator.PublishAndWait(new LifetimeEventA());

            (await mediator.Send(new LifetimeQuery())).Should().Be(3, "the same object handled all three notifications and the query");
        }

        [Theory]
        [InlineData("scan")]
        [InlineData("generated")]
        [InlineData("scan+generated")]
        public void ScopedHandler_ShouldBeOneObjectPerScope(string registration)
        {
            using var provider = Build(registration);
            using var scopeOne = provider.CreateScope();
            using var scopeTwo = provider.CreateScope();

            var first = scopeOne.ServiceProvider.GetServices<INotificationHandler<LifetimeScopedEventA>>().Should().ContainSingle().Subject;
            var other = scopeOne.ServiceProvider.GetServices<INotificationHandler<LifetimeScopedEventB>>().Single();
            var elsewhere = scopeTwo.ServiceProvider.GetServices<INotificationHandler<LifetimeScopedEventA>>().Single();

            first.Should().BeOfType<ScopedLifetimeHandler>();
            other.Should().BeSameAs(first);
            elsewhere.Should().NotBeSameAs(first);
        }

        [Theory]
        [InlineData("scan")]
        [InlineData("generated")]
        public void HandlerWithoutTheAttribute_ShouldStayTransient(string registration)
        {
            using var provider = Build(registration);

            var first = provider.GetServices<INotificationHandler<LifetimeUnmarkedEvent>>().Should().ContainSingle().Subject;
            var again = provider.GetServices<INotificationHandler<LifetimeUnmarkedEvent>>().Single();

            again.Should().NotBeSameAs(first);
        }

        [Fact]
        public void ScanAndGenerator_ShouldGiveEveryHandlerTheSameLifetime()
        {
            var scanned = new ServiceCollection().AddMediator(typeof(HandlerLifetimeTests).Assembly);
            var generated = new ServiceCollection().AddMediatorHandlers();

            Lifetimes(generated).Should().BeEquivalentTo(Lifetimes(scanned));
            Lifetimes(scanned).Should().Contain((typeof(INotificationHandler<LifetimeEventA>), ServiceLifetime.Singleton));
            Lifetimes(scanned).Should().Contain((typeof(INotificationHandler<LifetimeScopedEventA>), ServiceLifetime.Scoped));
            Lifetimes(scanned).Should().Contain((typeof(INotificationHandler<LifetimeUnmarkedEvent>), ServiceLifetime.Transient));
        }

        private static List<(Type, ServiceLifetime)> Lifetimes(IServiceCollection services)
            => services
                .Where(d => d.ServiceType.IsGenericType && d.ServiceType.Namespace == "Mediator" && d.ServiceType.IsInterface)
                .Select(d => (d.ServiceType, d.Lifetime))
                .ToList();

        private static ServiceProvider Build(string registration)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            if (registration.Contains("scan"))
                services.AddMediator(options => options.NotificationWorkerCount = 1, typeof(HandlerLifetimeTests).Assembly);
            if (registration.Contains("generated"))
            {
                services.AddMediatorCore(options => options.NotificationWorkerCount = 1);
                services.AddMediatorHandlers();
            }
            return services.BuildServiceProvider();
        }
    }

    public sealed class LifetimeEventA : INotification;

    public sealed class LifetimeEventB : INotification;

    public sealed class LifetimeQuery : IRequest<int>;

    [MediatorHandlerLifetime(ServiceLifetime.Singleton)]
    public sealed class SingletonLifetimeHandler : INotificationHandler<LifetimeEventA>, INotificationHandler<LifetimeEventB>, IRequestHandler<LifetimeQuery, int>
    {
        private int _handled;

        public Task Handle(LifetimeEventA notification, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handled);
            return Task.CompletedTask;
        }

        public Task Handle(LifetimeEventB notification, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handled);
            return Task.CompletedTask;
        }

        public Task<int> Handle(LifetimeQuery request, CancellationToken cancellationToken) => Task.FromResult(Volatile.Read(ref _handled));
    }

    public sealed class LifetimeScopedEventA : INotification;

    public sealed class LifetimeScopedEventB : INotification;

    [MediatorHandlerLifetime(ServiceLifetime.Scoped)]
    public sealed class ScopedLifetimeHandler : INotificationHandler<LifetimeScopedEventA>, INotificationHandler<LifetimeScopedEventB>
    {
        public Task Handle(LifetimeScopedEventA notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Handle(LifetimeScopedEventB notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class LifetimeUnmarkedEvent : INotification;

    public sealed class UnmarkedLifetimeHandler : INotificationHandler<LifetimeUnmarkedEvent>
    {
        public Task Handle(LifetimeUnmarkedEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
