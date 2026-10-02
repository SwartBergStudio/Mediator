using System.Runtime.CompilerServices;
using Mediator;
using Mediator.AotSample;
using Mediator.Persistence;
using Mediator.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// End-to-end Native AOT check. Published with PublishAot=true and run by CI; exits non-zero on the first failure.

var persistenceDirectory = Path.Combine(Path.GetTempPath(), "mediator-aot-sample", Guid.NewGuid().ToString("N"));
var serializer = new JsonNotificationSerializer(SampleJsonContext.Default.Options);

Check(!RuntimeFeature.IsDynamicCodeSupported, "dynamic code is disabled (Native AOT semantics)");

// Simulate a notification persisted by a previous run that crashed before handling it.
using (var previousRun = new FileNotificationPersistence(persistenceDirectory))
{
    var pending = new ReportGenerated { ReportId = "from-previous-run" };
    await previousRun.PersistAsync(new NotificationWorkItem(
        pending, typeof(ReportGenerated), DateTime.UtcNow, serializer.Serialize(pending, typeof(ReportGenerated))));
}

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Critical));
services.AddSingleton<Tracker>();
services.AddTransient<IPipelineBehavior<GetGreeting, string>, UppercaseGreeting>();
// CountingBehavior<,> is declared with [assembly: MediatorPipelineBehaviors] in Messages.cs, so the generator registers
// it closed per request. (AddTransient(typeof(IPipelineBehavior<,>), ...) fails under Native AOT for value-type responses.)

// Persistence under Native AOT: a serializer backed by a source-generated JsonSerializerContext.
services.AddSingleton<INotificationSerializer>(serializer);
services.AddSingleton<INotificationPersistence>(new FileNotificationPersistence(persistenceDirectory));

services.AddMediatorCore(options =>
{
    options.NotificationWorkerCount = 1;
    options.EnablePersistence = true;
    options.ProcessingInterval = TimeSpan.FromMilliseconds(100);
});
services.AddMediatorHandlers(); // source-generated: no reflection

using (var provider = services.BuildServiceProvider())
{
    var mediator = provider.GetRequiredService<IMediator>();
    var tracker = provider.GetRequiredService<Tracker>();

    var greeting = await mediator.Send(new GetGreeting("aot"));
    Check(greeting == "HELLO, AOT!", $"request with closed pipeline behavior -> {greeting}");
    Check(tracker.Events.Contains("behavior:GetGreeting"), "open generic pipeline behavior (reference-type response)");

    var total = await mediator.Send(new GetTotal([1, 2, 3]));
    Check(total == 6, $"value-type response -> {total}");
    Check(tracker.Events.Contains("behavior:GetTotal"), "open generic pipeline behavior (value-type response)");

    await mediator.Send(new RecordVisit("home"));
    Check(tracker.Events.Contains("visit:home"), "command");

    var streamed = new List<int>();
    await foreach (var i in mediator.CreateStream(new CountTo(3)))
    {
        streamed.Add(i);
    }
    Check(streamed.SequenceEqual([2, 4, 6]), $"stream with generated stream behavior -> {string.Join(",", streamed)}");

    try
    {
        await mediator.Send(new Explode());
        Check(false, "handler exception propagates");
    }
    catch (InvalidOperationException ex) when (ex.Message == "boom")
    {
        Check(true, "handler exception propagates");
    }

    await mediator.Publish(new OrderPlaced { OrderId = "42" });
    await WaitUntil(() => tracker.Events.Contains("email:42") && tracker.Events.Contains("audit:42") && tracker.Events.Contains("report:from-previous-run")
                          && !Directory.EnumerateFiles(persistenceDirectory).Any());
    Check(tracker.Events.Contains("email:42") && tracker.Events.Contains("audit:42"), "notification to two handlers");
    Check(tracker.Events.Contains("report:from-previous-run"), "notification persisted before restart is recovered");
    Check(!Directory.EnumerateFiles(persistenceDirectory).Any(), "persisted notifications completed and removed");
}

Directory.Delete(persistenceDirectory, recursive: true);
Console.WriteLine("All checks passed.");
return 0;

static async Task WaitUntil(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition() && DateTime.UtcNow < deadline)
    {
        await Task.Delay(20);
    }
}

static void Check(bool condition, string description)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
    if (!condition) Environment.Exit(1);
}
