# Changelog

All notable changes to SwartBerg.Mediator and SwartBerg.Mediator.SourceGenerator. Versions follow [Semantic Versioning](https://semver.org/); release dates and packages are on the [Releases](https://github.com/SwartBergStudio/Mediator/releases) page.

## Unreleased

### Added
- **Closed behaviors can be listed in `[assembly: MediatorPipelineBehaviors(...)]`.** Previously only open generic behaviors could be listed. Now a behavior written for one request, such as `GetUserCachingBehavior : IPipelineBehavior<GetUserQuery, User>`, can be listed too, mixed with open ones. Every listed behavior is registered in the listed order, by the source generator and by `AddMediator(assemblies)` alike, so the attribute describes the whole pipeline.
- **`MEDGEN007` (Info).** Reported by the source generator for a closed stream behavior that is registered only because scanning found it. See the deprecation below.

### Deprecated
- **Automatic registration of closed stream behaviors.** Scanning (and the source generator) registered every closed `IStreamPipelineBehavior<,>` it found, in no defined order: alphabetically in generated code, and in the assembly's type order with `AddMediator(assemblies)`. Request behaviors were never registered this way. Nothing changes at runtime in this version: unlisted closed stream behaviors are still registered as before. List them in `[assembly: MediatorPipelineBehaviors(...)]` (or register them yourself) to give them a defined position. A listed one is registered only once, where it is listed. This automatic registration will be removed in the next major version, so that behaviors follow one rule: declared, in order. `MEDGEN007` is Info, not Warning, so builds with `TreatWarningsAsErrors` keep building.

### Changed
- **`ICommandDispatcher.Send<TRequest>` falls back to the command's actual type.** It looked up the handler for the declared type `TRequest`, so a command held as an interface or base type (`ICommand cmd = new ShipOrder(); dispatcher.Send(cmd)`) failed with "Handler not found", while `IMediator.Send` used the actual type and succeeded. When `TRequest` has no handler, the dispatcher now uses the handler (and behaviors) of the actual type. Calls that worked before resolve exactly as before, and the check only runs when no handler was found, so the normal path costs nothing extra. `IMediator.Send` is unchanged.
- **"Handler not found" names the request.** The message read `Handler not found: IRequestHandler`1`. It now reads, for example, `Handler not found: IRequestHandler<ShipOrder>`.
- **`FileNotificationPersistence.FailAsync` keeps the notification when the file can't be accessed.** If recording a retry failed with an IO error (for example a virus scanner or backup holding the file), the notification was deleted and the call reported success, so it never ran again. The file is now kept and the `IOException` (or `UnauthorizedAccessException`) is thrown. The mediator logs it, and the next recovery pass runs the notification again. A record that is no longer readable is still removed.

### Fixed
- **A retried notification could reach its handler with every property blank.** When a notification could not be serialized (for example an object cycle), `JsonNotificationSerializer` returned `"{}"`. That was persisted, and a retry or crash recovery deserialized it as a notification with default values, so the handler ran again on blank data. `Serialize` now throws; the mediator logs the error and handles that notification in memory only, without persistence or retries. Present since before 3.0.
- **A retry delay past the year 9999 made failed handlers run again and again.** The retry time overflowed `DateTime` and threw, so the retry was never stored: the item stayed ready, and every recovery pass ran all of its handlers again, with no backoff and no attempt limit. With the default 1 minute delay doubling each time, this happened from the 33rd attempt (`MaxRetryAttempts` of 33 or more). Retry times now cap at `DateTime.MaxValue`. A very large `MaxRetryAttempts`, such as `int.MaxValue`, also threw `OutOfMemoryException` when the publisher was created; delays are now calculated per attempt.
- **One malformed file stopped all file-based recovery.** A file in the persistence folder that was valid JSON of the wrong shape (for example a `null` date) made `FileNotificationPersistence.GetPendingAsync` throw, so no other notification was recovered on any pass. Such files are now skipped, and cleanup removes them once they are older than the retention period.
- **`Send<TCommand>(null)` passed `null` to the handler.** The generic command overload now returns a faulted task with `ArgumentNullException`, like the other `Send` overloads.
- **A crash while recording a retry could lose the notification.** `FileNotificationPersistence.FailAsync` rewrote the file in place, and an interrupted write left a broken file that the next scan deleted. It now writes a temporary file and renames it, as new items already did. (Fixed by reading the code; a crash at that point is not reproduced in the tests.)
- **Temporary files from interrupted writes were never removed.** Cleanup only looked at `*.json`, so a `*.json.tmp` left by a process that stopped mid-write stayed in the folder forever. Cleanup now removes them after the retention period too.

### Performance
- **`Send` and `CreateStream` without behaviors allocate 40 bytes less per call.** Each typed dispatcher declared its pipeline lambdas in the same method that calls the handler, and C# allocates the closure for captured variables on entry to that method, so every call paid for the pipeline even when there were no behaviors. The pipeline is now built in a separate method, only when behaviors exist. Measured: `Send` 64 → 24 bytes (the 24 left are the transient handler itself), a new DI scope plus `Send` 232 → 192 bytes, and dispatch itself allocates nothing with a singleton handler. A test now fails if this allocation returns.
- **Background notifications allocate 24 bytes less each.** The same pattern in the publisher: a lambda used only for handler retries was allocated for every notification. Measured 232 → 208 bytes per notification.
- **`Publish` no longer waits for a file recovery scan.** `FileNotificationPersistence` held its lock while reading every waiting file, and `Publish` persists through the same lock. Measured with 5,000 pending retries, the worst `Publish` during a scan went from 459 ms (42 s on a cold first scan) to under 35 ms. The lock is now taken for one file at a time, and the scan takes as long as before.

### Samples
- The Redis persistence sample threw on a record it could not parse, so the rest of its claimed batch was not returned. It now drops that record, as its comments said.

### Tests
- `SingleNotification_EndToEndLatency` failed whenever the machine was busy, because it asserted one wall-clock sample under 100 ms. It now asserts the fastest of five samples, which still fails if notifications wait for a polling interval.

### Build
- `local-build.sh` and `local-build.cmd` now give local builds the version `999.0.0-local`. The pack step failed because a `0.0.0-dev` build is older than the API validation baseline (`CP0003`).

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

## 2.0.11

### Changed (behavior change, missing from earlier release notes)
- **`Send` no longer creates its own DI scope.** Requests and commands resolve their handler and pipeline behaviors from the caller's `IServiceProvider`, so scoped services (for example the current user in Blazor Server) keep their state. Before 2.0.11, every `Send` ran in a fresh scope.

### Upgrade notes
- **Handlers now share scoped services with the code that calls `Send`, including the `DbContext`.** Code that relied on each request getting a fresh `DbContext` can change behavior: entities the caller already loaded are returned from the change tracker, and a caller that saves after the handler works on the same tracked instances.
- **Register one `DbContext` instance per scope.** `services.AddScoped<IAppDbContext, AppDbContext>()` next to `AddDbContext<AppDbContext>()` creates two contexts per scope. While each `Send` had its own scope this rarely mattered; with a shared scope, a copy loaded through one context before the handler saved through the other is stale, and saving it overwrites the handler's changes. Forward the interface instead: `services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>())`.

