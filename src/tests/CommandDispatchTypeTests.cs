using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Which handler a command reaches when it is held as an interface or base type.
    /// <see cref="IMediator.Send(IRequest, CancellationToken)"/> always uses the command's actual type.
    /// <see cref="ICommandDispatcher.Send{TRequest}"/> uses the declared type <c>TRequest</c>, and falls back to the actual
    /// type only when the declared type has no handler, so no call that worked before changes.
    /// </summary>
    public class CommandDispatchTypeTests
    {
        [Fact]
        public async Task CommandDeclaredAsInterface_ShouldReachTheHandlerOfItsActualType()
        {
            // The dispatcher used to look the handler up for the interface and fail with "Handler not found".
            using var provider = Build();
            ICommandMarker command = new CancelOrder();

            await provider.GetRequiredService<IMediator>().Send(command);
            await provider.GetRequiredService<ICommandDispatcher>().Send(command);

            provider.GetRequiredService<Calls>().Should().Equal("CancelOrder", "CancelOrder");
        }

        [Fact]
        public async Task CommandsInAListOfTheirInterface_ShouldEachReachTheirOwnHandler()
        {
            using var provider = Build();
            var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
            var commands = new List<ICommandMarker> { new CancelOrder(), new ShipOrder() };

            foreach (var command in commands)
                await dispatcher.Send(command);

            provider.GetRequiredService<Calls>().Should().Equal("CancelOrder", "ShipOrderBehavior", "ShipOrder");
        }

        [Fact]
        public async Task CommandDeclaredAsInterface_ShouldRunTheBehaviorsOfItsActualType()
        {
            using var provider = Build();
            ICommandMarker command = new ShipOrder();

            await provider.GetRequiredService<ICommandDispatcher>().Send(command);

            provider.GetRequiredService<Calls>().Should().Equal("ShipOrderBehavior", "ShipOrder");
        }

        [Fact]
        public async Task HandlerForTheDeclaredType_ShouldStillWinInTheDispatcher()
        {
            // Unchanged: the dispatcher uses a handler registered for the declared (base) type, as before.
            using var provider = Build();
            BaseAudit command = new DerivedAudit();

            await provider.GetRequiredService<ICommandDispatcher>().Send(command);

            provider.GetRequiredService<Calls>().Should().Equal("BaseAudit");
        }

        [Fact]
        public async Task Mediator_ShouldUseTheActualTypeEvenWhenTheDeclaredTypeHasAHandler()
        {
            // Unchanged, and pinned here: IMediator has no generic command overload, so it always uses the actual type.
            using var provider = Build();
            BaseAudit command = new DerivedAudit();

            await provider.GetRequiredService<IMediator>().Send(command);

            provider.GetRequiredService<Calls>().Should().Equal("DerivedAudit");
        }

        [Fact]
        public async Task NoHandlerForEitherType_ShouldStillFailWithHandlerNotFound()
        {
            using var provider = Build();
            ICommandMarker command = new UnhandledCommand();

            await provider.GetRequiredService<ICommandDispatcher>().Invoking(d => d.Send(command))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("Handler not found: IRequestHandler<*>", "the message names the request type, not IRequestHandler`1");
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<Calls>();
            services.AddTransient<IRequestHandler<ShipOrder>, ShipOrderHandler>();
            services.AddTransient<IRequestHandler<CancelOrder>, CancelOrderHandler>();
            services.AddTransient<IRequestHandler<BaseAudit>, BaseAuditHandler>();
            services.AddTransient<IRequestHandler<DerivedAudit>, DerivedAuditHandler>();
            services.AddTransient<IPipelineBehavior<ShipOrder>, ShipOrderBehavior>();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider();
        }

        public sealed class Calls : ConcurrentQueue<string>;

        public interface ICommandMarker : IRequest;

        public sealed class ShipOrder : ICommandMarker;

        public sealed class CancelOrder : ICommandMarker;

        // Intentionally has no handler: the test checks the failure.
        #pragma warning disable MEDGEN005
        public sealed class UnhandledCommand : ICommandMarker;
        #pragma warning restore MEDGEN005

        public class BaseAudit : IRequest;

        public sealed class DerivedAudit : BaseAudit;

        public sealed class ShipOrderHandler(Calls calls) : IRequestHandler<ShipOrder>
        {
            public Task Handle(ShipOrder request, CancellationToken cancellationToken) { calls.Enqueue("ShipOrder"); return Task.CompletedTask; }
        }

        public sealed class CancelOrderHandler(Calls calls) : IRequestHandler<CancelOrder>
        {
            public Task Handle(CancelOrder request, CancellationToken cancellationToken) { calls.Enqueue("CancelOrder"); return Task.CompletedTask; }
        }

        public sealed class BaseAuditHandler(Calls calls) : IRequestHandler<BaseAudit>
        {
            public Task Handle(BaseAudit request, CancellationToken cancellationToken) { calls.Enqueue("BaseAudit"); return Task.CompletedTask; }
        }

        public sealed class DerivedAuditHandler(Calls calls) : IRequestHandler<DerivedAudit>
        {
            public Task Handle(DerivedAudit request, CancellationToken cancellationToken) { calls.Enqueue("DerivedAudit"); return Task.CompletedTask; }
        }

        public sealed class ShipOrderBehavior(Calls calls) : IPipelineBehavior<ShipOrder>
        {
            public Task Handle(ShipOrder request, RequestHandlerDelegate next, CancellationToken cancellationToken) { calls.Enqueue("ShipOrderBehavior"); return next(); }
        }
    }
}
