# Native AOT roadmap

Goal: ship two flavours of SwartBerg.Mediator without breaking existing consumers.

| Flavour | Package | Handler discovery | Dispatch |
|---|---|---|---|
| Reflection (today) | `SwartBerg.Mediator` | `AddMediator(assemblies)` – assembly scanning | Typed wrappers created lazily via `MakeGenericType` |
| AOT | `SwartBerg.Mediator` + `SwartBerg.Mediator.SourceGenerator` | Generated `AddMediatorHandlers()` | Typed wrappers pre-registered by generated code – no reflection |

Both flavours share one runtime. The only difference is who creates the strongly typed handler wrappers.

## Phase 1 – runtime foundation and bug fixes (done in this branch)

- Replaced the reflection-based invokers (`MakeGenericMethod` + `object` boxing + `Task<object>`) with strongly
  typed wrappers (`Core/Wrappers`). These are cached process-wide instead of per DI scope.
- Added `MediatorRegistry.Register*` and `AddMediatorCore()`. These are the AOT-safe entry points the generator will call.
- When dynamic code is unavailable and a wrapper wasn't registered, the mediator throws a clear error instead of failing inside reflection.

## Phase 2 – source generator (done)

New project `src/Mediator.SourceGenerator` (netstandard2.0, `IIncrementalGenerator`, Roslyn 4.8 so the .NET 8 SDK works). It is shipped as a separate NuGet package, so existing users are unaffected.

1. Find non-abstract, non-open-generic classes implementing `IRequestHandler<,>`, `IRequestHandler<>`,
   `INotificationHandler<>`, `IStreamRequestHandler<,>` or `IStreamPipelineBehavior<,>`. This mirrors the reflection scan exactly.
2. Emit `internal static IServiceCollection AddMediatorHandlers(this IServiceCollection services)` in the project's
   root namespace. Using the root namespace avoids clashes between projects and with `InternalsVisibleTo`. The generated method:
   - calls `services.AddMediatorCore()`;
   - calls `MediatorRegistry.AddHandlers(services, new[] { ServiceDescriptor.Transient(typeof(IRequestHandler<A,B>), typeof(AHandler)), ... })`.
     This uses the same de-duplication as scanning;
   - calls `MediatorRegistry.RegisterRequest<A,B>()` (and the other `Register*` methods) for every message type.
3. Diagnostics: warn on handlers that generated code can't access (private nested types) and on open generic
   handlers that need manual registration.
4. Tests: a generator snapshot test, plus an equivalence test checking that the generated and reflection registrations produce the same descriptors.
5. `samples/Mediator.AotSample` with `PublishAot=true`, published in CI with warnings as errors. It runs send, command, publish and stream.

## Phase 3 – trimming/AOT annotations (done, except the 3.x item at the end)

- Set `<IsAotCompatible>true</IsAotCompatible>` on the library for net8.0+ and fix every analyzer warning.
- `JsonNotificationSerializer`: add a `JsonSerializerOptions` constructor that accepts a source-generated
  `JsonSerializerContext`. Serialize the type wrapper with `Utf8JsonWriter` instead of reflection.
- `FileNotificationPersistence`: write files with `Utf8JsonWriter` instead of anonymous types, using the same on-disk format.
  Resolve notification types through the generator's registry before falling back to `Type.GetType`.
- Future major version: annotate `AddMediator(params Assembly[])` with `[RequiresUnreferencedCode]`. This is still
  deferred because it can break builds of consumers that have trim analysis and warnings-as-errors turned on
  (published MAUI and Blazor WebAssembly apps).

## Open generic behaviors under Native AOT

Microsoft.Extensions.DependencyInjection refuses to close open generic services over value types when dynamic code is
unavailable. So `AddTransient(typeof(IPipelineBehavior<,>), ...)` throws under Native AOT for requests such as
`IRequest<int>` or `IRequest<Guid>`. The fix is `[assembly: MediatorPipelineBehaviors(...)]`: the generator closes each
declared behavior per request at compile time, checking its generic constraints and keeping the declared order.
`AddMediator(assemblies)` applies the same attribute via reflection, so both modes register identical behaviors.

## Status

- The library builds with `IsAotCompatible=true` and no suppressed warnings in the dispatch path.
- `samples/Mediator.AotSample` publishes with Native AOT with zero trim/AOT warnings (warnings as errors). It runs a
  request with a behavior, a value-type response, a command, a stream, a notification with two handlers, and persistence.
  CI and the Release workflow publish and run it.
- The generated registrations are tested for equality with reflection scanning (`SourceGeneratedRegistrationTests`).
- The serializer and file persistence output is pinned byte-for-byte to the earlier format (`PersistenceFormatCompatibilityTests`).
- `src/tests-aot` runs the full test suite with dynamic code disabled, using only generated dispatchers, on .NET 8, 9 and 10.
- Package validation fails CI and releases on public API breaks compared with the last release.

## Bugs found and fixed in Phase 1

- **Persisted notifications were handled twice.** `Publish` persisted and queued the notification but never completed
  the persisted file, so the recovery loop replayed it. Items are now completed after successful handling. Handler
  failures schedule a retry with backoff (`MaxRetryAttempts`), and the recovery loop skips items still queued in this process.
- **Open generic handlers or behaviours in a scanned assembly crashed `BuildServiceProvider`.** They are now skipped and must be registered manually, as already documented for pipeline behaviours.
- **`retryAfter` was parsed with `DateTime.Parse`**, which converted it to local time. Retries were delayed by the server's UTC offset.
- **Assembly scanning failed completely on `ReflectionTypeLoadException`.** Scanning now registers the types that did load.
- **Build broke on NuGet audit (NU1902)** because of `Microsoft.SourceLink.GitHub` 8.0.0. SourceLink is built into the .NET 8+ SDK, so the package was removed.

## Performance fixes in Phase 1

- Dispatchers are scoped, and each one allocated 3–6 `ConcurrentDictionary` caches, so every DI scope (each HTTP
  request) started cold. The caches are now static and shared.
- Requests with behaviours resolved the behaviours twice: once to check whether any existed and again to run them. They are now resolved once.
- No more `Task<object>` boxing and re-wrapping per call and per behaviour. Value-type responses no longer box.
- Streams no longer pass through two extra box/unbox async iterators per item (and one more per behaviour).
- `InternalHelpers.AwaitConfigurable` added an extra async state machine per await. It was replaced by `ConfigureAwait(bool)`.
- Dead code removed: the root-provider handler cache and the `MaterializeTypes` path.
