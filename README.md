# SwartBerg.Mediator

[![Build Status](https://github.com/SwartBergStudio/Mediator/workflows/CI/badge.svg)](https://github.com/SwartBergStudio/Mediator/actions/workflows/ci.yml)
[![Release](https://github.com/SwartBergStudio/Mediator/workflows/Release/badge.svg)](https://github.com/SwartBergStudio/Mediator/actions/workflows/release.yml)
[![NuGet Version](https://img.shields.io/nuget/v/SwartBerg.Mediator.svg)](https://www.nuget.org/packages/SwartBerg.Mediator/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SwartBerg.Mediator.svg)](https://www.nuget.org/packages/SwartBerg.Mediator/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A fast mediator implementation for .NET 9 with background processing and notification persistence.

Inspired by MediatR, this library was created as a free alternative with similar patterns but optimized for performance and includes built-in persistence and background processing.

## Features

- **High Performance**: Uses expression trees and caching to avoid reflection overhead
- **Background Processing**: Handles notifications in the background without blocking your app
- **Pipeline Behaviors**: Add logging, validation, and other cross-cutting concerns easily
- **Configurable Persistence**: File-based persistence with JSON serialization (can be replaced)
- **Retry Logic**: Automatically retries failed notifications with exponential backoff
- **Lightweight**: Minimal dependencies, optimized for performance
- **.NET 9 Ready**: Takes advantage of .NET 9 performance improvements

## Requirements

- .NET 9.0 or later
- Works with:
  - .NET 9+ applications
  - .NET MAUI applications
  - Blazor applications
  - ASP.NET Core 9+ applications
  - Console applications

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

Add cross-cutting concerns like validation, logging, or caching:

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

By default, the mediator uses file-based persistence for crash recovery. The channel handles processing for high throughput.

Replace with custom persistence:

```csharp
services.AddSingleton<INotificationPersistence, RedisNotificationPersistence>();

services.AddSingleton<INotificationPersistence, SqlServerNotificationPersistence>();

services.AddMediator(options => options.EnablePersistence = false, typeof(Program).Assembly);
```

Example Redis implementation:
```csharp
public class RedisNotificationPersistence : INotificationPersistence
{
    private readonly IDatabase _database;
    
    public RedisNotificationPersistence(IConnectionMultiplexer redis)
    {
        _database = redis.GetDatabase();
    }
    
    public async Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var persistedItem = new PersistedNotificationWorkItem
        {
            Id = id,
            WorkItem = workItem,
            CreatedAt = DateTime.UtcNow,
            AttemptCount = 0
        };
        
        var key = $"mediator:notifications:{id}";
        var value = JsonSerializer.Serialize(persistedItem);
        await _database.StringSetAsync(key, value);
        
        return id;
    }
    
    // implement other interface methods
}
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
}, typeof(Program).Assembly);
```

## Architecture

The mediator uses a channel-first approach with optional persistence backup:

1. **Primary Processing**: In-memory channels for fast, reliable processing
2. **Persistence**: Optional backup that saves notifications to disk/storage
3. **Recovery**: On startup/timer, recovers persisted notifications back into the channel
4. **Cleanup**: Removes old persisted items periodically

### Flow:
```
Publish() → Channel (immediate) → Background Workers
     ↓
Persist() (async backup) → Storage
     ↓
Recovery Timer → Load from Storage → Back to Channel
```

## Performance Benchmarks

BenchmarkDotNet results on .NET 9 (Intel Core i7-13620H):

### Request Processing
| Method | Mean | Error | StdDev | Allocated | Throughput |
|--------|------|-------|---------|-----------|------------|
| SingleRequest | 98.05 ns | 1.94 ns | 1.81 ns | 672 B | ~10.2M req/sec |
| BatchRequests100 | 9.70 μs | 0.17 μs | 0.16 μs | 64 KB | ~103K batches/sec |

### Notification Processing  
| Method | Mean | Error | StdDev | Allocated | Throughput |
|--------|------|-------|---------|-----------|------------|
| SingleNotification | 348.2 ns | 3.45 ns | 3.23 ns | 717 B | ~2.9M notifs/sec |
| BatchNotifications100 | 40.18 μs | 0.48 μs | 0.40 μs | 80 KB | ~24.9K batches/sec |

### Performance Highlights
- **Blazing requests**: 98ns per request - one of the fastest mediators available
- **Ultra-fast notifications**: 348ns with background processing
- **Outstanding throughput**: 10.2 million requests per second capability
- **Efficient batch processing**: 100 requests in 9.7μs  
- **Low memory usage**: Optimized allocations with compiled delegates
- **Pipeline behavior support**: Full hot path optimization for behaviors too
- **Enterprise-grade performance**: Perfect for hyperscale production systems

Run benchmarks:
```bash
cd benchmarks
dotnet run -c Release
```

## Testing

```bash
dotnet test

cd benchmarks
dotnet run -c Release
```

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/amazing-feature`
3. Make your changes and add tests
4. Run benchmarks to ensure performance
5. Commit your changes: `git commit -m 'Add amazing feature'`
6. Push to the branch: `git push origin feature/amazing-feature`
7. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Support

- Create an issue for bug reports or feature requests
- Check existing issues before creating new ones
- Provide clear reproduction steps for bugs

## Appreciation (Optional)

SwartBerg.Mediator is completely free and always will be. Use it, modify it, distribute it - no strings attached! 

If this library happens to save you time or makes your project better, and you feel like buying me a coffee out of the goodness of your heart, that's awesome but totally optional:

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/swartbergstudio)

**Remember: This library will always be free, regardless of donations. No premium features, no paid support, no strings attached.**

## Contributors

Thanks to all the developers who contribute to making SwartBerg.Mediator better!

