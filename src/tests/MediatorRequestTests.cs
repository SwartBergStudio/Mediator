namespace Mediator.Tests
{
    public class MediatorRequestTests
    {
        [Fact]
        public async Task Send_Request_ShouldReturnResponse()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();
            
            using (serviceProvider)
            {
                var query = new TestQuery { Query = "Simple test" };

                var response = await mediator.Send(query);

                response.Should().Be("Result for: Simple test");
                tracker.GetCounter("QueryHandled").Should().Be(1);
            }
        }

        [Fact]
        public async Task Send_Command_ShouldCompleteSuccessfully()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();
            
            using (serviceProvider)
            {
                var command = new TestCommand { Data = "Simple command" };

                await mediator.Send(command);

                command.WasHandled.Should().BeTrue();
                tracker.GetCounter("CommandHandled").Should().Be(1);
            }
        }

        [Fact]
        public async Task Send_WithPipelineBehaviors_ShouldApplyCrossCuttingConcerns()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();
            
            using (serviceProvider)
            {
                var query = new TestQuery { Query = "Pipeline test" };

                var result = await mediator.Send(query);

                result.Should().Be("Result for: Pipeline test");
                tracker.GetCounter("LoggingBehavior").Should().Be(1);
                tracker.GetCounter("ValidationBehavior").Should().Be(1);
            }
        }
    }
}