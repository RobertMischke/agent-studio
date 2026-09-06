# Deterministic runner image used only by scripts/scenario.sh. It keeps the
# production agent-host code and replaces subscription CLIs with a fixed stub.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /opt/agent-host
COPY --from=build /app ./
COPY testsupport/scenario/fake-agent-cli.sh /usr/local/bin/codex
RUN chmod 0755 /usr/local/bin/codex \
 && git config --system safe.directory '*'
ENV RUNNER_CLI_BIN=/usr/local/bin/codex \
    RUNNER_CODEX_CLI_BIN=/usr/local/bin/codex \
    RUNNER_EXEC_ENGINE=legacy \
    RUNNER_WORKDIR=/var/lib/agent-host/runner-work \
    RUNNER_STATE_DIR=/var/lib/agent-host/state
VOLUME ["/var/lib/agent-host"]
ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]
