@echo off
REM Local build and test script that mimics CI pipeline

echo ???  Building SwartBerg.Mediator locally...

REM Clean previous builds
echo ?? Cleaning previous builds...
dotnet clean --configuration Release

REM Restore dependencies
echo ?? Restoring dependencies...
dotnet restore

REM Build solution
echo ?? Building solution...
dotnet build --no-restore --configuration Release
if %ERRORLEVEL% neq 0 goto :error

REM Run tests
echo ?? Running tests...
dotnet test --no-build --configuration Release --verbosity normal
if %ERRORLEVEL% neq 0 goto :error

REM Pack NuGet package (local)
echo ?? Creating NuGet package...
dotnet pack src/Mediator.csproj --no-build --configuration Release --output ./local-packages
if %ERRORLEVEL% neq 0 goto :error

REM Run benchmarks
echo ? Running benchmarks...
cd benchmarks
dotnet run --configuration Release --framework net9.0 -- --job short --memory
cd ..
if %ERRORLEVEL% neq 0 goto :error

echo ? Local build completed successfully!
echo ?? NuGet package created in ./local-packages/

REM List generated packages
echo Generated packages:
dir /B local-packages\

goto :end

:error
echo ? Build failed with error level %ERRORLEVEL%
exit /b %ERRORLEVEL%

:end