using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mediator.Core;

/// <summary>
/// Traces (<see cref="ActivitySource"/>) and metrics (<see cref="Meter"/>) for the mediator.
/// </summary>
/// <remarks>
/// Every entry point checks <see cref="IsEnabled"/> first, which is false unless a tracer or meter listener is
/// subscribed, so unobserved apps pay nothing. Tag and instrument names follow OpenTelemetry conventions.
/// </remarks>
internal static class MediatorTelemetry
{
    public const string MessageTypeTag = "mediator.message.type";
    public const string HandlerTypeTag = "mediator.handler.type";
    public const string PublishModeTag = "mediator.publish.mode";
    public const string ErrorTypeTag = "error.type";

    private static readonly string? s_version = typeof(MediatorTelemetry).Assembly.GetName().Version?.ToString();

    public static readonly ActivitySource Source = new(MediatorDiagnostics.ActivitySourceName, s_version);
    private static readonly Meter s_meter = new(MediatorDiagnostics.MeterName, s_version);

    private static readonly Histogram<double> s_requestDuration = s_meter.CreateHistogram<double>(
        "mediator.request.duration", "s", "Duration of requests, commands and stream enumerations, including behaviors.");

    private static readonly Counter<long> s_notificationsPublished = s_meter.CreateCounter<long>(
        "mediator.notification.published", "{notification}", "Notifications published, by mode (background or awaited).");

    private static readonly Histogram<double> s_handlerDuration = s_meter.CreateHistogram<double>(
        "mediator.notification.handler.duration", "s", "Duration of individual notification handlers; failures carry error.type.");

    private static readonly Counter<long> s_retriesScheduled = s_meter.CreateCounter<long>(
        "mediator.notification.retries", "{retry}", "Persisted notification retries scheduled after a handler failed.");

    private static readonly Counter<long> s_notificationsDropped = s_meter.CreateCounter<long>(
        "mediator.notification.dropped", "{notification}", "Persisted notifications given up after the maximum retry attempts.");

    private static readonly ConcurrentDictionary<Func<int>, byte> s_queueSizeSources = new();

    static MediatorTelemetry()
    {
        s_meter.CreateObservableGauge(
            "mediator.notification.queue.size",
            static () =>
            {
                var total = 0;
                foreach (var source in s_queueSizeSources.Keys) total += source();
                return total;
            },
            "{notification}",
            "Background notifications queued and not yet picked up by a worker.");
    }

    /// <summary>True when traces or request metrics are being collected.</summary>
    public static bool IsEnabled => Source.HasListeners() || s_requestDuration.Enabled || s_handlerDuration.Enabled;

    public static void RegisterQueue(Func<int> queueSize) => s_queueSizeSources.TryAdd(queueSize, 0);

    public static void UnregisterQueue(Func<int> queueSize) => s_queueSizeSources.TryRemove(queueSize, out _);

    public static Activity? StartActivity(string operation, Type messageType, ActivityContext parent = default, ActivityKind kind = ActivityKind.Internal)
    {
        if (!Source.HasListeners())
            return null;

        var activity = Source.StartActivity($"{operation} {messageType.Name}", kind, parent);
        activity?.SetTag(MessageTypeTag, messageType.FullName);
        return activity;
    }

    public static void RecordRequest(Type requestType, long startTimestamp, Exception? exception)
    {
        if (!s_requestDuration.Enabled) return;

        var tags = new TagList { { MessageTypeTag, requestType.FullName } };
        if (exception != null) tags.Add(ErrorTypeTag, exception.GetType().FullName);
        s_requestDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
    }

    public static void RecordPublished(Type notificationType, string mode)
    {
        if (!s_notificationsPublished.Enabled) return;
        s_notificationsPublished.Add(1, new TagList { { MessageTypeTag, notificationType.FullName }, { PublishModeTag, mode } });
    }

    public static void RecordHandler(Type notificationType, Type handlerType, long startTimestamp, Exception? exception)
    {
        if (!s_handlerDuration.Enabled) return;

        var tags = new TagList { { MessageTypeTag, notificationType.FullName }, { HandlerTypeTag, handlerType.FullName } };
        if (exception != null) tags.Add(ErrorTypeTag, exception.GetType().FullName);
        s_handlerDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
    }

    public static void RecordRetry(Type? notificationType)
    {
        if (s_retriesScheduled.Enabled)
            s_retriesScheduled.Add(1, new TagList { { MessageTypeTag, notificationType?.FullName } });
    }

    public static void RecordDropped(Type? notificationType)
    {
        if (s_notificationsDropped.Enabled)
            s_notificationsDropped.Add(1, new TagList { { MessageTypeTag, notificationType?.FullName } });
    }

    /// <summary>Marks the activity as failed with the exception details (OpenTelemetry semantic conventions).</summary>
    public static void SetError(Activity? activity, Exception exception)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(ErrorTypeTag, exception.GetType().FullName);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.ToString() },
        }));
    }
}
