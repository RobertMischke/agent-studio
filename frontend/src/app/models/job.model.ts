export interface JobInfo {
  id: string;
  jobKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  totalSizeBytes: number;
  execution: CliExecution | null;
}

export interface JobDetail {
  info: JobInfo;
  promptMarkdown: string | null;
  statusMarkdown: string | null;
  log: JobLogEntry[];
}

export interface JobLogEntry {
  timestamp: string;
  event: string;
  detail: string | null;
}

export interface GroupedJobs {
  preparation: JobInfo[];
  ready: JobInfo[];
  progress: JobInfo[];
  review: JobInfo[];
  completed: JobInfo[];
}

export interface CreateJobRequest {
  id?: string;
  title: string;
  order?: number;
  agent: string;
  watchPath: string;
  promptMarkdown?: string;
}

export interface JobOrderItem {
  jobId: string;
  watchPath: string;
}

export interface WatchPathEntry {
  name: string;
  path: string;
  rootPath: string;
}

export interface CliExecution {
  jobId: string;
  jobKey: string;
  processId: number;
  startedAt: string;
  status: string;
  exitCode: number | null;
  durationSeconds: number | null;
}

export interface CliOutputLine {
  timestamp: string;
  stream: string;
  text: string;
}

export interface ProjectRunnerStatus {
  projectName: string;
  mode: string;
  activeJobId: string | null;
  activeExecution: CliExecution | null;
  queuedJobIds: string[];
}

export interface RunnerStatus {
  projects: { [key: string]: ProjectRunnerStatus };
}

export interface CliSettings {
  path: string;
  available: boolean;
  version: string | null;
  hasToken: boolean;
}
