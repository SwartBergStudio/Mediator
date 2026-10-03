# Consumer compatibility check

`Net8Consumer` is a .NET 8 app built with the **.NET 8 SDK** (pinned by `global.json`) against the packages packed from
the current commit. It guards two things CI on the latest SDK cannot see:

- **The source generator still loads on older compilers.** A generator built against a newer Roslyn than the .NET 8 SDK
  ships is silently skipped there, so `AddMediatorHandlers()` would not exist.
- **The library doesn't force newer `Microsoft.Extensions.*` packages.** The app references the 8.0 versions directly
  with warnings as errors, so a library dependency on 9.x or 10.x fails with NU1605 (package downgrade).

Run locally (needs the .NET 8 SDK):

```bash
dotnet pack src/Mediator/Mediator.csproj -c Release -o packages-ci -p:Version=999.0.0-ci
dotnet pack src/Mediator.SourceGenerator/Mediator.SourceGenerator.csproj -c Release -o packages-ci -p:Version=999.0.0-ci
cd tests-compat && dotnet run --project Net8Consumer
```
