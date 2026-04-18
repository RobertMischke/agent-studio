export interface JobInfo {
  id: string;
  title: string;
  state: string;
  priority: string;
  agent: string;
  createdAt: string;
  watchPath: string;
  folderPath: string;
  lastActivity: string;
  totalSizeBytes: number;
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
