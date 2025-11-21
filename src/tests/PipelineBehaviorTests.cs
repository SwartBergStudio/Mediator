using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    public class PipelineBehaviorTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly IMediator _mediator;

        public PipelineBehaviorTests()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddMediator(typeof(TestRequestHandler).Assembly);
            
            // Register multiple pipeline behaviors
            services.AddTransient<IPipelineBehavior<TestRequest, string>, LoggingPipelineBehavior>();
            services.AddTransient<IPipelineBehavior<TestRequest, string>, TimingPipelineBehavior>();
            services.AddTransient<IPipelineBehavior<TestRequest, string>, ValidationPipelineBehavior>();

            _serviceProvider = services.BuildServiceProvider();
            _mediator = _serviceProvider.GetRequiredService<IMediator>();
        }

        [Fact]
        public async Task Send_WithMultiplePipelineBehaviors_ShouldExecuteAllInOrder()
        {
            // Arrange
            LoggingPipelineBehavior.ClearExecutionLog();
            TimingPipelineBehavior.ClearExecutionLog();
            ValidationPipelineBehavior.ClearExecutionLog();

            var request = new TestRequest { Message = "Pipeline test" };

            // Act
            var response = await _mediator.Send(request);

            // Assert
            response.Should().Be("Handled: Pipeline test");
            
            // All behaviors should have executed
            LoggingPipelineBehavior.GetExecutionLog().Should().Contain("Before");
            LoggingPipelineBehavior.GetExecutionLog().Should().Contain("After");
            TimingPipelineBehavior.GetExecutionLog().Should().Contain("Started");
            TimingPipelineBehavior.GetExecutionLog().Should().ContainMatch("Completed in *ms");
            ValidationPipelineBehavior.GetExecutionLog().Should().Contain("Validated");
        }

        [Fact]
        public async Task Send_WithValidationFailure_ShouldNotCallHandler()
        {
            // Arrange
            LoggingPipelineBehavior.ClearExecutionLog();
            var request = new TestRequest { Message = "INVALID" }; // Special message to trigger validation failure

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => _mediator.Send(request));
            
            // Handler should not have been called
            LoggingPipelineBehavior.GetExecutionLog().Should().Contain("Before");
            LoggingPipelineBehavior.GetExecutionLog().Should().NotContain("After");
        }

        [Fact]
        public async Task Send_WithExceptionInPipeline_ShouldPropagateException()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddMediator(typeof(TestRequestHandler).Assembly);
            services.AddTransient<IPipelineBehavior<TestRequest, string>, ExceptionThrowingPipelineBehavior>();

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            var request = new TestRequest { Message = "Exception test" };

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(request));
            exception.Message.Should().Be("Pipeline exception");
        }

        [Fact]
        public async Task Send_WithGenericPipelineBehavior_ShouldExecute()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddMediator(typeof(TestRequestHandler).Assembly);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(GenericLoggingPipelineBehavior<,>));

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            GenericLoggingPipelineBehavior<TestRequest, string>.ClearExecutedRequests();
            var request = new TestRequest { Message = "Generic pipeline test" };

            // Act
            var response = await mediator.Send(request);

            // Assert
            response.Should().Be("Handled: Generic pipeline test");
            GenericLoggingPipelineBehavior<TestRequest, string>.GetExecutedRequests().Should().Contain("Generic pipeline test");
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
        }
    }

    // Test pipeline behaviors
    public class LoggingPipelineBehavior : IPipelineBehavior<TestRequest, string>
    {
        private static readonly List<string> _executionLog = new();

        public static List<string> GetExecutionLog()
        {
            lock (_executionLog)
            {
                return _executionLog.ToList();
            }
        }

        public static void ClearExecutionLog()
        {
            lock (_executionLog)
            {
                _executionLog.Clear();
            }
        }

        public async Task<string> Handle(TestRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            lock (_executionLog)
            {
                _executionLog.Add("Before");
            }
            var response = await next();
            lock (_executionLog)
            {
                _executionLog.Add("After");
            }
            return response;
        }
    }

    public class TimingPipelineBehavior : IPipelineBehavior<TestRequest, string>
    {
        private static readonly List<string> _executionLog = new();

        public static List<string> GetExecutionLog()
        {
            lock (_executionLog)
            {
                return _executionLog.ToList();
            }
        }

        public static void ClearExecutionLog()
        {
            lock (_executionLog)
            {
                _executionLog.Clear();
            }
        }

        public async Task<string> Handle(TestRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            lock (_executionLog)
            {
                _executionLog.Add("Started");
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await next();
            stopwatch.Stop();
            lock (_executionLog)
            {
                _executionLog.Add($"Completed in {stopwatch.ElapsedMilliseconds}ms");
            }
            return response;
        }
    }

    public class ValidationPipelineBehavior : IPipelineBehavior<TestRequest, string>
    {
        private static readonly List<string> _executionLog = new();

        public static List<string> GetExecutionLog()
        {
            lock (_executionLog)
            {
                return _executionLog.ToList();
            }
        }

        public static void ClearExecutionLog()
        {
            lock (_executionLog)
            {
                _executionLog.Clear();
            }
        }

        public async Task<string> Handle(TestRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            if (request.Message == "INVALID")
            {
                throw new ArgumentException("Invalid request");
            }

            lock (_executionLog)
            {
                _executionLog.Add("Validated");
            }
            return await next();
        }
    }

    public class ExceptionThrowingPipelineBehavior : IPipelineBehavior<TestRequest, string>
    {
        public Task<string> Handle(TestRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Pipeline exception");
        }
    }

    public class GenericLoggingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private static readonly List<string> _executedRequests = new();

        public static List<string> GetExecutedRequests()
        {
            lock (_executedRequests)
            {
                return _executedRequests.ToList();
            }
        }

        public static void ClearExecutedRequests()
        {
            lock (_executedRequests)
            {
                _executedRequests.Clear();
            }
        }

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (request is TestRequest testRequest)
            {
                lock (_executedRequests)
                {
                    _executedRequests.Add(testRequest.Message);
                }
            }
            
            return await next();
        }
    }
}