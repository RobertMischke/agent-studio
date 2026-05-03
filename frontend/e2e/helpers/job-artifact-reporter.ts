import fs from 'fs';
import path from 'path';
import { Reporter, TestCase, TestResult } from '@playwright/test';

/**
 * Custom Playwright reporter that harvests test artifacts (screenshots, videos, traces)
 * into a job folder's results directory when JOB_RESULTS_DIR env var is set.
 *
 * This reporter activates only when JOB_RESULTS_DIR is defined and copies artifacts
 * from the test-results/ folder into <JOB_RESULTS_DIR>/playwright/<spec-name>/ with
 * a summary index.json listing all copied files and their test status.
 *
 * Designed for use by the agent task orchestrator to make Playwright artifacts
 * available in the job protocol alongside the activity log.
 */
export class JobArtifactReporter implements Reporter {
  private readonly jobResultsDir = process.env.JOB_RESULTS_DIR;
  private readonly testResultsDir = 'frontend/e2e/test-results';
  private readonly artifacts = new Map<string, { status: string; files: string[] }>();

  onTestEnd(test: TestCase, result: TestResult): void {
    if (!this.jobResultsDir) return;

    const specName = test.titlePath()[0] || 'unknown';
    const testName = test.title;
    const status =
      result.status === 'passed'
        ? '✓'
        : result.status === 'failed'
          ? '✗'
          : result.status === 'skipped'
            ? '⊘'
            : '?';

    if (!this.artifacts.has(specName)) {
      this.artifacts.set(specName, { status, files: [] });
    }

    const specArtifacts = this.artifacts.get(specName)!;

    // Copy artifacts from test-results/<spec>/ subfolder to results/playwright/<spec>/
    const testResultsSpecDir = path.join(this.testResultsDir, specName.replace(/\s+/g, '-').toLowerCase());

    if (fs.existsSync(testResultsSpecDir)) {
      const destDir = path.join(this.jobResultsDir, 'playwright', specName);
      fs.mkdirSync(destDir, { recursive: true });

      // Copy PNG screenshots
      const files = fs.readdirSync(testResultsSpecDir);
      for (const file of files) {
        const srcPath = path.join(testResultsSpecDir, file);
        if (fs.statSync(srcPath).isFile() && /\.(png|webm|zip)$/.test(file)) {
          const destPath = path.join(destDir, file);
          fs.copyFileSync(srcPath, destPath);
          specArtifacts.files.push(file);
        }
      }
    }

    // Also copy from result.attachments if available
    if (result.attachments) {
      for (const attachment of result.attachments) {
        if (attachment.path && fs.existsSync(attachment.path) && /\.(png|webm|zip)$/.test(attachment.path)) {
          const destDir = path.join(this.jobResultsDir, 'playwright', specName);
          fs.mkdirSync(destDir, { recursive: true });

          const fileName = path.basename(attachment.path);
          const destPath = path.join(destDir, fileName);
          fs.copyFileSync(attachment.path, destPath);
          if (!specArtifacts.files.includes(fileName)) {
            specArtifacts.files.push(fileName);
          }
        }
      }
    }
  }

  onEnd(): void {
    if (!this.jobResultsDir || this.artifacts.size === 0) return;

    // Write index.json summary
    const indexPath = path.join(this.jobResultsDir, 'playwright', 'index.json');
    const indexDir = path.dirname(indexPath);
    fs.mkdirSync(indexDir, { recursive: true });

    const summary = {
      timestamp: new Date().toISOString(),
      specs: Object.fromEntries(this.artifacts)
    };

    fs.writeFileSync(indexPath, JSON.stringify(summary, null, 2));
  }
}

export default JobArtifactReporter;
