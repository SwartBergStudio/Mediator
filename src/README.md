# SwartBerg.Mediator

[![Build Status](https://github.com/SwartBergStudio/Mediator/workflows/CI/badge.svg)](https://github.com/SwartBergStudio/Mediator/actions/workflows/ci.yml)
[![Release](https://github.com/SwartBergStudio/Mediator/workflows/Release/badge.svg)](https://github.com/SwartBergStudio/Mediator/actions/workflows/release.yml)
[![NuGet Version](https://img.shields.io/nuget/v/SwartBerg.Mediator.svg)](https://www.nuget.org/packages/SwartBerg.Mediator/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SwartBerg.Mediator.svg)](https://www.nuget.org/packages/SwartBerg.Mediator/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A fast mediator implementation for .NET 9 with background processing and notification persistence.

Inspired by MediatR, this library was created as a free alternative with similar patterns but optimized for performance and includes built-in persistence and background processing.

The name "SwartBerg" means "Black Mountain" in Afrikaans, it is a combination of my surname and my wife's maiden name.  If you like to thank me for the library buy me a coffee.  Link is at the bottom of this readme.

## Features

- **High Performance**: Precompiled generic delegates (no runtime expression compilation)
- **Background Processing**: Non-blocking notification dispatch with worker pool
- **Pipeline Behaviors**: Plug-in cross-cutting concerns
- **Configurable Persistence**: Pluggable store & serializer
- **Retry Logic**: Exponential backoff with precomputed delays
- **Modern Async Patterns**: Optional global ConfigureAwait(false)
- **Lightweight**: Low allocations, minimal deps
- **.NET 9 Ready**: Utilizes latest runtime improvements

## Requirements

- .NET 9.0 or later
- Works with:
  - .NET 9+ applications
  - .NET MAUI applications
  - Blazor applications
  - ASP.NET Core 9+ applications
  - Console applications
  - WPF applications
  - WinForms applications

## Installation

### Package Manager Console
```powershell
Install-Package SwartBerg.Mediator
```

### .NET CLI
```bash
dotnet add package SwartBerg.Mediator
```

### PackageReference
```xml
<PackageReference Include="SwartBerg.Mediator" Version="1.0.0" />
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
    {
        return Task.FromResult(new User { Id = request.UserId, Name = "John Doe" });
    }
}

public class CreateUserCommand : IRequest
{
    public string Name { get; set; }
    public string Email { get; set; }
}

public class CreateUserHandler : IRequestHandler<CreateUserCommand>
{
    public Task Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class UserCreatedNotification : INotification
{
    public int UserId { get; set; }
    public string Name { get; set; }
}

public class SendWelcomeEmailHandler : INotificationHandler<UserCreatedNotification>
{
    public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

### 2. Register services

```csharp
builder.Services.AddMediator(typeof(Program).Assembly);
```

### 3. Use the mediator

```csharp
public class UserController : ControllerBase
{
    private readonly IMediator _mediator;

    public UserController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{id}")]
    public async Task<User> GetUser(int id)
    {
        return await _mediator.Send(new GetUserQuery { UserId = id });
    }

    [HttpPost]
    public async Task CreateUser(CreateUserCommand command)
    {
        await _mediator.Send(command);
        await _mediator.Publish(new UserCreatedNotification { UserId = 1, Name = command.Name });
    }
}
```

## Advanced Configuration

### Pipeline Behaviors

```csharp
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return await next();
    }
}

services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
```

### Custom Persistence

```csharp
services.AddSingleton<INotificationPersistence, RedisNotificationPersistence>();
services.AddSingleton<INotificationPersistence, SqlServerNotificationPersistence>();
services.AddMediator(options => options.EnablePersistence = false, typeof(Program).Assembly);
```

### Configuration Options

```csharp
services.AddMediator(options =>
{
    options.NotificationWorkerCount = 4;
    options.EnablePersistence = true;
    options.ProcessingInterval = TimeSpan.FromSeconds(30);
    options.ProcessingBatchSize = 50;
    options.MaxRetryAttempts = 3;
    options.InitialRetryDelay = TimeSpan.FromMinutes(2);
    options.RetryDelayMultiplier = 2.0;
    options.CleanupRetentionPeriod = TimeSpan.FromHours(24);
    options.CleanupInterval = TimeSpan.FromHours(1);
    // options.UseConfigureAwaitGlobally = false; // only if you need sync ctx
}, typeof(Program).Assembly);
```

## Architecture

Channel-first with optional persistence:

1. In-memory channel dispatch
2. Optional persistence backup
3. Periodic recovery loop
4. Periodic cleanup loop

```
Publish() → Channel → Background Workers
        ↘
         Persist() → Storage
              ↘
         Recovery → Channel
```

## Performance Benchmarks

Latest request benchmark excerpt (.NET 9.0.11, Intel i7-13620H, single run):

| Method                       | Mean (ns) | Allocated (B) | Approx Throughput (ops/sec) |
|-----------------------------|----------:|--------------:|----------------------------:|
| SimpleRequestResponse        | 94.66     | 672           | ~10.56 M                    |
| ComplexRequestResponse       | 120.36    | 688           | ~8.31 M                     |
| SimpleRequestWithoutResponse | 98.39     | 240           | ~10.16 M                    |
| RequestWith1000Iterations*   | 103.45    | 644           | ~9.66 M (avg/op)            |

*Batch of 1000 requests total ≈103,448 ns (≈103.45 ns each).

Latest notification benchmark excerpt (.NET 9.0.11, Intel i7-13620H, single run):

| Method                     | Mean (ms) | Allocated (B) |
|---------------------------|----------:|--------------:|
| PublishSimpleNotification  | 15.85     | 950           |
| PublishComplexNotification | 16.00     | 936           |
| Publish100Notifications    | 56.09     | 27,261        |
| Publish1000Notifications   | 105.54    | 270,549       |

Notes:
- Notification benchmarks include enqueue + background processing overhead.
- Allocation scales primarily with handler count and batch size.
- Single notification latency dominated by worker scheduling (intentionally decoupled).

### Highlights
- Precompiled delegates
- Immutable work item struct
- Pooled task arrays for multi-handler fan-out
- Fast-path synchronous completions
- Exponential retry with precomputed delays

> Numbers from a single run; re-run on your hardware for authoritative results.

Run benchmarks:
```bash
cd benchmarks
DOTNET_TieredPGO=1 DOTNET_ReadyToRun=0 dotnet run -c Release --filter *Request*
DOTNET_TieredPGO=1 DOTNET_ReadyToRun=0 dotnet run -c Release --filter *Publish*
```

## Testing

```bash
dotnet test
```

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/amazing-feature`
3. Add changes + tests
4. Run benchmarks
5. Commit: `git commit -m 'Add amazing feature'`
6. Push: `git push origin feature/amazing-feature`
7. Open PR

## License

MIT License - see [LICENSE](LICENSE).

## Support

Open issues for bugs or features. Provide clear reproduction steps.

## Appreciation (Optional)

Free forever. If it helps you and you want to buy a coffee:

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/swartbergstudio)

**Always free. No premium features, no paid support.****Always free. No premium features, no paid support.**