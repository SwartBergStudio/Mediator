using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Handlers and behaviors must resolve scoped services (such as a per-user IUserSession) from the caller's DI scope,
    /// i.e. the current web request or Blazor circuit. Notification handlers run in the background in their own scope.
    /// </summary>
    public class ScopedServiceTests
    {
        [Fact]
        public async Task RequestHandlersAndBehaviors_ShouldUseTheCallersScopedServices()
        {
            using var provider = Build();

            using var alice = provider.CreateScope();
            using var bob = provider.CreateScope();
            alice.ServiceProvider.GetRequiredService<UserSession>().UserId = "alice";
            bob.ServiceProvider.GetRequiredService<UserSession>().UserId = "bob";

            // Interleave the two users to make sure nothing is shared between scopes.
            var aliceTask = alice.ServiceProvider.GetRequiredService<IMediator>().Send(new GetCurrentUser());
            var bobTask = bob.ServiceProvider.GetRequiredService<IMediator>().Send(new GetCurrentUser());

            (await aliceTask).Should().Be("alice seen by behavior as alice");
            (await bobTask).Should().Be("bob seen by behavior as bob");
        }

        [Fact]
        public async Task MediatorInjectedIntoScopedService_ShouldUseThatScope()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<UserSession>().UserId = "carol";

            var page = scope.ServiceProvider.GetRequiredService<FakePage>();

            (await page.LoadAsync()).Should().Be("carol seen by behavior as carol");
        }

        [Fact]
        public async Task NotificationHandlers_ShouldRunInTheirOwnScope()
        {
            var seen = new ConcurrentQueue<string?>();
            using var provider = Build(services => services.AddSingleton(seen));
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<UserSession>().UserId = "dave";

            await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new UserNotified { UserId = "dave" });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (seen.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);

            // Background handlers get a fresh scope: pass what they need in the notification itself.
            seen.Should().ContainSingle().Which.Should().Be("session:<none> notification:dave");
        }

        private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddScoped<UserSession>();
            services.AddScoped<FakePage>();
            services.AddTransient<IRequestHandler<GetCurrentUser, string>, GetCurrentUserHandler>();
            services.AddTransient<IPipelineBehavior<GetCurrentUser, string>, SessionCheckingBehavior>();
            services.AddTransient<INotificationHandler<UserNotified>, UserNotifiedHandler>();
            configure?.Invoke(services);
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public sealed class UserSession
        {
            public string? UserId { get; set; }
        }

        /// <summary>Stands in for a Blazor component or controller that gets IMediator injected.</summary>
        public sealed class FakePage(IMediator mediator)
        {
            public Task<string> LoadAsync() => mediator.Send(new GetCurrentUser());
        }

        public sealed class GetCurrentUser : IRequest<string>
        {
            public string? SeenByBehavior { get; set; }
        }

        public sealed class GetCurrentUserHandler(UserSession session) : IRequestHandler<GetCurrentUser, string>
        {
            public async Task<string> Handle(GetCurrentUser request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return $"{session.UserId} seen by behavior as {request.SeenByBehavior}";
            }
        }

        public sealed class SessionCheckingBehavior(UserSession session) : IPipelineBehavior<GetCurrentUser, string>
        {
            public Task<string> Handle(GetCurrentUser request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
            {
                request.SeenByBehavior = session.UserId;
                return next();
            }
        }

        public sealed class UserNotified : INotification
        {
            public string? UserId { get; set; }
        }

        public sealed class UserNotifiedHandler(UserSession session, ConcurrentQueue<string?> seen) : INotificationHandler<UserNotified>
        {
            public Task Handle(UserNotified notification, CancellationToken cancellationToken)
            {
                seen.Enqueue($"session:{session.UserId ?? "<none>"} notification:{notification.UserId}");
                return Task.CompletedTask;
            }
        }
    }
}
