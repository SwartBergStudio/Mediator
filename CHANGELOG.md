# Changelog

All notable changes to SwartBerg.Mediator and SwartBerg.Mediator.SourceGenerator. Versions follow [Semantic Versioning](https://semver.org/); release dates and packages are on the [Releases](https://github.com/SwartBergStudio/Mediator/releases) page.

## 3.1.0 (unreleased)

### Added
- **Native AOT and trimming support.** The new `SwartBerg.Mediator.SourceGenerator` package generates `AddMediatorHandlers()` per project. It registers every handler and pre-creates the typed dispatchers at compile time, so no reflection runs at runtime.
  - The library itself is now marked AOT-compatible (`IsAotCompatible`).
  - It reports diagnostics `MEDGEN001`–`MEDGEN004`, described in the README.
- **`[assembly: MediatorPipelineBehaviors(...)]`.** Declares open generic request and stream behaviors once per project. They are registered closed per request, honouring generic constraints and the declared order.
  - This works under Native AOT, including requests with value-type responses (`IRequest<int>`, `IRequest<Guid>`). Registering with `AddTransient(typeof(IPipelineBehavior<,>), ...)` fails for those because the DI container can't close them without dynamic code.
  - `AddMediator(assemblies)` honours the same attribute.
- **`JsonNotificationSerializer(JsonSerializerOptions)`.** Accepts a source-generated `JsonSerializerContext`, so persistence works under Native AOT.
- **`AddMediatorCore()` and `MediatorRegistry`.** These are the reflection-free registration entry points used by the generator.
- **Package icon** for both packages.

### Changed
- **Faster dispatch.** Against MediatR 12.4.1, the library is ahead in every scenario and allocates less in all of them (see README → Benchmarks).
- **`IMediator` and the request, command and stream dispatchers are now transient** instead of scoped. They hold no state, and handlers still resolve from the caller's scope.
- **Requests, commands and streams are dispatched directly.** The mediator returns the handler pipeline's task without wrapping it.
- **File persistence no longer starves ready notifications.** `GetPendingAsync` skips files that are waiting for a retry instead of letting them fill the batch.
- **AOT-safe JSON without a format change.** File persistence and `JsonNotificationSerializer` write JSON with `Utf8JsonWriter`. The on-disk and payload formats are byte-for-byte unchanged, and files written by 3.0 are still read.

### Upgrade notes (behavior changes)
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
