using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Background notification handlers run outside any request. The publisher is a singleton created inside the first
    /// request that publishes, so its long-lived worker loops must not capture that request's ExecutionContext
    /// (AsyncLocal state such as IHttpContextAccessor's HttpContext, logger scopes and Activity.Current).
    /// </summary>
    public class BackgroundExecutionContextTests
    {
        private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

        /// <summary>Stands in for AsyncLocal request state such as IHttpContextAccessor.HttpContext.</summary>
        public static readonly AsyncLocal<string?> CurrentUser = new();

        [Fact]
        public async Task BackgroundHandlers_ShouldNotSeeTheExecutionContextOfTheFirstPublisher()
        {
            using var provider = Build();
            var recorder = provider.GetRequiredService<WhoAmIRecorder>();

            // Request 1 creates the publisher singleton.
            await RunAsSeparateRequest(provider, "alice", new WhoAmI("first"));
            await recorder.WaitFor("first");

            await RunAsSeparateRequest(provider, "bob", new WhoAmI("second"));
            await recorder.WaitFor("second");

            recorder.Seen["first"].Should().BeNull("a background handler runs outside the request that published it");
            recorder.Seen["second"].Should().BeNull("a background handler must not see the first publisher's request state");
        }

        private static Task RunAsSeparateRequest(IServiceProvider provider, string user, WhoAmI notification)
        {
            // Each request starts from a clean ExecutionContext, as ASP.NET Core requests do.
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(async () =>
                {
                    CurrentUser.Value = user;
                    using var scope = provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(notification);
                });
            }
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<WhoAmIRecorder>();
            services.AddMediator(options => options.NotificationWorkerCount = 1, typeof(BackgroundExecutionContextTests).Assembly);
            return services.BuildServiceProvider();
        }

        public sealed record WhoAmI(string Id) : INotification;

        public sealed class WhoAmIRecorder
        {
            private readonly ConcurrentDictionary<string, TaskCompletionSource> _handled = new();

            public ConcurrentDictionary<string, string?> Seen { get; } = new();

            public void Record(string id, string? user)
            {
                Seen[id] = user;
                Signal(id).TrySetResult();
            }

            public Task WaitFor(string id) => Signal(id).Task.WaitAsync(s_timeout);

            private TaskCompletionSource Signal(string id)
                => _handled.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public sealed class WhoAmIHandler(WhoAmIRecorder recorder) : INotificationHandler<WhoAmI>
        {
            public Task Handle(WhoAmI notification, CancellationToken cancellationToken)
            {
                recorder.Record(notification.Id, CurrentUser.Value);
                return Task.CompletedTask;
            }
        }
    }
}
