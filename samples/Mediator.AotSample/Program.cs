using System.Runtime.CompilerServices;
using Mediator;
using Mediator.AotSample;
using Mediator.Persistence;
using Mediator.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var persistenceDirectory = Path.Combine(Path.GetTempPath(), "mediator-aot-sample", Guid.NewGuid().ToString("N"));

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<Tracker>();
services.AddTransient<IPipelineBehavior<GetGreeting, string>, UppercaseGreeting>();

// Persistence under Native AOT: provide a serializer backed by a source-generated JsonSerializerContext.
services.AddSingleton<INotificationSerializer>(new JsonNotificationSerializer(SampleJsonContext.Default.Options));
services.AddSingleton<INotificationPersistence>(new FileNotificationPersistence(persistenceDirectory));

services.AddMediatorCore(options =>
{
    options.NotificationWorkerCount = 1;
    options.EnablePersistence = true;
});
services.AddMediatorHandlers(); // source-generated: no reflection

using var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();
var tracker = provider.GetRequiredService<Tracker>();


var greeting = await mediator.Send(new GetGreeting("aot"));
Check(greeting == "HELLO, AOT!", $"request with pipeline behavior -> {greeting}");

var total = await mediator.Send(new GetTotal([1, 2, 3]));
Check(total == 6, $"value-type response -> {total}");

await mediator.Send(new RecordVisit("home"));
Check(tracker.Events.Contains("visit:home"), "command");

var streamed = new List<int>();
await foreach (var i in mediator.CreateStream(new CountTo(3)))
{
    streamed.Add(i);
}
Check(streamed.SequenceEqual([1, 2, 3]), $"stream -> {string.Join(",", streamed)}");

await mediator.Publish(new OrderPlaced { OrderId = "42" });
var deadline = DateTime.UtcNow.AddSeconds(5);
while ((!tracker.Events.Contains("email:42") || !tracker.Events.Contains("audit:42") || Directory.EnumerateFiles(persistenceDirectory).Any())
       && DateTime.UtcNow < deadline)
{
    await Task.Delay(20);
}
Check(tracker.Events.Contains("email:42") && tracker.Events.Contains("audit:42"), "notification to two handlers");
Check(!Directory.EnumerateFiles(persistenceDirectory).Any(), "persisted notification completed and removed");

Directory.Delete(persistenceDirectory, recursive: true);
Console.WriteLine($"All checks passed (dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}).");
return 0;


static void Check(bool condition, string description)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
    if (!condition) Environment.Exit(1);
}
