using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// OTLP export to the self-hosted collector, running alongside the Application Insights
    /// pipeline in <see cref="Telemetry"/>. Both destinations receive the same events during the
    /// migration; neither knows about the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything here is sanitised on the way in.</b> <see cref="Telemetry"/> owns scrubbing
    /// and calls this class with values that have already been through <see cref="Sanitizer"/>.
    /// That ordering is deliberate and must not be inverted: OpenTelemetry's exception helpers
    /// (<c>Activity.RecordException</c>, the <c>ILogger</c> exception overloads) serialise
    /// <c>Exception.Message</c> and <c>StackTrace</c> verbatim, and TDPdf's exception text
    /// routinely contains the path of the document being worked on. This class therefore never
    /// touches an <see cref="Exception"/> object — it takes strings that are already clean.
    /// </para>
    /// <para>
    /// <b>Resource attributes are the contract.</b> Staging and production post to the same
    /// collector; the attributes are the only thing separating apps and environments.
    /// <c>service.name</c> is fixed at <c>tdpdf</c> and must never change — it is the key SigNoz
    /// groups on.
    /// </para>
    /// </remarks>
    internal static class OtlpTelemetry
    {
        /// <summary>Fixed for the life of the app. See the remarks — never change this.</summary>
        private const string ServiceName = "tdpdf";

        private const string ServiceNamespace = "stfoa";

        private static readonly object s_lock = new();
        private static TracerProvider? s_tracerProvider;
        private static ILoggerFactory? s_loggerFactory;
        private static ILogger? s_logger;
        private static ActivitySource? s_activitySource;

        public static bool IsEnabled { get; private set; }

        /// <summary>
        /// Best-effort start. A missing or malformed destination leaves this disabled and every
        /// method a no-op — the same contract <see cref="Telemetry"/> offers.
        /// </summary>
        public static void Initialize(string appVersion, string environment)
        {
            try
            {
                lock (s_lock)
                {
                    if (IsEnabled) return;

                    var otlp = TelemetryConfig.TryResolveOtlp();
                    if (otlp is null) return;

                    var (endpoint, token) = otlp.Value;

                    EnableDiskRetry();

                    var resource = ResourceBuilder.CreateDefault()
                        .AddService(serviceName: ServiceName, serviceVersion: appVersion)
                        .AddAttributes(new Dictionary<string, object>
                        {
                            ["service.namespace"] = ServiceNamespace,
                            // Both spellings on purpose: deployment.environment is what the SigNoz
                            // environment filter reads, deployment.environment.name is the current
                            // OpenTelemetry semantic convention. Each costs nothing and one of them
                            // is always the one a given tool wants.
                            ["deployment.environment"] = environment,
                            ["deployment.environment.name"] = environment,
                        });

                    void Configure(OtlpExporterOptions o, string signalPath)
                    {
                        o.Endpoint = new Uri($"{endpoint}/v1/{signalPath}");
                        // HTTP, not gRPC. The collector is reached through a Cloudflare Tunnel that
                        // carries HTTP only — with the default gRPC protocol the exporter fails
                        // silently and simply never appears in SigNoz.
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                        o.Headers = $"Authorization=Bearer {token}";
                    }

                    s_loggerFactory = LoggerFactory.Create(b =>
                    {
                        b.AddOpenTelemetry(o =>
                        {
                            o.SetResourceBuilder(resource);
                            // Attributes carry the meaning here; the formatted message is just the
                            // event name, so shipping the template as well would double the volume
                            // for nothing.
                            o.IncludeFormattedMessage = true;
                            o.ParseStateValues = true;
                            o.AddOtlpExporter(oo => Configure(oo, "logs"));
                        });
                        b.SetMinimumLevel(LogLevel.Information);
                    });
                    s_logger = s_loggerFactory.CreateLogger(ServiceName);

                    s_activitySource = new ActivitySource(ServiceName, appVersion);
                    s_tracerProvider = Sdk.CreateTracerProviderBuilder()
                        .SetResourceBuilder(resource)
                        .AddSource(ServiceName)
                        .AddOtlpExporter(o => Configure(o, "traces"))
                        .Build();

                    IsEnabled = true;
                }
            }
            catch
            {
                // Never let telemetry setup break startup.
                IsEnabled = false;
                Shutdown();
            }
        }

        /// <summary>
        /// Turns on the exporter's on-disk retry queue, so telemetry raised while the collector is
        /// unreachable is persisted and sent when connectivity returns instead of being dropped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This matters far more here than it would for a server. TDPdf runs on laptops: offline,
        /// captive-portal wifi and VPN-off are the normal state, not an incident. Without this,
        /// every event raised away from the network is lost silently, and the fleet's telemetry
        /// quietly under-reports exactly the users who are travelling.
        /// </para>
        /// <para>
        /// The queue is a directory of serialised OTLP batches. Setting the path is what enables
        /// the feature — the exporter derives EnableDiskRetry from its presence — and the storage
        /// engine ships inside the exporter package, so no extra dependency is involved. The
        /// variable is read when the providers are built, which is why this runs first.
        /// </para>
        /// <para>
        /// <b>This reverses the "no on-disk buffering" choice</b> that <see cref="TelemetryStore"/>
        /// records for the Application Insights channel, and the reversal is deliberate: everything
        /// written here has already been through <see cref="Sanitizer"/>, so the queue holds event
        /// names and scrubbed text rather than document paths. It lives under the user's own
        /// LocalApplicationData, is disclosed in PRIVACY.md, and is deleted along with the rest of
        /// that folder. What it buys is that a crash report raised on a plane still arrives.
        /// </para>
        /// <para>
        /// Note the limit: this retries batches whose <em>send</em> failed. A batch still sitting in
        /// the in-memory processor when the process is killed is not covered — closing that gap for
        /// crash records specifically is what the on-disk crash log replay is for.
        /// </para>
        /// </remarks>
        private static void EnableDiskRetry()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TDPdf", "telemetry-spool");
                System.IO.Directory.CreateDirectory(dir);
                Environment.SetEnvironmentVariable(
                    "OTEL_DOTNET_EXPERIMENTAL_OTLP_DISK_RETRY_DIRECTORY_PATH", dir);
            }
            catch
            {
                // A read-only or redirected profile just means no queue — export still works while
                // the collector is reachable, which is strictly better than failing to start.
            }
        }

        /// <summary>
        /// One event as a log record. <paramref name="properties"/> must already be scrubbed.
        /// </summary>
        public static void TrackEvent(string name, IDictionary<string, string>? properties)
        {
            if (!IsEnabled || s_logger is null) return;
            try
            {
                var state = new List<KeyValuePair<string, object?>>(( properties?.Count ?? 0) + 1)
                {
                    // The conventional attribute for a log record that represents an event, which
                    // is what every one of TDPdf's TrackEvent calls actually is.
                    new("event.name", name),
                };
                if (properties is not null)
                    foreach (var kv in properties)
                        state.Add(new KeyValuePair<string, object?>(kv.Key, kv.Value));

                s_logger.Log(LogLevel.Information, default, state, null, (_, _) => name);
            }
            catch { /* swallow — telemetry never breaks the app */ }
        }

        /// <summary>
        /// A completed operation as a span, so SigNoz can compute latency percentiles natively
        /// rather than us pre-aggregating. The operation has already finished by the time we are
        /// called, so start and end are set explicitly from the measured duration.
        /// </summary>
        public static void TrackOperation(string name, double durationMs, bool success,
                                          IDictionary<string, string>? properties)
        {
            if (!IsEnabled || s_activitySource is null) return;
            try
            {
                var end = DateTime.UtcNow;
                var start = end.AddMilliseconds(-Math.Max(0, durationMs));

                using var activity = s_activitySource.StartActivity(
                    name, ActivityKind.Internal, parentContext: default,
                    startTime: start);
                if (activity is null) return;   // no listener — nothing sampled this source

                activity.SetTag("operation.success", success);
                if (properties is not null)
                    foreach (var kv in properties)
                        activity.SetTag(kv.Key, kv.Value);

                activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                activity.SetEndTime(end);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// A crash as an ERROR log record carrying the OpenTelemetry exception attributes.
        /// </summary>
        /// <remarks>
        /// Takes pre-scrubbed strings rather than an <see cref="Exception"/> precisely so the
        /// unscrubbed message and stack trace cannot reach the wire. See the class remarks.
        /// </remarks>
        public static void TrackCrash(string exceptionType, string scrubbedMessage,
                                      string scrubbedStackTrace, string source, bool recoverable,
                                      string groupingKey)
        {
            if (!IsEnabled || s_logger is null) return;
            try
            {
                var state = new List<KeyValuePair<string, object?>>
                {
                    new("event.name", "Crash"),
                    new("exception.type", exceptionType),
                    new("exception.message", scrubbedMessage),
                    new("exception.stacktrace", scrubbedStackTrace),
                    new("crash.source", source),
                    new("crash.recoverable", recoverable),
                    new("crash.grouping_key", groupingKey),
                };
                s_logger.Log(LogLevel.Error, default, state, null, (_, _) => "Crash");
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Bounded flush of the span pipeline before shutdown.
        /// </summary>
        /// <remarks>
        /// Spans can be force-flushed directly. Log records cannot: this build drives them through
        /// an <see cref="ILoggerFactory"/>, which exposes no ForceFlush — their batch is drained by
        /// disposing the factory, which <see cref="Shutdown"/> does. So the exit path must be
        /// Flush() THEN Shutdown(), which is what <see cref="Telemetry"/> does. Calling Flush()
        /// alone and then killing the process would lose buffered log records, crash records
        /// included.
        /// </remarks>
        public static void Flush(int timeoutMs = 2000)
        {
            if (!IsEnabled) return;
            try { s_tracerProvider?.ForceFlush(timeoutMs); } catch { /* swallow */ }
        }

        /// <summary>Stops export and releases both pipelines. Idempotent; never throws.</summary>
        public static void Shutdown()
        {
            lock (s_lock)
            {
                IsEnabled = false;
                try { s_loggerFactory?.Dispose(); } catch { /* swallow */ }
                try { s_tracerProvider?.Dispose(); } catch { /* swallow */ }
                try { s_activitySource?.Dispose(); } catch { /* swallow */ }
                s_loggerFactory = null;
                s_logger = null;
                s_tracerProvider = null;
                s_activitySource = null;
            }
        }
    }
}
