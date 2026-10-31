#!/usr/bin/env node
/**
 * Apply the TDPdf alert rules to SigNoz.
 * Modeled on STFoA-Church's scripts/signoz/apply-alert-rules.mjs (same payload
 * shape, confirmed live via GET /api/v1/rules against this cluster on 2026-08-20).
 *
 * Read-only by default: prints what it would create or update and stops. Pass --apply to write.
 *
 *   SIGNOZ_API_KEY=<query/rules key> node scripts/signoz/apply-alert-rules.mjs
 *   SIGNOZ_API_KEY=<key> node scripts/signoz/apply-alert-rules.mjs --apply
 *
 * Fetch the key live rather than trusting a stale copy:
 *   kubectl -n signoz get secret signoz-query-api-key -o jsonpath='{.data.key}' | base64 -d
 *
 * Matches an existing rule by its `alert` name, so a re-run updates rather than duplicates. It
 * never deletes a rule it doesn't know about.
 *
 * Every rule needs a notification channel, and SigNoz refuses a rule that names none. The query/rules
 * service-account key can create channels (POST /api/v1/channels returned 201 on 2026-10-24; the older
 * 403 authz_forbidden no longer applies). This script refuses to run (not silently proceed unrouted)
 * until the `ntfy-tdpdf` channel exists (a webhook channel to the ntfy topic `tdpdf`).
 */
// RULES_MODULE selects another rules file in this directory ;
// RULES_SOURCE sets the `source` label. Defaults keep the TDPdf behaviour unchanged.
const RULES_MODULE = process.env.RULES_MODULE || './alert-rules.mjs';
const RULES_SOURCE = process.env.RULES_SOURCE || 'tdpdf';
const { RULES } = await import(RULES_MODULE);

const BASE = process.env.SIGNOZ_URL || 'https://signoz.cluster.thedoodleproject.net';
const KEY = process.env.SIGNOZ_API_KEY;
const CHANNEL = process.env.SIGNOZ_CHANNEL || 'ntfy-tdpdf';
const APPLY = process.argv.includes('--apply');

if (!KEY) {
  console.error('SIGNOZ_API_KEY is required.');
  process.exit(2);
}

async function api(pathname, init = {}) {
  const res = await fetch(`${BASE}${pathname}`, {
    ...init,
    headers: {
      'SIGNOZ-API-KEY': KEY,
      'Content-Type': 'application/json',
      ...(init.headers || {}),
    },
  });
  const text = await res.text();
  let body;
  try {
    body = JSON.parse(text);
  } catch {
    // A SigNoz path that does not exist falls through to the single-page app, which answers
    // 200 with HTML. Treat that as the routing error it is.
    throw new Error(`${pathname} returned non-JSON (${res.status}).`);
  }
  if (!res.ok || body.status === 'error') {
    throw new Error(
      `${pathname} ${res.status}: ${body.error?.message || body.error || text.slice(0, 200)}`
    );
  }
  return body;
}

/** Matches the live v2alpha1 rule shape confirmed via GET /api/v1/rules on 2026-08-20. */
function toPayload(rule) {
  return {
    schemaVersion: 'v2alpha1',
    alert: rule.name,
    alertType: rule.alertType || 'LOGS_BASED_ALERT',
    ruleType: 'threshold_rule',
    disabled: false,
    condition: {
      selectedQueryName: 'A',
      compositeQuery: {
        queries: [
          {
            type: 'clickhouse_sql',
            spec: { name: 'A', disabled: false, query: rule.query },
          },
        ],
      },
      thresholds: {
        kind: 'basic',
        spec: [
          {
            name: rule.severity,
            target: rule.target,
            targetUnit: '',
            recoveryTarget: null,
            matchType: '1',
            op: '1',
            channels: [CHANNEL],
          },
        ],
      },
    },
    evaluation: {
      kind: 'rolling',
      spec: { evalWindow: rule.evalWindow, frequency: rule.frequency },
    },
    notificationSettings: { groupBy: [], renotify: { enabled: false } },
    labels: {
      severity: rule.severity,
      source: RULES_SOURCE,
      managed_by: 'apply-alert-rules.mjs',
    },
    annotations: { summary: rule.intent, description: rule.description },
  };
}

const channels = await api('/api/v1/channels');
const names = (channels.data || []).map((c) => c.name);
if (!names.includes(CHANNEL)) {
  console.error(
    `Notification channel "${CHANNEL}" does not exist. SigNoz refuses a rule that names no ` +
      'channel, and only an ADMIN can create one.\n' +
      'Create it in the SigNoz UI: Settings -> Alert Channels -> New, type Webhook, URL ' +
      '= the ntfy publish URL for topic tdpdf (https://alert.thedoodleproject.net/tdpdf?template=alertmanager&auth=...).\n' +
      `Channels visible to this key: ${names.length ? names.join(', ') : '(none)'}`
  );
  process.exit(1);
}

const existing = await api('/api/v1/rules');
const byName = new Map((existing.data?.rules || []).map((r) => [r.alert || r.name, r]));

let created = 0;
let updated = 0;
for (const rule of RULES) {
  const current = byName.get(rule.name);
  const verb = current ? 'UPDATE' : 'CREATE';
  if (!APPLY) {
    console.log(`${verb.padEnd(6)} ${rule.name}`);
    continue;
  }
  const payload = toPayload(rule);
  if (current) {
    await api(`/api/v1/rules/${current.id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
    updated += 1;
  } else {
    await api('/api/v1/rules', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
    created += 1;
  }
  console.log(`${verb.padEnd(6)} ${rule.name}`);
}

console.log(
  APPLY
    ? `\nDone. ${created} created, ${updated} updated, channel "${CHANNEL}".`
    : `\nDRY RUN — ${RULES.length} rule(s). Pass --apply to write.`
);
