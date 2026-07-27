# agent-host (coding/review runner) with the coding CLIs baked in. Running the
# CLI inside this container is the container-default execution boundary: the
# container is the isolation, the CLI itself runs unsandboxed within it.
# Build context is the repository root: docker build -f runner/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /app /p:UseAppHost=false

# SDK base image on purpose: coding agents build and test the projects they
# work on, so the execution container needs the full toolchain.
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git openssh-client curl ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install --global @openai/codex @anthropic-ai/claude-code \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /opt/agent-host
COPY --from=build /app ./
# Variable names follow deploy/release/agent-host/runner.env.template; identity,
# tokens, and git remotes come from the compose env file, never from the image.
ENV RUNNER_SERVER_URL=http://orchestrator-api:5031 \
    RUNNER_ROLE=coding \
    RUNNER_WORKDIR=/var/lib/agent-host/runner-work \
    RUNNER_STATE_DIR=/var/lib/agent-host/state
VOLUME ["/var/lib/agent-host"]
ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]
