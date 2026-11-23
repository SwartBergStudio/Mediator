using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Mediator.AzureServiceBus.Tests.Emulator;

/// <summary>Writes the hosts' warnings and errors to the test output, so a failure shows what the processor saw.</summary>
internal sealed class TestOutputLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(output, categoryName);

    public void Dispose() { }

    private sealed class Logger(ITestOutputHelper output, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : Environment.NewLine + exception)}");
            }
            catch (InvalidOperationException)
            {
                // Safe to drop: the test has already finished (xunit rejects output after that), so nobody reads it.
            }
        }
    }
}
