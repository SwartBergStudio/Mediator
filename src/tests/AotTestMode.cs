#if AOT_MODE
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Only compiled into Mediator.Tests.Aot, which runs every test in this folder with dynamic code disabled.
    /// </summary>
    internal static class AotTestMode
    {
        /// <summary>
        /// Registers the source-generated typed dispatchers before any test runs. Tests keep their own handler
        /// registrations (AddMediator scan or manual), but dispatch can only use what the generator produced:
        /// with dynamic code disabled there is no reflection fallback.
        /// </summary>
        [ModuleInitializer]
        internal static void RegisterGeneratedDispatchers() => new ServiceCollection().AddMediatorHandlers();
    }

    public class AotModeTests
    {
        [Fact]
        public void DynamicCode_ShouldBeDisabled()
        {
            RuntimeFeature.IsDynamicCodeSupported.Should().BeFalse("this project emulates Native AOT; otherwise the reflection fallback would hide missing generated dispatchers");
        }

        [Fact]
        public async Task Send_TypeWithoutGeneratedDispatcher_ShouldFailWithGuidance()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddMediatorCore();
            using var provider = services.BuildServiceProvider();
            var mediator = provider.GetRequiredService<IMediator>();

            var act = () => mediator.Send(new RequestWithoutHandler());

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*No handler wrapper is registered*SourceGenerator*");
        }

        private sealed class RequestWithoutHandler : IRequest<int>;
    }
}
#endif
