#!/bin/bash

# Local build and test script that mimics CI pipeline
set -e

echo "???  Building SwartBerg.Mediator locally..."

# Clean previous builds
echo "?? Cleaning previous builds..."
dotnet clean --configuration Release

# Restore dependencies
echo "?? Restoring dependencies..."
dotnet restore

# Build solution
echo "?? Building solution..."
dotnet build --no-restore --configuration Release

# Run tests
echo "?? Running tests..."
dotnet test --no-build --configuration Release --verbosity normal

# Pack NuGet package (local)
echo "?? Creating NuGet package..."
dotnet pack src/Mediator.csproj --no-build --configuration Release --output ./local-packages

# Run benchmarks
echo "? Running benchmarks..."
cd benchmarks
dotnet run --configuration Release --framework net9.0 -- --job short --memory
cd ..

echo "? Local build completed successfully!"
echo "?? NuGet package created in ./local-packages/"

# List generated packages
echo "Generated packages:"
ls -la ./local-packages/