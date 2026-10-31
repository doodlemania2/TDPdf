/**
 * TDPdf alert rules for SigNoz. Rules as code: scripts/signoz/apply-alert-rules.mjs applies them.
 * (Moved here from K3s services/signoz/scripts/tdpdf-alert-rules.mjs; this is now the only copy.)
 *
 * Field names are what the app emits in Diagnostics/OtlpTelemetry.cs:
 *   - events are LOG records with attribute `event.name` (Crash, App.Startup, File.SaveFailed, ...);
 *   - a crash carries `crash.grouping_key`, not `GroupingKey`;
 *   - Telemetry.TrackOperation ships a SPAN named `Op.<name>`, so latency rules read signoz_traces,
 *     never signoz_logs.
 * `host.name` and `service.version` are resource attributes. Every threshold marked `provisional`
 * is a first cut, not a measured baseline.
 *
 * Scope: every query filters service.name=tdpdf, service.namespace=stfoa,
 * deployment.environment=production — same three-attribute contract as every other app onboarded
 * onto this collector (STFoA-Church/docs/observability/otlp-onboarding.md §3).
 */

const LOGS = 'signoz_logs.distributed_logs_v2';
// signoz_logs stores timestamp as UInt64 nanoseconds (confirmed live via `DESCRIBE TABLE
// signoz_logs.distributed_logs_v2`, same as STFoA-Church's alert-rules.mjs found for traces).
const WINDOW =
  'timestamp >= {{.start_timestamp_nano}} AND timestamp <= {{.end_timestamp_nano}}';
const SCOPE =
  "resources_string['service.name'] = 'tdpdf' " +
  "AND resources_string['service.namespace'] = 'stfoa' " +
  "AND resources_string['deployment.environment'] = 'production'";
const attr = (key) => `attributes_string['${key}']`;
const num = (key) => `attributes_number['${key}']`;
const res = (key) => `resources_string['${key}']`;
// signoz_traces stores timestamp as DateTime64(9), so a raw nanosecond integer overflows.
const TRACE_WINDOW =
  'timestamp >= fromUnixTimestamp64Nano({{.start_timestamp_nano}}) AND ' +
  'timestamp <= fromUnixTimestamp64Nano({{.end_timestamp_nano}})';
const DAY_NS = 24 * 60 * 60 * 1000000000;

// A SigNoz threshold rule needs a TIME SERIES. A bare aggregate — with or without GROUP BY —
// makes the evaluator log `query result is nil` every cycle and the rule can never fire, however
// many rows the same query returns through /api/v5/query_range. Projecting one constant time
// column is what turns the result into a series. Found and proven in STFoA-Church (#4193, PR
// #4202): 6,158 evaluations, 6,158 nils, zero alerts.
//
// CONSTANT is load-bearing. toStartOfInterval(...) also makes rules fire, but re-buckets the
// aggregate, so "10 failures in 15 minutes" silently becomes "10 failures in one minute". One
// constant bucket per evaluation keeps every count, quantile and ratio meaning "across the whole
// evaluation window", which is what the existing targets were written against.
const TS = 'fromUnixTimestamp64Nano({{.start_timestamp_nano}}) AS ts';

export const RULES = [
  {
    // Priority 1 — the alert that would have caught the live 2026-08-20 crash nothing detected.
    name: 'TDPdf - New Crash Signature',
    severity: 'critical',
    evalWindow: '15m0s',
    frequency: '5m0s',
    target: 0, // provisional: fires on the FIRST occurrence of any never-seen GroupingKey
    intent: 'a GroupingKey not seen in the preceding 7 days appears in a Crash event',
    description:
      'A crash with a GroupingKey not seen in the last 7 days just occurred. This is a new ' +
      'failure mode, not a repeat of a known one — treat as a live incident until triaged.',
    query: `SELECT
  ${TS},
  grouping_key,
  count() AS value
FROM (
  SELECT ${attr('crash.grouping_key')} AS grouping_key
  FROM ${LOGS}
  WHERE ${WINDOW}
    AND ${SCOPE}
    AND ${attr('event.name')} = 'Crash'
    AND ${attr('crash.grouping_key')} != ''
)
WHERE grouping_key NOT IN (
  SELECT DISTINCT ${attr('crash.grouping_key')}
  FROM ${LOGS}
  WHERE timestamp >= {{.start_timestamp_nano}} - ${7 * DAY_NS}
    AND timestamp < {{.start_timestamp_nano}}
    AND ${SCOPE}
    AND ${attr('event.name')} = 'Crash'
)
GROUP BY ts, grouping_key`,
  },
  {
    // Priority 2 — raw crash counts can't distinguish one unlucky user from a fleet regression;
    // this can.
    name: 'TDPdf - Crash-Free Session Rate Dropped',
    severity: 'critical',
    evalWindow: '1h0m0s',
    frequency: '15m0s',
    target: 0.05, // provisional: >5% of startups crashing in the window. Not an N-1 integer
    // adjustment (STFoA-Church's toPayload note only applies to integer counts) — this is a
    // float ratio compared directly with op Above.
    intent: 'Crash count / App.Startup count exceeds 5% in the last hour',
    description:
      'More than 5% of sessions in the last hour ended in a crash. Re-baseline this threshold ' +
      'once real crash-free-rate data exists — 5% is a placeholder, not a measured normal.',
    query: `SELECT
  ${TS},
  if(startups = 0, 0, crashes / startups) AS value
FROM (
  SELECT
    countIf(${attr('event.name')} = 'Crash') AS crashes,
    countIf(${attr('event.name')} = 'App.Startup') AS startups
  FROM ${LOGS}
  WHERE ${WINDOW}
    AND ${SCOPE}
    AND ${attr('event.name')} IN ('Crash', 'App.Startup')
)
-- crashes/startups come from the subquery, which returns exactly ONE row, so grouping by them
-- alongside ts is a no-op that satisfies ClickHouse rather than changing what is measured.
-- Grouping by ts alone fails: ClickHouse 215, column startups not under aggregate function.
GROUP BY ts, crashes, startups`,
  },
  {
    name: 'TDPdf - Version Rollout Stall',
    severity: 'warning',
    evalWindow: '6h0m0s',
    frequency: '30m0s',
    target: 0.1, // provisional: >10% of active traffic still on a non-latest version
    intent:
      'more than 10% of active machines are reporting a version other than the most-common ' +
      'one seen in the last 6 hours',
    description:
      'A meaningful share of the fleet is still on an old build 6+ hours into the window. ' +
      'This is a PROXY for "stalled rollout" (share of non-latest-version traffic), not a true ' +
      "time-since-release trigger — there's no release-tagging mechanism to anchor to yet. " +
      'Revisit once releases are tagged (e.g. a deploy marker event) so this can measure actual ' +
      'hours-since-release instead of a rolling snapshot.',
    query: `WITH counts AS (
  SELECT
    ${res('service.version')} AS version,
    count() AS cnt
  FROM ${LOGS}
  WHERE ${WINDOW}
    AND ${SCOPE}
    AND ${res('service.version')} != ''
  GROUP BY version
),
latest AS (
  SELECT version FROM counts ORDER BY cnt DESC LIMIT 1
)
SELECT
  ${TS},
  version,
  cnt / (SELECT sum(cnt) FROM counts) AS value
FROM counts
WHERE version != (SELECT version FROM latest)
GROUP BY ts, version, cnt`,
  },
  {
    name: 'TDPdf - Fleet Went Quiet',
    severity: 'warning',
    evalWindow: '1h0m0s',
    frequency: '1h0m0s',
    target: 23, // provisional: fires at 24h+ of silence (op Above 23)
    intent:
      'a machine that emitted telemetry in the last 7 days has not emitted anything in the ' +
      'last 24 hours',
    description:
      'A previously-active machine has gone silent for 24+ hours. Catches broken telemetry as ' +
      'well as a broken install — either way, nobody is watching that machine right now.',
    query: `SELECT
  ${TS},
  host,
  dateDiff('hour', fromUnixTimestamp64Nano(toInt64(last_seen)), now()) AS value
FROM (
  SELECT
    ${res('host.name')} AS host,
    max(timestamp) AS last_seen
  FROM ${LOGS}
  WHERE timestamp >= {{.end_timestamp_nano}} - ${7 * DAY_NS}
    AND timestamp <= {{.end_timestamp_nano}}
    AND ${SCOPE}
    AND ${res('host.name')} != ''
  GROUP BY host
)
GROUP BY ts, host, last_seen`,
  },
  {
    name: 'TDPdf - File Save Failures',
    severity: 'warning',
    evalWindow: '15m0s',
    frequency: '5m0s',
    target: 2, // provisional: fires at 3+ in 15 minutes — the event already exists, nothing
    // watches it today, so this is a first cut, not a measured baseline.
    intent: 'at least 3 File.SaveFailed events in 15 minutes',
    description:
      'File.SaveFailed fired at least 3 times in 15 minutes. The event already exists in the ' +
      "app; this is the first thing that actually watches it.",
    query: `SELECT
  ${TS},
  count() AS value
FROM ${LOGS}
WHERE ${WINDOW}
  AND ${SCOPE}
  AND ${attr('event.name')} = 'File.SaveFailed'
GROUP BY ts`,
  },
  {
    name: 'TDPdf - Slow Operations (p95)',
    severity: 'warning',
    evalWindow: '30m0s',
    frequency: '15m0s',
    target: 5000, // provisional: 5000ms p95, no measured baseline yet
    intent: 'p95 duration of any Op.* operation exceeds 5000ms over 30 minutes',
    description:
      'p95 duration for an Op.* operation crossed 5s. Threshold is a placeholder — re-baseline ' +
      'against real Op.* span durations.',
    alertType: 'TRACES_BASED_ALERT',
    query: `SELECT
  ${TS},
  name AS operation,
  quantile(0.95)(duration_nano) / 1000000 AS value
FROM signoz_traces.distributed_signoz_index_v3
WHERE ${TRACE_WINDOW}
  AND ${SCOPE}
  AND name LIKE 'Op.%'
GROUP BY ts, operation`,
  },
];
