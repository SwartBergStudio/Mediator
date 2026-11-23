using DotNet.Testcontainers.Builders;
using Testcontainers.ServiceBus;

namespace Mediator.AzureServiceBus.Tests.Emulator;

/// <summary>
/// Starts the Azure Service Bus emulator (and the SQL Server it needs) in Docker once for all emulator tests, with the
/// entities from <c>emulator-config.json</c>.
/// </summary>
/// <remarks>
/// Without Docker the tests are skipped, unless <c>REQUIRE_CONTAINER_TESTS=true</c> (set in CI), where a missing Docker
/// fails them: a CI run must never pass without having run them.
/// </remarks>
public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    public const string EmulatorImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1";

    private ServiceBusContainer? _container;
    private string? _unavailable;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ServiceBusBuilder(EmulatorImage)
                .WithAcceptLicenseAgreement(true)
                .WithConfig(Path.Combine(AppContext.BaseDirectory, "Emulator", "emulator-config.json"))
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (DockerUnavailableException ex)
        {
            // Not swallowed: every test calls RequireEmulator, which skips or (in CI) fails with this reason.
            _unavailable = ex.Message;
        }
    }

    /// <summary>Skips the test when Docker is not available, or fails it when container tests are required.</summary>
    public void RequireEmulator()
    {
        if (_unavailable == null) return;
        if (string.Equals(Environment.GetEnvironmentVariable("REQUIRE_CONTAINER_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"REQUIRE_CONTAINER_TESTS is set but Docker is not available: {_unavailable}");
        Skip.If(true, $"Docker is not available: {_unavailable}");
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
{
    public const string Name = "Service Bus emulator";
}
