# Changelog

All notable changes to SwartBerg.Mediator and SwartBerg.Mediator.SourceGenerator. Versions follow [Semantic Versioning](https://semver.org/); release dates and packages are on the [Releases](https://github.com/SwartBergStudio/Mediator/releases) page.

## 3.1.2

### Fixed
- **Background notification handlers inherited the ExecutionContext of the first request that published.** The publisher's worker, recovery and cleanup loops are started by the first `Publish` call and captured that caller's `AsyncLocal` state for the life of the process. In ASP.NET Core this meant every background handler could see that request's `HttpContext` (through `IHttpContextAccessor`), its logger scopes and `Activity.Current`, whichever request was publishing. The loops now start with a clean context. Handler spans are still linked to the span that published them. Present since before 3.0.

## 3.1.1

### Fixed
- **The package README on nuget.org showed the logo oversized and out of place.** The README now uses a plain Markdown image, which renders the same on GitHub and nuget.org. No code changes.

### Changed
- The source generator is built with `Microsoft.CodeAnalysis.Analyzers` 5.9.0, a build-time code-quality check. It still targets the .NET 8 SDK compiler and adds no dependencies for consumers.

## 3.1.0

### Added
- **Tracing and metrics.** Requests, commands, streams and notification handlers report OpenTelemetry-compatible spans and metrics through the `SwartBerg.Mediator` `ActivitySource` and `Meter` (see README → Observability).
  - Background handler spans are children of the span that published them.
  - Nothing is recorded, and there is no overhead, unless a listener is subscribed.
  - `MediatorDiagnostics` exposes the source and meter names.
- **Pipeline behaviors for requests without a response.** The new `IPipelineBehavior<TRequest>` (with `RequestHandlerDelegate`) wraps `IRequest` handlers such as commands, in registration order. Previously, behaviors only ran for `IRequest<TResponse>`, so a validation behavior silently skipped these requests.
  - Open generic versions can be listed in `[assembly: MediatorPipelineBehaviors(...)]` next to the other behaviors, and work under Native AOT.
- **`IMediator.PublishAndWait(notification)`.** Runs a notification's handlers now, one after another, in the caller's DI scope, and completes when they have finished. The first exception reaches the caller.
  - Use it for work that must succeed or fail with the caller, such as domain events in the same `DbContext` or transaction. `Publish` keeps its background behavior.
- **Build-time handler checks** (in the source generator package):
  - `MEDGEN005` warns when a request declared in the project has no handler.
  - `MEDGEN006` warns when a request has several handlers, which would make all but the last one dead code.

  Both can be suppressed per type with `#pragma` or per project with `.editorconfig` / `NoWarn`.
- **Native AOT and trimming support.** The new `SwartBerg.Mediator.SourceGenerator` package generates `AddMediatorHandlers()` per project. It registers every handler and pre-creates the typed dispatchers at compile time, so no reflection runs at runtime.
  - The library itself is now marked AOT-compatible (`IsAotCompatible`).
  - It reports diagnostics `MEDGEN001`–`MEDGEN004`, described in the README.
- **`[assembly: MediatorPipelineBehaviors(...)]`.** Declares open generic request and stream behaviors once per project. They are registered closed per request, honouring generic constraints and the declared order.
  - This works under Native AOT, including requests with value-type responses (`IRequest<int>`, `IRequest<Guid>`). Registering with `AddTransient(typeof(IPipelineBehavior<,>), ...)` fails for those because the DI container can't close them without dynamic code.
  - `AddMediator(assemblies)` honours the same attribute.
- **`INotificationRetryPersistence` (optional).** Lets a store save a retry item together with its retry time in one step, so it is never briefly visible as ready. `FileNotificationPersistence` implements it. Stores shared by several app instances should implement it too.
- **Persistence samples for Redis and EF Core** (`samples/`, not packages). Both are safe for several app instances: leases, atomic claims and atomic retry scheduling. Shared contract tests run in CI against SQLite and a real Redis server.
- **`JsonNotificationSerializer(JsonSerializerOptions)`.** Accepts a source-generated `JsonSerializerContext`, so persistence works under Native AOT.
- **`AddMediatorCore()` and `MediatorRegistry`.** These are the reflection-free registration entry points used by the generator.
- **Package icon** for both packages.

### Fixed
- **A persisted notification could be handled twice.** Two rare timing windows let the recovery loop queue a notification that was already being handled, or had just finished:
  - between storing a new notification and marking it as queued;
  - when the recovery loop worked from a snapshot taken just before the item finished.

  Both were present since 3.0.0. A deterministic test now covers the second one.
- **A crash while writing a notification file could lose the notification.** File persistence now writes to a temporary file and renames it, so a half-written file is never read and deleted as corrupt.

### Changed
- **Only failed handlers are retried.** With persistence, when some of a notification's handlers fail, each failed handler is retried on its own. Handlers that already succeeded no longer run again. The retry entry records its handler in the new `NotificationWorkItem.TargetHandlerType`; file persistence stores it in the `workItem` object, and only when it is set.
- **Faster dispatch with fewer allocations.** Creating the mediator per DI scope and sending a request are both cheaper; see README → Benchmarks.
- **`IMediator` and the request, command and stream dispatchers are now transient** instead of scoped. They hold no state, and handlers still resolve from the caller's scope.
- **Requests, commands and streams are dispatched directly.** The mediator returns the handler pipeline's task without wrapping it.
- **File persistence no longer starves ready notifications.** `GetPendingAsync` skips files that are waiting for a retry instead of letting them fill the batch.
- **AOT-safe JSON without a format change.** File persistence and `JsonNotificationSerializer` write JSON with `Utf8JsonWriter`. The on-disk and payload formats are byte-for-byte unchanged, and files written by 3.0 are still read.

### Upgrade notes (behavior changes)
- **Custom persistence should store `NotificationWorkItem.TargetHandlerType`.** Implementations that don't store it keep working: their retries re-run all of the notification's handlers, as before.
- **Custom `IMediator` implementations still compile.** `PublishAndWait` is a default interface method; on a custom implementation that doesn't override it, it throws `NotSupportedException`.
- **No more "Request/Command … failed" log entries.** Handler exceptions still reach your code unchanged; your host (ASP.NET Core, Blazor) logs them as before. Failures in background notification handlers are still logged by the mediator.
- **`IMediator` no longer routes through `IRequestDispatcher` / `ICommandDispatcher` / `IStreamRequestDispatcher`.** Those services still work when injected directly. Replacing them in DI only affected `IMediator` if you deliberately decorated them to intercept every call.
- **Transient `IMediator`.** Resolving it from the root provider, for example in a singleton, is no longer rejected by scope validation. Handlers resolved that way come from the root provider, so prefer resolving the mediator inside a scope.
- **Upgrading to the source generator:**
  1. Add the generator package next to `SwartBerg.Mediator`.
  2. Replace `AddMediator(assemblies)` with `AddMediatorCore(options => ...)` + `AddMediatorHandlers()`.
  3. Move open generic behaviors into `[assembly: MediatorPipelineBehaviors(...)]`, and remove their `AddTransient(typeof(IPipelineBehavior<,>), ...)` lines so they don't run twice.

## 3.0.0

### Fixed
- **Persisted notifications were handled twice.** Each one ran in memory and again when the recovery loop replayed the file. A notification is now removed once all its handlers succeed. A failing handler schedules a retry with exponential backoff up to `MaxRetryAttempts`; after that the notification is dropped and a warning is logged.
- **Open generic handlers or behaviors in a scanned assembly crashed `BuildServiceProvider`.** They are now skipped and must be registered explicitly.
- **Retry times were parsed in local time.** On servers not running in UTC, retries were delayed by the UTC offset.
- **One unloadable type stopped assembly scanning.** Scanning now registers the types that do load.

### Changed
- **Dispatch uses strongly-typed handler wrappers cached per message type.** These replace the per-scope reflection caches and `Task<object>` boxing. Requests with behaviors resolve them once instead of twice.
- **Removed `Microsoft.SourceLink.GitHub`.** It had a known vulnerability, and SourceLink is now built into the .NET 8+ SDK.
- **Releases are versioned from git tags and published with NuGet trusted publishing.** CI builds with the .NET 10 SDK; the library still targets .NET 8, 9 and 10.
