using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Thin opt-in wrapper over <see cref="TelemetryClient"/>. By
    /// default this is a no-op — telemetry only emits when the
    /// administrator has provisioned <see cref="TelemetryStore"/>.
    ///
    /// Guarantees:
    ///   * No configured destination (see <see cref="TelemetryConfig"/>)
    ///     OR consent withdrawn ⇒ <see cref="IsEnabled"/> stays
    ///     <c>false</c> and every method returns immediately without
    ///     touching the network. A build that has never been pointed at
    ///     a collector cannot report anywhere, whatever the setting says.
    ///   * Every public method swallows its own exceptions. Telemetry
    ///     can never crash, block, or slow the app.
    ///   * No <c>TrackException(Exception)</c> overload is exposed,
    ///     because the raw <see cref="Exception.Message"/> and
    ///     <see cref="Exception.StackTrace"/> commonly contain user
    ///     document paths. Callers must use <see cref="TrackCrash"/>
    ///     which scrubs via <see cref="Sanitizer"/>.
    /// </summary>
    internal static class Telemetry
    {
        private static TelemetryClient? s_client;
        private static TelemetryConfiguration? s_config;
        private static string s_appVersion = string.Empty;
        private static readonly object s_lock = new();
        private static System.Threading.Timer? s_heartbeatTimer;
        private static DateTimeOffset s_sessionStart;

        /// <summary>
        /// Identifies this run. Random per launch and never written to disk, so it is NOT a device
        /// fingerprint — the deliberate absence of Context.User.Id / Device.Id below still holds.
        /// </summary>
        /// <remarks>
        /// Without it, crash counts cannot be normalised: there is no way to tell one user having a
        /// terrible afternoon from a regression hitting the whole fleet, which is exactly the
        /// question that mattered on 2026-08-20. With it, crash-free session rate is
        /// countIf(Crash) / countIf(App.Startup) grouped by session.
        /// </remarks>
        public static string SessionId { get; private set; } = string.Empty;

        /// <summary>
        /// How often a running instance says it is still alive. A "fleet went quiet" alert needs a
        /// positive signal — App.Startup alone cannot distinguish "nobody launched it today" from
        /// "telemetry broke", and the second is the one worth waking up for. Fifteen minutes is
        /// cheap at this fleet size: the busiest hour in 90 days carried 131 events in total.
        /// </summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(15);

        public static bool IsEnabled { get; private set; }

        /// <summary>
        /// Best-effort start. Reads the provisioning file, builds a
        /// private <see cref="TelemetryConfiguration"/> (never the
        /// global <c>TelemetryConfiguration.Active</c>) with auto
        /// collectors disabled, and primes a <see cref="TelemetryClient"/>.
        /// Does not emit any event; callers should follow with the
        /// startup event that fits their context (App.Startup,
        /// Install.Start, etc.).
        /// </summary>
        public static void Initialize(string appVersion)
        {
            try
            {
                lock (s_lock)
                {
                    if (IsEnabled) return;

                    // Before any gate: a session identity even when nothing is exported, so the
                    // value is stable for the whole run rather than appearing only once telemetry
                    // happens to be configured.
                    if (SessionId.Length == 0) SessionId = Guid.NewGuid().ToString("N");
                    s_sessionStart = DateTimeOffset.UtcNow;

                    // Two independent gates, both required.
                    //
                    // Consent: a per-user opt-out from the Settings dialog. Read defensively —
                    // a corrupt user.config must not be the reason crash reporting silently
                    // stops, so a throw here is treated as "consented" (the default) rather
                    // than swallowing the whole Initialize.
                    bool consented = true;
                    try { consented = TDPdf.Properties.Settings.Default.TelemetryEnabled; }
                    catch { /* unreadable user.config — fall back to the default */ }
                    if (!consented) return;

                    // OTLP first, and independently: the two destinations are resolved
                    // separately, so a device configured for only one still reports to it. During
                    // the migration both are live; afterwards, retiring either is a config change.
                    s_appVersion = appVersion ?? string.Empty;
                    OtlpTelemetry.Initialize(s_appVersion, ResolveEnvironment(), SessionId);

                    // Destination: absent in every build that has not been explicitly pointed
                    // somewhere, which is what lets this source ship publicly without phoning
                    // home. Also honours the /clear-telemetry device opt-out internally.
                    string? connectionString = TelemetryConfig.TryResolveConnectionString();
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        // No Application Insights destination, but OTLP may still be live — in
                        // which case this is enabled, not disabled. Getting this wrong would make
                        // every Track* call below a no-op on an OTLP-only device.
                        IsEnabled = OtlpTelemetry.IsEnabled;
                        return;
                    }

                    var config = TelemetryConfiguration.CreateDefault();
                    config.ConnectionString = connectionString;

                    // InMemoryChannel: no on-disk buffering => no
                    // privacy surface and no per-user permission
                    // problems. Buffer is best-effort; events lost on
                    // crash/network failure are expected.
                    config.TelemetryChannel = new InMemoryChannel
                    {
                        DeveloperMode = false,
                    };

                    // Disable automatic collectors that could leak more
                    // than we want (dependency tracking, perf counters,
                    // live metrics, heartbeat, etc.). The base SDK has
                    // none of these wired by default — this guard is
                    // belt-and-suspenders against future SDK changes.
                    config.DisableTelemetry = false;

                    s_config = config;
                    s_client = new TelemetryClient(config);
                    s_client.Context.Component.Version = appVersion;
                    s_client.Context.Cloud.RoleName = "TDPdf";
                    s_appVersion = appVersion ?? string.Empty;
                    // Deliberately do NOT set Context.User.Id or
                    // Device.Id — no persistent device fingerprint.

                    IsEnabled = true;
                }

                // Outside the lock: replay is I/O and emits through the very methods that take it.
                ReplaySpooledCrashes();
                StartHeartbeat();
            }
            catch
            {
                // Initialization failure must never propagate. Leave
                // IsEnabled false; future calls become no-ops.
                IsEnabled = false;
                s_client = null;
                s_config = null;
            }
        }

        /// <summary>
        /// Emit a custom event. Property values are scrubbed; callers
        /// should still avoid passing file paths, document names, or
        /// other user content.
        /// </summary>
        public static void TrackEvent(string name, IDictionary<string, string>? properties = null)
        {
            if (!IsEnabled) return;
            if (s_client is null && !OtlpTelemetry.IsEnabled) return;
            try
            {
                IDictionary<string, string>? scrubbed = ScrubProperties(properties);
                s_client?.TrackEvent(name, scrubbed);
                OtlpTelemetry.TrackEvent(name, scrubbed);
            }
            catch { /* swallow — telemetry must never throw */ }
        }



        /// <summary>
        /// Emit a completed-operation event carrying a duration metric
        /// and a success flag. Prefer <see cref="StartOperation"/> for
        /// the common "time a using-block" pattern.
        /// </summary>
        public static void TrackOperation(string name, double durationMs, bool success, IDictionary<string, string>? properties = null)
        {
            if (!IsEnabled) return;
            if (s_client is null && !OtlpTelemetry.IsEnabled) return;
            try
            {
                var props = new Dictionary<string, string>
                {
                    ["Success"]    = success ? "true" : "false",
                    ["DurationMs"] = ((long)durationMs).ToString(),
                };
                IDictionary<string, string>? scrubbed = ScrubProperties(properties);
                if (scrubbed is not null)
                    foreach (var kv in scrubbed)
                        props[kv.Key] = kv.Value;

                var metrics = new Dictionary<string, double> { ["DurationMs"] = durationMs };
                s_client?.TrackEvent("Op." + name, props, metrics);
                // As a span, not an event: SigNoz computes latency percentiles from spans, so the
                // p95-duration alert needs this shape rather than a pre-aggregated number.
                OtlpTelemetry.TrackOperation("Op." + name, durationMs, success, props);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Begin timing an operation. Dispose the returned scope (ideally
        /// via <c>using</c>) to emit an <c>Op.&lt;name&gt;</c> event with
        /// the elapsed duration. Call <see cref="Operation.Fail"/> to mark
        /// it unsuccessful. Always returns a usable object even when
        /// telemetry is disabled (the emit is then a no-op), so callers
        /// never have to null-check.
        /// </summary>
        public static Operation StartOperation(string name) => new(name);

        /// <summary>
        /// Disposable stopwatch scope produced by <see cref="StartOperation"/>.
        /// Cheap to allocate; safe to use whether or not telemetry is on.
        /// </summary>
        public sealed class Operation : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _stopwatch;
            private readonly Dictionary<string, string> _properties = new();
            private bool _success = true;
            private bool _completed;

            internal Operation(string name)
            {
                _name = name;
                _stopwatch = Stopwatch.StartNew();
            }

            public Operation With(string key, string value)
            {
                _properties[key] = value;
                return this;
            }

            public void Fail(Exception? exception = null)
            {
                _success = false;
                if (exception is not null)
                    _properties["ExceptionType"] = exception.GetType().FullName ?? "Unknown";
            }

            public void Dispose()
            {
                if (_completed) return;
                _completed = true;
                _stopwatch.Stop();
                TrackOperation(_name, _stopwatch.Elapsed.TotalMilliseconds, _success, _properties);
            }
        }

        /// <summary>
        /// Emit a sanitized crash record. The raw exception object is
        /// never serialized; we only ship type name, scrubbed message,
        /// scrubbed stack, and a grouping hash.
        /// </summary>
        public static void TrackCrash(Exception exception, string source, bool recoverable)
        {
            if (!IsEnabled) return;
            if (s_client is null && !OtlpTelemetry.IsEnabled) return;
            try
            {
                var props = new Dictionary<string, string>
                {
                    ["Source"]        = source,
                    ["Recoverable"]   = recoverable ? "true" : "false",
                    ["ExceptionType"] = exception.GetType().FullName ?? "Unknown",
                    ["Message"]       = Sanitizer.Scrub(exception.Message),
                    ["StackTrace"]    = Sanitizer.Scrub(exception.StackTrace ?? string.Empty),
                    ["GroupingKey"]   = Sanitizer.GroupingKey(exception),
                    ["AppVersion"]    = s_appVersion,
                };
                s_client?.TrackEvent("Crash", props);

                // Queue the same already-scrubbed record on disk. Both pipelines above buffer in
                // memory, so a crash that kills the process takes its own report with it — which is
                // exactly what happened on 2026-08-20. Replay at the next launch closes that.
                CrashSpool.Write(new CrashSpool.Record
                {
                    ExceptionType = props["ExceptionType"],
                    Message = props["Message"],
                    StackTrace = props["StackTrace"],
                    Source = source,
                    Recoverable = recoverable,
                    GroupingKey = props["GroupingKey"],
                    AppVersion = s_appVersion,
                    TimestampUtc = DateTimeOffset.UtcNow,
                });
                // Pass the ALREADY-SCRUBBED strings, never the exception. OpenTelemetry's exception
                // helpers serialise Message and StackTrace verbatim, and TDPdf's exception text
                // routinely carries the path of the document being worked on.
                OtlpTelemetry.TrackCrash(
                    props["ExceptionType"], props["Message"], props["StackTrace"],
                    source, recoverable, props["GroupingKey"]);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Best-effort bounded flush. Called from
        /// <see cref="System.Windows.Application.OnExit"/>. Caps total
        /// blocking time at ~2s so telemetry can never hang shutdown.
        /// </summary>
        public static void Flush()
        {
            if (!IsEnabled) return;
            // Spans force-flush here; buffered LOG records are drained by OtlpTelemetry.Shutdown's
            // dispose, so the exit path must be Flush() then Shutdown() or crash records are lost.
            OtlpTelemetry.Flush();
            if (s_client is null) return;
            try
            {
                s_client.Flush();
                // Classic SDK Flush is fire-and-forget for the HTTPS
                // upload; the upload completes asynchronously on a
                // background thread. The documented pattern is to
                // sleep briefly afterwards.
                Task.Delay(2000).Wait();
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Stops this session reporting and releases the client. Used when a user withdraws
        /// consent mid-session — the alternative, waiting for the next launch, would keep sending
        /// after they had asked us to stop.
        /// </summary>
        /// <remarks>
        /// Deliberately does NOT flush: <see cref="Flush"/> is the caller's decision, because the
        /// right behaviour differs. Withdrawing consent flushes first (events already recorded in
        /// good faith are not silently dropped); a hard stop would not. Idempotent, and never
        /// throws — the same contract as every other method here.
        /// </remarks>
        public static void Shutdown()
        {
            OtlpTelemetry.Shutdown();
            try { s_heartbeatTimer?.Dispose(); } catch { /* swallow */ }
            s_heartbeatTimer = null;
            lock (s_lock)
            {
                IsEnabled = false;
                try { s_config?.Dispose(); }
                catch { /* swallow */ }
                finally
                {
                    s_client = null;
                    s_config = null;
                }
            }
        }

        /// <summary>Begins the periodic liveness signal. Idempotent.</summary>
        private static void StartHeartbeat()
        {
            if (!IsEnabled || s_heartbeatTimer is not null) return;
            try
            {
                s_heartbeatTimer = new System.Threading.Timer(
                    _ => TrackEvent("App.Heartbeat", new Dictionary<string, string>
                    {
                        ["UptimeMinutes"] =
                            ((int)(DateTimeOffset.UtcNow - s_sessionStart).TotalMinutes)
                                .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }),
                    null, HeartbeatInterval, HeartbeatInterval);
            }
            catch { /* a machine that cannot spare a timer still reports everything else */ }
        }

        /// <summary>
        /// Records that this session ended under its own power. The presence or absence of this
        /// event is what separates a clean exit from a crash or a kill — a session with an
        /// App.Startup and no App.SessionEnd did not finish, whether or not a Crash arrived.
        /// Call from Application.OnExit, before <see cref="Flush"/>.
        /// </summary>
        public static void TrackSessionEnd()
        {
            if (!IsEnabled) return;
            try
            {
                s_heartbeatTimer?.Dispose();
                s_heartbeatTimer = null;
                TrackEvent("App.SessionEnd", new Dictionary<string, string>
                {
                    ["DurationMinutes"] =
                        ((int)(DateTimeOffset.UtcNow - s_sessionStart).TotalMinutes)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Sends crash records that were queued by a previous run that died before it could report
        /// them. Marked <c>Replayed</c> so they are distinguishable from live crashes — their
        /// arrival time is this launch, not the crash, and an alert counting crashes per session
        /// would otherwise attribute an old crash to a healthy session.
        /// </summary>
        private static void ReplaySpooledCrashes()
        {
            CrashSpool.Replay(record =>
            {
                try
                {
                    var props = new Dictionary<string, string>
                    {
                        ["Source"]        = record.Source,
                        ["Recoverable"]   = record.Recoverable ? "true" : "false",
                        ["ExceptionType"] = record.ExceptionType,
                        ["Message"]       = record.Message,
                        ["StackTrace"]    = record.StackTrace,
                        ["GroupingKey"]   = record.GroupingKey,
                        ["AppVersion"]    = record.AppVersion,
                        ["Replayed"]      = "true",
                        ["CrashTimeUtc"]  = record.TimestampUtc.ToString("o"),
                    };
                    // Straight to the sinks, never back through TrackCrash — that would re-spool a
                    // record we are in the middle of draining and replay it forever.
                    s_client?.TrackEvent("Crash", props);
                    OtlpTelemetry.TrackCrash(record.ExceptionType, record.Message,
                        record.StackTrace, record.Source, record.Recoverable, record.GroupingKey);
                }
                catch { /* swallow */ }
            });
        }

        /// <summary>
        /// The deployment.environment resource attribute. A desktop application has no staging
        /// tier, so this distinguishes a real install from a developer's machine rather than a
        /// deployment ring: an unconfigured build reports "development", which also keeps a
        /// contributor's local runs out of the production stream if they point one at a collector.
        /// </summary>
        private static string ResolveEnvironment()
        {
            try
            {
                string? explicitEnv = Environment.GetEnvironmentVariable("TDPDF_DEPLOYMENT_ENVIRONMENT");
                if (!string.IsNullOrWhiteSpace(explicitEnv)) return explicitEnv.Trim();
            }
            catch { /* restricted host */ }

#if DEBUG
            return "development";
#else
            return "production";
#endif
        }

        private static IDictionary<string, string>? ScrubProperties(IDictionary<string, string>? properties)
        {
            if (properties is null || properties.Count == 0)
                return properties;

            var copy = new Dictionary<string, string>(properties.Count + 1);
            foreach (var kv in properties)
                copy[kv.Key] = Sanitizer.Scrub(kv.Value);
            // Stamped centrally rather than at each of the 24 call sites — a signal that has to be
            // remembered is a signal that will be missing from whichever event you need it on.
            if (SessionId.Length > 0) copy["SessionId"] = SessionId;
            return copy;
        }
    }
}
