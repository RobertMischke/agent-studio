import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { homedir } from 'node:os';

const root = resolve(import.meta.dirname, '..');
const run = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
const tag = process.env.AGENT_STUDIO_RELEASE_TAG || run('describe', '--tags', '--exact-match', 'HEAD');
if (!/^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(tag)) throw new Error('Stable builds require an exact immutable vMAJOR.MINOR.PATCH tag.');
const dirty = run('status', '--porcelain').length > 0;
if (dirty && process.env.AGENT_STUDIO_ALLOW_DIRTY !== '1') throw new Error('Stable release builds must use a clean checkout.');
const lock = JSON.parse(readFileSync(resolve(root, 'frontend/package-lock.json'), 'utf8'));
const chat = lock.packages?.['node_modules/coding-agent-chat'];
if (!chat?.version || !chat?.integrity) throw new Error('coding-agent-chat must be a versioned, integrity-pinned package in package-lock.json; local dist dependencies are not releasable.');
const project = readFileSync(resolve(root, 'backend/OrchestratorApi.csproj'), 'utf8');
const carVersion = project.match(/PackageReference Include="CodingAgentRunner" Version="([^"]+)"/)?.[1];
if (!carVersion) throw new Error('CodingAgentRunner must be version-pinned.');
const packagesRoot = process.env.NUGET_PACKAGES || resolve(homedir(), '.nuget/packages');
const carHashFile = resolve(packagesRoot, 'codingagentrunner', carVersion, `codingagentrunner.${carVersion}.nupkg.sha512`);
const carIntegrity = readFileSync(carHashFile, 'utf8').trim();
const manifest = {
  schemaVersion: 1, appTag: tag, appVersion: tag.slice(1), commit: run('rev-parse', 'HEAD'), dirty,
  builtAt: new Date().toISOString(),
  codingAgentRunner: { name: 'CodingAgentRunner', version: carVersion, tag: `v${carVersion}`, commit: null, integrity: `sha512-${carIntegrity}`, source: 'nuget-lock' },
  codingAgentChat: { name: 'coding-agent-chat', version: chat.version, tag: `v${chat.version}`, commit: null, integrity: chat.integrity, source: 'npm-lock' }
};
writeFileSync(resolve(root, 'release-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
console.log(`release-manifest.json: ${tag} ${manifest.commit}`);
