import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';

const markerIndex = process.argv.indexOf('--marker');
const delayIndex = process.argv.indexOf('--delay-ms');
const marker = markerIndex >= 0 ? process.argv[markerIndex + 1] : '';
const delayMs = delayIndex >= 0 ? Number(process.argv[delayIndex + 1]) : 0;

if (!marker || !Number.isInteger(delayMs) || delayMs < 1000) {
  throw new Error('A marker path and a delay of at least 1000 ms are required.');
}

const child = spawn(process.execPath, [
  '-e',
  `setTimeout(() => process.exit(0), ${delayMs})`
], {
  stdio: 'ignore'
});

await writeFile(marker, `${JSON.stringify({
  parentPid: process.pid,
  childPid: child.pid
})}\n`);

await new Promise(resolve => setTimeout(resolve, delayMs));

