#!/usr/bin/env node
// aggregate-runtime-events.mjs
//
// Read one or more JSONL files of ProductRuntimeEvent records and print a
// JSON aggregate with the six finding categories the runtime-log-analysis
// skill turns into a report:
//
//   1. repeatedErrors             - groups of (event, error.type, error.code)
//   2. slowOperations             - p50 / p95 / p99 per (subsystem, operation)
//   3. noisyEvents                - top-share event names plus rate spikes
//   4. missingCorrelationIds      - per-subsystem null-correlationId streaks
//   5. suspiciousSequences        - hard-coded invariants + UNVERIFIED bucket
//   6. parseWarnings              - lines that did not parse as JSON
//
// Read-only. Never writes to disk. Never calls a network. Exits 0 on
// success; 2 on usage error; 3 when no input lines were readable.
//
// The skill itself (./../SKILL.md) turns these numbers into Markdown +
// fenced JSON. This script is the deterministic bit so two CLIs produce
// the same aggregate from the same input.

import { readFileSync, existsSync, statSync } from "node:fs";
import { resolve } from "node:path";

const args = process.argv.slice(2);
if (args.length === 0 || args.includes("--help") || args.includes("-h")) {
  process.stderr.write(
    "usage: aggregate-runtime-events.mjs <jsonl> [more.jsonl ...]\n" +
      "       Reads ProductRuntimeEvent JSONL and prints an aggregate to stdout.\n",
  );
  process.exit(args.length === 0 ? 2 : 0);
}

// Project-extensible. The skill documents this list as INVARIANTS the
// project author opted into; the script ships a tiny default set so the
// "suspiciousSequences" output is never silently empty when none are
// configured. Unknown orderings flow into UNVERIFIED so the reviewer can
// inspect them.
const KNOWN_INVARIANTS = [
  // pair: [before, after]  -> emitting `after` without a preceding `before`
  // for the same correlationId is suspicious.
  ["auth.session-issued", "auth.session-rotated"],
  ["order.placed", "order.shipped"],
];

const NOISY_SHARE_THRESHOLD = 0.4; // top-1 event > 40% of all events
const NOISY_RATE_PER_MIN = 60; // sustained > 1/sec for a minute
const MISSING_CORR_STREAK = 5;

const events = [];
const parseWarnings = [];

for (const path of args) {
  const abs = resolve(path);
  if (!existsSync(abs) || !statSync(abs).isFile()) {
    parseWarnings.push({ sourcePath: abs, lineNumber: 0, reason: "missing-or-not-a-file" });
    continue;
  }
  const text = readFileSync(abs, "utf8");
  let lineNumber = 0;
  for (const raw of text.split(/\r?\n/)) {
    lineNumber++;
    const line = raw.trim();
    if (line === "") continue;
    if (line[0] !== "{") {
      parseWarnings.push({ sourcePath: abs, lineNumber, reason: "not-json-object", rawLine: truncate(line) });
      continue;
    }
    let obj;
    try {
      obj = JSON.parse(line);
    } catch (err) {
      parseWarnings.push({ sourcePath: abs, lineNumber, reason: "json-parse: " + err.message, rawLine: truncate(line) });
      continue;
    }
    if (!isLikelyRuntimeEvent(obj)) {
      parseWarnings.push({ sourcePath: abs, lineNumber, reason: "missing-required-field", rawLine: truncate(line) });
      continue;
    }
    obj.__source = { path: abs, line: lineNumber };
    events.push(obj);
  }
}

if (events.length === 0 && parseWarnings.length === 0) {
  process.stderr.write("aggregate-runtime-events: no input lines\n");
  process.exit(3);
}

const aggregate = {
  schemaVersion: 1,
  inputCount: events.length,
  fileCount: args.length,
  window: computeWindow(events),
  repeatedErrors: groupRepeatedErrors(events),
  slowOperations: groupSlowOperations(events),
  noisyEvents: detectNoisy(events),
  missingCorrelationIds: detectMissingCorrelation(events),
  suspiciousSequences: detectSuspiciousSequences(events),
  parseWarnings,
};

process.stdout.write(JSON.stringify(aggregate, null, 2) + "\n");
process.exit(0);

// ---------------------------------------------------------------------------

function isLikelyRuntimeEvent(o) {
  return (
    o &&
    typeof o === "object" &&
    typeof o.timestamp === "string" &&
    typeof o.level === "string" &&
    typeof o.event === "string" &&
    typeof o.subsystem === "string"
  );
}

function truncate(s, max = 240) {
  return s.length > max ? s.slice(0, max) + "..." : s;
}

function computeWindow(events) {
  if (events.length === 0) return { from: null, to: null };
  let lo = events[0].timestamp;
  let hi = events[0].timestamp;
  for (const e of events) {
    if (e.timestamp < lo) lo = e.timestamp;
    if (e.timestamp > hi) hi = e.timestamp;
  }
  return { from: lo, to: hi };
}

function groupRepeatedErrors(events) {
  const buckets = new Map();
  for (const e of events) {
    const isError = e.level === "Error" || e.level === "Fatal" || e.status === "Failed" || e.status === "Timeout";
    if (!isError) continue;
    const key = [e.event, e.error?.type ?? "", e.error?.code ?? ""].join("|");
    let b = buckets.get(key);
    if (!b) {
      b = {
        event: e.event,
        errorType: e.error?.type ?? null,
        errorCode: e.error?.code ?? null,
        retryable: e.error?.retryable ?? null,
        count: 0,
        firstSeen: e.timestamp,
        lastSeen: e.timestamp,
        sampleMessage: e.error?.message ?? null,
        evidenceRefs: [],
      };
      buckets.set(key, b);
    }
    b.count++;
    if (e.timestamp < b.firstSeen) b.firstSeen = e.timestamp;
    if (e.timestamp > b.lastSeen) b.lastSeen = e.timestamp;
    if (b.evidenceRefs.length < 3) {
      b.evidenceRefs.push(`${e.__source.path}:${e.__source.line}`);
    }
  }
  return Array.from(buckets.values()).sort((a, b) => b.count - a.count);
}

function groupSlowOperations(events) {
  const buckets = new Map();
  for (const e of events) {
    const ms = e.duration?.ms;
    if (typeof ms !== "number" || e.status === "Failed") continue;
    const key = `${e.subsystem}|${e.operation ?? ""}`;
    let b = buckets.get(key);
    if (!b) {
      b = { subsystem: e.subsystem, operation: e.operation ?? null, samples: [], slowest: null };
      buckets.set(key, b);
    }
    b.samples.push(ms);
    if (!b.slowest || ms > b.slowest.ms) {
      b.slowest = { ms, source: `${e.__source.path}:${e.__source.line}` };
    }
  }
  const out = [];
  for (const b of buckets.values()) {
    b.samples.sort((x, y) => x - y);
    out.push({
      subsystem: b.subsystem,
      operation: b.operation,
      sampleCount: b.samples.length,
      p50: percentile(b.samples, 0.5),
      p95: percentile(b.samples, 0.95),
      p99: percentile(b.samples, 0.99),
      slowest: b.slowest,
    });
  }
  // Slowest p95 first; ties broken by sample count.
  out.sort((a, b) => b.p95 - a.p95 || b.sampleCount - a.sampleCount);
  return out;
}

function percentile(sortedAsc, p) {
  if (sortedAsc.length === 0) return 0;
  const idx = Math.min(sortedAsc.length - 1, Math.floor(p * sortedAsc.length));
  return sortedAsc[idx];
}

function detectNoisy(events) {
  const total = events.length;
  if (total === 0) return [];
  const counts = new Map();
  for (const e of events) counts.set(e.event, (counts.get(e.event) ?? 0) + 1);

  const minuteBuckets = new Map(); // event -> Map(minuteKey -> count)
  for (const e of events) {
    const minute = e.timestamp.slice(0, 16); // yyyy-mm-ddThh:mm
    let m = minuteBuckets.get(e.event);
    if (!m) minuteBuckets.set(e.event, (m = new Map()));
    m.set(minute, (m.get(minute) ?? 0) + 1);
  }

  const out = [];
  for (const [event, count] of counts) {
    const share = count / total;
    let peakPerMinute = 0;
    for (const v of minuteBuckets.get(event).values()) {
      if (v > peakPerMinute) peakPerMinute = v;
    }
    if (share >= NOISY_SHARE_THRESHOLD || peakPerMinute >= NOISY_RATE_PER_MIN) {
      out.push({ event, count, share: Number(share.toFixed(4)), peakPerMinute });
    }
  }
  out.sort((a, b) => b.share - a.share || b.peakPerMinute - a.peakPerMinute);
  return out;
}

function detectMissingCorrelation(events) {
  // Per subsystem: count Info+ events, count those with null correlationId,
  // and report the longest contiguous null streak.
  const bySub = new Map();
  for (const e of events) {
    if (e.level === "Trace" || e.level === "Debug") continue;
    let s = bySub.get(e.subsystem);
    if (!s) {
      s = { subsystem: e.subsystem, total: 0, missing: 0, longestStreak: 0, currentStreak: 0, dominantEvent: null, eventCounts: new Map() };
      bySub.set(e.subsystem, s);
    }
    s.total++;
    if (!e.correlationId) {
      s.missing++;
      s.currentStreak++;
      if (s.currentStreak > s.longestStreak) s.longestStreak = s.currentStreak;
      s.eventCounts.set(e.event, (s.eventCounts.get(e.event) ?? 0) + 1);
    } else {
      s.currentStreak = 0;
    }
  }
  const out = [];
  for (const s of bySub.values()) {
    if (s.missing === 0 || s.longestStreak < MISSING_CORR_STREAK) continue;
    let topEvent = null;
    let topCount = -1;
    for (const [k, v] of s.eventCounts) {
      if (v > topCount) {
        topEvent = k;
        topCount = v;
      }
    }
    out.push({
      subsystem: s.subsystem,
      total: s.total,
      missing: s.missing,
      longestStreak: s.longestStreak,
      dominantEvent: topEvent,
    });
  }
  return out;
}

function detectSuspiciousSequences(events) {
  // For each correlationId, walk events in timestamp order and check the
  // hard-coded invariants. Also collect ordered pairs the user can scan
  // manually as UNVERIFIED.
  const byCorr = new Map();
  for (const e of events) {
    const cid = e.correlationId;
    if (!cid) continue;
    let arr = byCorr.get(cid);
    if (!arr) byCorr.set(cid, (arr = []));
    arr.push(e);
  }
  const violations = [];
  const unverified = [];
  for (const [cid, arr] of byCorr) {
    arr.sort((a, b) => (a.timestamp < b.timestamp ? -1 : 1));
    const seen = new Set();
    for (const e of arr) {
      for (const [before, after] of KNOWN_INVARIANTS) {
        if (e.event === after && !seen.has(before)) {
          violations.push({
            correlationId: cid,
            invariant: `${before} -> ${after}`,
            offendingEvent: e.event,
            timestamp: e.timestamp,
            evidenceRef: `${e.__source.path}:${e.__source.line}`,
          });
        }
      }
      seen.add(e.event);
    }
    // Capture one ordered pair per correlationId for the UNVERIFIED bucket.
    if (arr.length >= 2) {
      unverified.push({
        correlationId: cid,
        ordering: arr.slice(0, 5).map((e) => e.event),
      });
    }
  }
  return { violations, unverified };
}
