# Replay-only Runner for the public demo instance (AGT-W34 slice S3).
#
# Build from the repository root:
#   docker build -f demo-replay-runner/Dockerfile -t demo-replay-runner .
#
# The image deliberately contains no coding CLI, no Node.js, no Git, no
# repository checkout, no Docker socket, and no credential. The runtime base
# image is the plain .NET runtime, not the SDK, so nothing here can build,
# clone, or execute project code. The replay credential and the trace are
# mounted read-only at deploy time and never baked into a layer.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish demo-replay-runner/DemoReplayRunner.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /opt/demo-replay
COPY --from=build /app ./

# Unprivileged service account. The process owns nothing writable in the image.
RUN useradd --system --create-home --shell /usr/sbin/nologin demo-replay \
    && mkdir -p /opt/demo-replay/trace \
    && chown -R root:root /opt/demo-replay
USER demo-replay

ENV DEMO_REPLAY_SERVER_URL=http://orchestrator-api:5031 \
    DEMO_REPLAY_TRACE_FILE=/opt/demo-replay/trace/replay-trace.json \
    DEMO_REPLAY_RUNNER_ID=demo-runner-replay \
    DOTNET_EnableDiagnostics=0

# Egress is default-deny at the network layer on the demo VM. The process
# enforces the same allowlist internally: one origin, two paths.
HEALTHCHECK --interval=60s --timeout=10s --start-period=15s --retries=3 \
    CMD ["dotnet", "/opt/demo-replay/demo-replay-runner.dll", "--health-check"]

ENTRYPOINT ["dotnet", "/opt/demo-replay/demo-replay-runner.dll"]
