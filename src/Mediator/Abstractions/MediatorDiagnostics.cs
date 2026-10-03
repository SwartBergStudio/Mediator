namespace Mediator
{
    /// <summary>
    /// Names of the tracing source and meter the mediator reports to. Subscribe to them to collect traces and metrics,
    /// for example with OpenTelemetry: <c>.WithTracing(t =&gt; t.AddSource(MediatorDiagnostics.ActivitySourceName))</c> and
    /// <c>.WithMetrics(m =&gt; m.AddMeter(MediatorDiagnostics.MeterName))</c>.
    /// </summary>
    /// <remarks>
    /// When nothing is subscribed, the mediator skips all telemetry work, so there is no cost.
    /// </remarks>
    public static class MediatorDiagnostics
    {
        /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name: <c>SwartBerg.Mediator</c>.</summary>
        public const string ActivitySourceName = "SwartBerg.Mediator";

        /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> name: <c>SwartBerg.Mediator</c>.</summary>
        public const string MeterName = "SwartBerg.Mediator";
    }
}
