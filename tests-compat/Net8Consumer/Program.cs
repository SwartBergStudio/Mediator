using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Net8Consumer;

public sealed record Ping(string Text) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult($"pong: {request.Text}");
}

public static class Program
{
    public static async Task<int> Main()
    {
        // AddMediatorHandlers() only exists if the source generator ran on this (.NET 8 SDK) compiler.
        await using var provider = new ServiceCollection().AddLogging().AddMediatorHandlers().BuildServiceProvider();
        var reply = await provider.GetRequiredService<IMediator>().Send(new Ping("net8"));
        Console.WriteLine(reply);
        return reply == "pong: net8" ? 0 : 1;
    }
}
