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
  reviewMarkdown: string | null;
  metrics: JobMetrics | null;
  artifacts: string[];
  screenshots: string[];
  logs: string[];
  timeline: JobTimelineEntry[];
}

export interface JobMetrics {
  durationMinutes: number;
  filesChanged: number;
  linesAdded: number;
  linesRemoved: number;
  screenshotsProduced: number;
  acceptedFirstTry: boolean;
  reworkCount: number;
  buildSuccess: boolean | null;
  testSuccess: boolean | null;
}

export interface JobTimelineEntry {
  timestamp: string;
  event: string;
  detail: string | null;
}

export interface GroupedJobs {
  active: JobInfo[];
  review: JobInfo[];
  completed: JobInfo[];
  failed: JobInfo[];
  idle: JobInfo[];
}
