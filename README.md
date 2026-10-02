<p align="center">
  <img src="https://raw.githubusercontent.com/SwartBergStudio/Mediator/main/assets/icon-256.png" alt="SwartBerg.Mediator" width="160" />
</p>

# SwartBerg.Mediator

[![Build Status](https://github.com/SwartBergStudio/Mediator/workflows/CI/badge.svg)](https://github.com/SwartBergStudio/Mediator/actions/workflows/ci.yml)
[![Release](https://github.com/SwartBergStudio/Mediator/workflows/Release/badge.svg)](https://github.com/SwartBergStudio/Mediator/actions/workflows/release.yml)
[![NuGet Version](https://img.shields.io/nuget/v/SwartBerg.Mediator.svg?label=SwartBerg.Mediator)](https://www.nuget.org/packages/SwartBerg.Mediator/)
[![NuGet Version](https://img.shields.io/nuget/v/SwartBerg.Mediator.SourceGenerator.svg?label=SourceGenerator)](https://www.nuget.org/packages/SwartBerg.Mediator.SourceGenerator/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SwartBerg.Mediator.svg)](https://www.nuget.org/packages/SwartBerg.Mediator/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A fast mediator for .NET 8, 9 and 10. It supports requests, commands, streaming, pipeline behaviors, background notification processing and crash-safe notification persistence, and runs under Native AOT.

When MediatR moved to a paid license, I built my own mediator using the same familiar interfaces, so it feels at home if you've used MediatR. It is free and adds built-in background processing, notification persistence and Native AOT support.

The name "SwartBerg" means "Black Mountain" in Afrikaans, it is a combination of my surname and my wife's maiden name.  If you like to thank me for the library buy me a coffee.  Link is at the bottom of this readme.

See [CHANGELOG.md](https://github.com/SwartBergStudio/Mediator/blob/main/CHANGELOG.md) for what changed in each release and upgrade notes.

## Features

- **High performance**: strongly-typed dispatch, cached per message type, with no per-call reflection or boxing. ([benchmarks](#benchmarks))
- **Native AOT and trimming**: optional source generator registers handlers at compile time
- **Streaming**: `IAsyncEnumerable<T>` responses via `CreateStream`, with stream pipeline behaviors
- **Background processing**: non-blocking notification dispatch with a worker pool
- **Pipeline behaviors**: plug-in cross-cutting concerns for requests and streams
- **Configurable persistence**: pluggable store and serializer, with retries and exponential backoff
- **UI-safe async**: no extra awaits on the request path, and `ConfigureAwait(false)` on the mediator's own background awaits (see [Scopes, Blazor and ConfigureAwait](#scopes-blazor-and-configureawait))
- **Lightweight**: low allocations, minimal dependencies

## Packages

| Package | Purpose |
|---|---|
| [`SwartBerg.Mediator`](https://www.nuget.org/packages/SwartBerg.Mediator/) | The mediator. Required. |
| [`SwartBerg.Mediator.SourceGenerator`](https://www.nuget.org/packages/SwartBerg.Mediator.SourceGenerator/) | Optional. Compile-time handler registration for Native AOT / trimmed apps (or simply faster startup). |

Both packages are released together with the same version number. The current version is shown on the badges above.

## Requirements

- .NET 8, 9 or 10
- Works with ASP.NET Core, Blazor (Server, WebAssembly, Auto), .NET MAUI, console, WPF and WinForms applications

## Installation

```bash
dotnet add package SwartBerg.Mediator

# Optional: Native AOT / trimming support
dotnet add package SwartBerg.Mediator.SourceGenerator
```

`dotnet add package` installs the latest version. To pin a version in the project file, use the version from the badge above:

```xml
<PackageReference Include="SwartBerg.Mediator" Version="x.y.z" />
<PackageReference Include="SwartBerg.Mediator.SourceGenerator" Version="x.y.z" PrivateAssets="all" />
```

## Quick Start

### 1. Define your requests and handlers

```csharp
public class GetUserQuery : IRequest<User>
{
    public int UserId { get; set; }
}

public class GetUserHandler : IRequestHandler<GetUserQuery, User>
{
    public Task<User> Handle(GetUserQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new User { Id = request.UserId, Name = "John Doe" });
}

public class CreateUserCommand : IRequest
{
    public string Name { get; set; } = "";
}

public class CreateUserHandler : IRequestHandler<CreateUserCommand>
{
    public Task Handle(CreateUserCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class UserCreatedNotification : INotification
{
    public int UserId { get; set; }
}

public class SendWelcomeEmailHandler : INotificationHandler<UserCreatedNotification>
{
    public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

### 2. Register services

Pick one of the two registration styles. They register exactly the same handlers.

**Reflection (assembly scanning).** This is the simplest option:

```csharp
builder.Services.AddMediator(typeof(Program).Assembly);
```

**Source generated (Native AOT safe).** This requires `SwartBerg.Mediator.SourceGenerator`:

```csharp
builder.Services.AddMediatorCore(options => options.NotificationWorkerCount = 4); // optional: options
builder.Services.AddMediatorHandlers();                                          // generated at compile time
```

### 3. Use the mediator

```csharp
public class UserController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id}")]
    public Task<User> GetUser(int id) => mediator.Send(new GetUserQuery { UserId = id });

    [HttpPost]
    public async Task CreateUser(CreateUserCommand command)
    {
        await mediator.Send(command);
        await mediator.Publish(new UserCreatedNotification { UserId = 1 });
    }
}
```

## Streaming

```csharp
public record ChatPrompt(string Text) : IStreamRequest<string>;

public class ChatHandler : IStreamRequestHandler<ChatPrompt, string>
{
    public async IAsyncEnumerable<string> Handle(ChatPrompt request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var word in request.Text.Split(' '))
        {
            await Task.Delay(50, cancellationToken);
            yield return word;
        }
    }
}

await foreach (var token in mediator.CreateStream(new ChatPrompt("hello streaming world"), cancellationToken))
{
    Console.Write(token);
}
```

## Pipeline Behaviors

Behaviors wrap handlers for cross-cutting concerns such as validation, logging or transactions. The first one registered is the outermost.

### Open generic behaviors (recommended)

Declare open generic behaviors once per project that contains handlers. The behaviors are listed in execution order:

```csharp
[assembly: MediatorPipelineBehaviors(typeof(LoggingBehavior<,>), typeof(ValidationBehavior<,>))]

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return await next();
    }
}
```

Each listed behavior is registered closed (for example `ValidationBehavior<CreateUser, Guid>`) for every request handled in that project.
- **Constraints decide where it applies.** For example, `where TRequest : ICommand` limits a behavior to commands.
- **Both registration styles honour it.** The source generator emits these registrations at compile time, and `AddMediator(assemblies)` applies the same attribute when scanning.
- **Stream behaviors work the same way.** Open generic `IStreamPipelineBehavior<,>` types can be listed in the same attribute.

### Registering behaviors manually

You can still register behaviors yourself, which is how it worked before:

```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)); // open generic
services.AddTransient<IPipelineBehavior<GetUserQuery, User>, CachingBehavior>();     // closed, one request
```

> **Native AOT:** the DI container can't close an open generic service over a value type without dynamic code. Under Native AOT, `services.AddTransient(typeof(IPipelineBehavior<,>), ...)` therefore throws for requests returning `int`, `Guid`, `bool`, and so on. Use the attribute instead. Don't register the same behavior both ways, or it runs twice.

Closed (non-generic) stream behaviors are discovered automatically.

## Notifications and Persistence

`Publish` queues the notification on an in-memory channel and returns immediately. Background workers then invoke every handler. A failing handler is logged and does not affect the other handlers.

> Notification handlers run in the background in a **new DI scope**, not the publisher's. Scoped services such as a per-user `IUserSession` are therefore fresh instances there. Put what the handlers need, such as the user id, in the notification itself.

With `EnablePersistence = true`, each notification is also written to storage before it is queued:

- A notification is removed from storage once all its handlers succeed.
- If a handler fails, the notification is retried with exponential backoff, up to `MaxRetryAttempts`, and then dropped. A retry runs all handlers for that notification again, so handlers should be idempotent.
- Notifications still in storage after a crash or restart are recovered by a periodic loop.

```csharp
services.AddMediator(options =>
{
    options.EnablePersistence = true;
    options.MaxRetryAttempts = 3;
    options.InitialRetryDelay = TimeSpan.FromMinutes(1);
}, typeof(Program).Assembly);
```

### Custom persistence

Register your own implementations **before** calling `AddMediator` / `AddMediatorCore`, and they will be used instead of the defaults:

```csharp
services.AddSingleton<INotificationPersistence, SqlServerNotificationPersistence>();
services.AddSingleton<INotificationSerializer, MyNotificationSerializer>();
services.AddMediator(options => options.EnablePersistence = true, typeof(Program).Assembly);
```

## Native AOT and Trimming

`AddMediator(assemblies)` discovers handlers with reflection. Trimming can remove handlers it never sees referenced, so in trimmed or Native AOT apps (for example published MAUI iOS/Android apps or Native AOT APIs) use the source generator instead:

1. Add `SwartBerg.Mediator.SourceGenerator` to **every project that contains handlers**.
2. Each such project gets an `internal` `AddMediatorHandlers()` extension method in its root namespace. Call it from that project. In a Clean Architecture solution that is typically the Application layer's `AddApplication()`:

   ```csharp
   namespace MyApp.Application;

   public static class DependencyInjection
   {
       public static IServiceCollection AddApplication(this IServiceCollection services)
       {
           services.AddMediatorCore(options => options.NotificationWorkerCount = 2);
           services.AddMediatorHandlers();
           return services;
       }
   }
   ```

3. Declare open generic behaviors with `[assembly: MediatorPipelineBehaviors(...)]`, as shown under [Pipeline Behaviors](#pipeline-behaviors).

The generated code registers every handler and pre-creates its strongly-typed dispatcher, so nothing is resolved by reflection at runtime. The generator reports:

| Id | Severity | Meaning |
|---|---|---|
| `MEDGEN001` | Warning | `SwartBerg.Mediator` is not referenced by the project. |
| `MEDGEN002` | Warning | A handler (or its message type) is `private`/`protected` and can't be registered. Make it `internal` or `public`. |
| `MEDGEN003` | Info | An open generic handler or behavior was skipped. List it in `[assembly: MediatorPipelineBehaviors(...)]` or register it explicitly. |
| `MEDGEN004` | Warning | A type in `[assembly: MediatorPipelineBehaviors(...)]` isn't an open generic pipeline behavior. |

### Persistence under Native AOT

The default `JsonNotificationSerializer()` uses reflection-based System.Text.Json. For Native AOT, register a serializer backed by a source-generated `JsonSerializerContext` that lists your persisted notification types:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserCreatedNotification))]
internal partial class NotificationJsonContext : JsonSerializerContext;

services.AddSingleton<INotificationSerializer>(new JsonNotificationSerializer(NotificationJsonContext.Default.Options));
services.AddMediatorCore(options => options.EnablePersistence = true);
services.AddMediatorHandlers();
```

[`samples/Mediator.AotSample`](samples/Mediator.AotSample) is a complete example. CI publishes it with Native AOT and runs it on every build.

## Scopes, Blazor and ConfigureAwait

**Scopes.** ASP.NET Core creates a DI scope per web request, and Blazor creates one per circuit (per user connection). Request, command and stream handlers, and their behaviors, are resolved from the scope of the code that calls the mediator. Scoped services such as `IUserSession` or a `DbContext` are therefore the caller's own. That holds whether `IMediator` is injected into a component, a controller or a service.

**Exceptions.** Request, command and stream handlers run inside your `await`. The mediator doesn't wrap or log their exceptions: they reach your code, and your host's logging, unchanged. Background notification failures are logged by the mediator, because nobody awaits them.

**ConfigureAwait.** The mediator adds no `await` of its own between your code and a request handler. So:
- In a Blazor component, `await Mediator.Send(...)` resumes on the component's synchronization context, as usual, so `StateHasChanged` and UI updates work.
- Inside handlers and library code, keep using `.ConfigureAwait(false)` on your own awaits. It avoids hopping back to the UI context and prevents sync-over-async deadlocks.
- `UseConfigureAwaitGlobally` (default `true`) applies to the mediator's own awaits when publishing notifications and persisting them.

## Configuration Options

```csharp
services.AddMediator(options =>
{
    options.NotificationWorkerCount = 4;                    // background workers (default: processor count)
    options.ChannelCapacity = 1000;                         // queued notifications before Publish waits
    options.EnablePersistence = true;                       // default: false
    options.ProcessingInterval = TimeSpan.FromSeconds(30);  // recovery loop interval
    options.ProcessingBatchSize = 50;                       // persisted items recovered per pass
    options.MaxRetryAttempts = 3;
    options.InitialRetryDelay = TimeSpan.FromMinutes(2);
    options.RetryDelayMultiplier = 2.0;
    options.CleanupRetentionPeriod = TimeSpan.FromHours(24);
    options.CleanupInterval = TimeSpan.FromHours(1);
    // options.UseConfigureAwaitGlobally = false;           // mediator's own awaits (publish/persistence) only
}, typeof(Program).Assembly);
```

`AddMediatorCore(options => ...)` accepts the same options.

## Architecture

```
Send / CreateStream ──► typed dispatcher (cached per message type) ──► behaviors ──► handler

Publish ──► [Persist] ──► Channel ──► Background workers ──► handlers (own DI scope)
               │                                  │
               │                                  ├─ all succeeded ──► Complete (remove)
               │                                  └─ a handler failed ──► Fail (retry with backoff)
               └─◄── Recovery loop (pending items after restart / due retries)
```

Highlights:
- Strongly-typed dispatchers created once per message type and shared across DI scopes
- Fast path for synchronously completed handlers
- Pooled task arrays for multi-handler fan-out
- Exponential retry with precomputed delays

## Benchmarks

Dispatch overhead measured with BenchmarkDotNet on .NET 10, x64 Linux:

| Scenario | Time | Allocated |
|---|---|---|
| Request | 67 ns | 64 B |
| Command | 31 ns | 24 B |
| Request + 1 pipeline behavior | 129 ns | 288 B |
| New DI scope + request | 160 ns | 224 B |
| New DI scope + async handler (awaits) + 1 behavior | 2.07 µs | 664 B |

The last row is closest to a real web request (new scope, a handler that awaits, one behavior); most of that time is the handler's own async work. Numbers vary per machine; run the benchmarks yourself:

```bash
cd src/benchmarks
dotnet run -c Release -- --filter *DispatchBenchmarks*
dotnet run -c Release -- --filter *Publish*
```

## Testing

```bash
dotnet test
```

The suites, all run by CI on every push and pull request:

| Suite | What it covers |
|---|---|
| `src/tests` | Library behavior, on .NET 8, 9 and 10 |
| `src/tests-aot` | The **same** tests again with dynamic code disabled (Native AOT semantics) and typed dispatchers coming only from the source generator. Any message type the generator misses fails here. |
| `src/generator-tests` | Generator output and diagnostics: constraints, ordering, accessibility and invalid declarations |
| `samples/Mediator.AotSample` | Published with **Native AOT** (warnings as errors) and run. It covers requests, value-type responses, open generic and stream behaviors, commands, streams, exceptions, notifications and persistence recovery. |
| Package validation | Packing fails if a public API changed incompatibly since the last release |

Two tests protect the generated path:
- generated registrations must equal reflection scanning;
- persisted payloads and files must stay byte-identical to the earlier format.

## Releases and Versioning

Versions come from git tags (`vX.Y.Z`). The **Release** workflow is started manually on `main` and takes a `patch` / `minor` / `major` choice:

- It works out the next version from the latest tag.
- It builds, tests and runs the Native AOT sample.
- It publishes both packages to NuGet using trusted publishing.
- It creates the GitHub release and tag.

Before releasing, move the `Unreleased` entries in [CHANGELOG.md](https://github.com/SwartBergStudio/Mediator/blob/main/CHANGELOG.md) under the new version. The version in the project files is a `0.0.0-dev` placeholder for local builds. The NuGet package README is this file, and the version badges at the top update automatically.

## Contributing

1. Fork the repository
2. Create a descriptively named feature branch: `git checkout -b feature/amazing-feature`
3. Add changes + tests
4. Run the tests (and the benchmarks for performance-sensitive changes)
5. Commit: `git commit -m 'Add amazing feature'`
6. Push: `git push origin feature/amazing-feature`
7. Open a PR

## License

MIT License - see [LICENSE](LICENSE).

## Support

Open issues for bugs or features. Provide clear reproduction steps.

## Appreciation (Optional)

Free forever. If it helps you and you want to buy a coffee:

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/swartbergstudio)

**Always free. No premium features, no paid support.**
