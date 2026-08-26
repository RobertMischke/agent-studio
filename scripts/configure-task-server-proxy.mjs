#!/usr/bin/env node

import { readFile, rename, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';

const [configPathArg, baseUrlArg, authTokenFileArg = ''] = process.argv.slice(2);
if (!configPathArg || !baseUrlArg) {
  console.error('Usage: configure-task-server-proxy.mjs <appsettings.Local.json> <base-url> [auth-token-file]');
  process.exit(2);
}

const baseUrl = new URL(baseUrlArg);
if (!['http:', 'https:'].includes(baseUrl.protocol)) {
  throw new Error('Task Server base URL must use HTTP or HTTPS.');
}

const configPath = resolve(configPathArg);
const raw = await readFile(configPath, 'utf8');
const config = JSON.parse(raw);
config.TaskServer ??= {};
config.TaskServer.BaseUrl = baseUrl.toString().replace(/\/$/, '');
if (authTokenFileArg) {
  config.TaskServer.AuthTokenFile = authTokenFileArg;
  delete config.TaskServer.AuthToken;
}

const temporaryPath = resolve(dirname(configPath), `.appsettings.Local.${process.pid}.tmp`);
await writeFile(temporaryPath, `${JSON.stringify(config, null, 2)}\n`, { mode: 0o600 });
await rename(temporaryPath, configPath);
console.log(`Configured TaskServer:BaseUrl=${config.TaskServer.BaseUrl}`);
