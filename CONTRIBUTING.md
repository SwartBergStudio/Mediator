# Contributing to SwartBerg.Mediator

Thanks for helping. Bug reports, fixes, docs and features are all welcome.

## Before you start

- **Bugs:** open an issue with the bug report template. A minimal reproduction makes a fix much faster.
- **Features:** open an issue first so we can agree on the approach before you spend time on code.
- **Security problems:** don't open a public issue. See [SECURITY.md](SECURITY.md).

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). To run the tests on every supported framework, also install the .NET 8 and .NET 9 runtimes.

```bash
dotnet build -c Release
dotnet test -c Release
```

The solution contains:

| Project | Purpose |
|---|---|
| `src/Mediator` | The library (`SwartBerg.Mediator`) |
| `src/Mediator.SourceGenerator` | Source generator and analyzer (`SwartBerg.Mediator.SourceGenerator`) |
| `src/tests` | Library tests (.NET 8, 9 and 10) |
| `src/tests-aot` | The same tests with dynamic code disabled and generated dispatchers only (Native AOT semantics) |
| `src/generator-tests` | Generator and analyzer tests |
| `samples/Mediator.AotSample` | Native AOT end-to-end check |
| `src/benchmarks` | BenchmarkDotNet benchmarks |

Native AOT check (Linux needs `clang` and `zlib1g-dev`):

```bash
dotnet publish samples/Mediator.AotSample -c Release -r linux-x64 -o ./aot-sample
./aot-sample/Mediator.AotSample
```

Benchmarks:

```bash
cd src/benchmarks
dotnet run -c Release -- --filter *DispatchBenchmarks*
```

## Pull requests

1. **Branch from `main` with a descriptive name.** For example `feature/awaited-publish` or `fix/release-version-stamping`.
2. **Add tests.** For a bug fix, add a test that fails without the fix.
3. **Keep the public API compatible.** CI packs the library with package validation against the latest release and fails on breaking changes. Breaking changes need an issue and a major version.
4. **Keep it Native AOT compatible.** The library builds with `IsAotCompatible`, and the AOT sample builds with warnings as errors.
5. **Update the docs.** Update `README.md` for user-facing changes, and add an entry under the unreleased section of `CHANGELOG.md`.
6. **Pass CI.** CI runs the tests on .NET 8, 9 and 10, runs the Native AOT sample, reports coverage, and validates the API.

### Writing tests

- **Background work:** wait for the expected state (see `Eventually.WaitUntilAsync`) instead of sleeping a fixed time. Fixed sleeps fail randomly on busy machines.
- **Mocks:** prefer small hand-written fakes over mocking libraries. They also work in the AOT-mode test project, where proxy generation is unavailable.
- **Timing assertions:** warm up first, so the measurement excludes one-time JIT costs.

### Code style

- Follow the existing style. `.editorconfig` covers the basics.
- Comment *why*, not *what*. Public APIs need XML documentation; the build treats missing docs as errors.
- Hot paths (`Send`, `Publish`, `CreateStream`) shouldn't add allocations. Check with the benchmarks.

## Releases

Maintainers release from `main` with the **Release** workflow. The version comes from the latest `vX.Y.Z` tag plus the selected bump (patch, minor or major). Both NuGet packages are published with the same version through NuGet trusted publishing.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
