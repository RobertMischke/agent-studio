#!/usr/bin/env node
import net from 'node:net';
import http from 'node:http';

const targetHost = required('FAULT_PROXY_TARGET_HOST');
const targetPort = positiveInt('FAULT_PROXY_TARGET_PORT');
const controlPort = positiveInt('FAULT_PROXY_CONTROL_PORT');
const links = new Map([
  ['runner', createLink('runner', positiveInt('FAULT_PROXY_RUNNER_PORT'))],
  ['studio', createLink('studio', positiveInt('FAULT_PROXY_STUDIO_PORT'))]
]);

for (const link of links.values()) {
  link.server.listen(link.port, '0.0.0.0');
}

const control = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? '/', 'http://127.0.0.1');
    if (request.method === 'GET' && url.pathname === '/healthz') {
      return json(response, 200, snapshot());
    }
    const match = /^\/links\/(runner|studio)\/(partition|heal)$/.exec(url.pathname);
    if (request.method === 'POST' && match) {
      const link = links.get(match[1]);
      link.partitioned = match[2] === 'partition';
      if (link.partitioned) {
        for (const socket of link.sockets) socket.destroy();
        link.sockets.clear();
      }
      return json(response, 200, snapshot());
    }
    return json(response, 404, { error: 'not-found' });
  } catch (error) {
    return json(response, 500, { error: String(error?.message ?? error) });
  }
});
control.listen(controlPort, '0.0.0.0');

function createLink(name, port) {
  const link = {
    name,
    port,
    partitioned: false,
    accepted: 0,
    rejected: 0,
    sockets: new Set(),
    server: null
  };
  link.server = net.createServer(client => {
    if (link.partitioned) {
      link.rejected++;
      client.destroy();
      return;
    }
    link.accepted++;
    const upstream = net.createConnection({ host: targetHost, port: targetPort });
    link.sockets.add(client);
    link.sockets.add(upstream);
    const close = () => {
      link.sockets.delete(client);
      link.sockets.delete(upstream);
      client.destroy();
      upstream.destroy();
    };
    client.on('error', close);
    upstream.on('error', close);
    client.on('close', close);
    upstream.on('close', close);
    client.pipe(upstream);
    upstream.pipe(client);
  });
  return link;
}

function snapshot() {
  return {
    status: 'ready',
    target: `${targetHost}:${targetPort}`,
    links: Object.fromEntries([...links].map(([name, link]) => [name, {
      listenPort: link.port,
      partitioned: link.partitioned,
      accepted: link.accepted,
      rejected: link.rejected,
      openSockets: link.sockets.size
    }]))
  };
}

function positiveInt(name) {
  const value = Number(required(name));
  if (!Number.isInteger(value) || value < 1 || value > 65535) {
    throw new Error(`${name} must be a valid TCP port`);
  }
  return value;
}

function required(name) {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`${name} is required`);
  return value;
}

function json(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(body)
  });
  response.end(body);
}

function shutdown() {
  control.close();
  for (const link of links.values()) {
    for (const socket of link.sockets) socket.destroy();
    link.server.close();
  }
}
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
