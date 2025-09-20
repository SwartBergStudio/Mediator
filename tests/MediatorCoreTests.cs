namespace Mediator.Tests
{
    public class MediatorCoreTests
    {
        [Fact]
        public async Task Send_Command_ShouldExecuteHandler()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var command = new TestCommand { Data = "Test Command" };

                await mediator.Send(command);

                command.WasHandled.Should().BeTrue();
                tracker.GetCounter("CommandHandled").Should().Be(1);
                tracker.GetEvents().Should().Contain("Command processed: Test Command");
            }
        }

        [Fact]
        public async Task Send_Query_ShouldReturnResponse()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var query = new TestQuery { Query = "Get User Data" };

                var result = await mediator.Send(query);

                result.Should().Be("Result for: Get User Data");
                tracker.GetCounter("QueryHandled").Should().Be(1);
                tracker.GetEvents().Should().Contain("Query processed: Get User Data");
            }
        }

        [Fact]
        public async Task Send_Query_WithPipelineBehaviors_ShouldExecuteInOrder()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var query = new TestQuery { Query = "Pipeline Test" };

                var result = await mediator.Send(query);

                result.Should().Be("Result for: Pipeline Test");
                tracker.GetCounter("LoggingBehavior").Should().Be(1);
                tracker.GetCounter("ValidationBehavior").Should().Be(1);

                var events = tracker.GetEvents();
                events.Should().Contain("Validation: Validating request");
                events.Should().Contain("Validation: Request passed validation");
                events.Should().Contain("Logging: Before handler");
                events.Should().Contain("Logging: After handler");
            }
        }

        [Fact]
        public async Task Send_Query_WithValidationFailure_ShouldThrowException()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var query = new TestQuery { Query = "Invalid Query", ShouldFail = true };

                var exception = await Assert.ThrowsAsync<ArgumentException>(() => mediator.Send(query));
                exception.Message.Should().Be("Request failed validation");

                tracker.GetEvents().Should().Contain("Validation: Request failed validation");
                tracker.GetCounter("QueryHandled").Should().Be(0);
            }
        }

        [Fact]
        public async Task Publish_Event_ShouldCallAllHandlers()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var testEvent = new TestEvent { Message = "Test Event" };

                await mediator.Publish(testEvent);
                await Task.Delay(100);

                tracker.GetCounter("EventHandler1").Should().Be(1);
                tracker.GetCounter("EventHandler2").Should().Be(1);
                tracker.GetCounter("FailingEventHandler").Should().Be(1);

                var events = tracker.GetEvents();
                events.Should().Contain("Handler1 processed: Test Event");
                events.Should().Contain("Handler2 processed: Test Event");
                events.Should().Contain("Failing handler called: Test Event");
            }
        }

        [Fact]
        public async Task Publish_Event_WithFailingHandler_ShouldContinueWithOthers()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var testEvent = new TestEvent { Message = "Event with failure" };

                await mediator.Publish(testEvent);
                await Task.Delay(200);

                tracker.GetCounter("EventHandler1").Should().Be(1);
                tracker.GetCounter("EventHandler2").Should().Be(1);
                tracker.GetCounter("FailingEventHandler").Should().Be(1);
            }
        }

        [Fact]
        public async Task Send_WithCancellation_ShouldRespectCancellationToken()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                var query = new TestQuery { Query = "Cancelled Query" };
                var cts = new CancellationTokenSource();
                cts.Cancel();

                await Assert.ThrowsAsync<OperationCanceledException>(() => mediator.Send(query, cts.Token));
                tracker.GetCounter("QueryHandled").Should().Be(0);
            }
        }

        [Fact]
        public async Task Send_WithUnregisteredRequest_ShouldThrowException()
        {
            var (mediator, serviceProvider) = TestFactory.CreateSimpleMediator();

            using (serviceProvider)
            {
                var unhandledRequest = new UnhandledRequest { Message = "No handler for this" };

                await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(unhandledRequest));
            }
        }

        [Fact]
        public async Task Send_PerformanceTest_ShouldHandleHighThroughput()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();

            using (serviceProvider)
            {
                const int requestCount = 1000;
                var queries = Enumerable.Range(0, requestCount)
                    .Select(i => new TestQuery { Query = $"Query {i}" })
                    .ToArray();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var tasks = queries.Select(q => mediator.Send(q));
                var responses = await Task.WhenAll(tasks);
                stopwatch.Stop();

                responses.Should().HaveCount(requestCount);
                responses.All(r => r.StartsWith("Result for:")).Should().BeTrue();
                tracker.GetCounter("QueryHandled").Should().Be(requestCount);
                stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
            }
        }
    }

    public class UnhandledRequest : IRequest<string>
    {
        public string Message { get; set; } = string.Empty;
    }
}