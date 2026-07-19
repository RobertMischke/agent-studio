/**
 * Wire shape for {@code GET /api/runtime/{project}/events}. Mirrors
 * {@code RuntimeEventListResponse} on the backend; the JSON serialiser uses
 * web defaults (camelCase) so field names below match the on-disk schema in
 * {@code docs/app/schemas/product-runtime-event.schema.json}.
 */

/** Severity levels accepted by the schema. */
export const PRODUCT_RUNTIME_LEVELS = [
  'Trace',
  'Debug',
  'Info',
  'Warn',
  'Error',
  'Fatal',
] as const;
export type ProductRuntimeLevel = typeof PRODUCT_RUNTIME_LEVELS[number];

/** Outcome labels for events that wrap a completed operation. */
export const PRODUCT_RUNTIME_STATUSES = [
  'Ok',
  'Failed',
  'Cancelled',
  'Timeout',
  'Skipped',
] as const;
export type ProductRuntimeStatus = typeof PRODUCT_RUNTIME_STATUSES[number];

export interface ProductRuntimeEventDuration {
  ms: number;
  startedAt?: string | null;
}

export interface ProductRuntimeEventError {
  type?: string | null;
  message: string;
  stack?: string | null;
  code?: string | null;
  retryable?: boolean | null;
}

export interface ProductRuntimeEvent {
  schemaVersion: 1;
  /** UTC ISO 8601 timestamp ending in Z. */
  timestamp: string;
  level: ProductRuntimeLevel;
  /** Stable kebab-case event name, optionally namespaced with dots. */
  event: string;
  subsystem: string;
  operation?: string | null;
  correlationId?: string | null;
  traceId?: string | null;
  spanId?: string | null;
  project?: string | null;
  jobId?: string | null;
  runId?: string | null;
  taskId?: string | null;
  duration?: ProductRuntimeEventDuration | null;
  status?: ProductRuntimeStatus | null;
  error?: ProductRuntimeEventError | null;
  tags?: string[];
  payload?: Record<string, unknown> | null;
}

/** Parse warning surfaced when a JSONL line could not be turned into a valid event. */
export interface RuntimeEventParseWarning {
  sourcePath: string;
  lineNumber: number;
  reason: string;
  rawLine: string;
}

export interface RuntimeEventListResponse {
  project: string;
  events: ProductRuntimeEvent[];
  warnings: RuntimeEventParseWarning[];
}
