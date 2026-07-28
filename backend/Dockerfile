# Agent Studio orchestrator API (the current monolith: board API, pipeline,
# review plane, local project registry).
# Build context is the repository root: docker build -f backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish backend/OrchestratorApi.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl: Compose uses the public health endpoint as its readiness contract.
# git: GitService shells out for merges, worktrees, and integration pushes.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl git openssh-client \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /app
COPY --from=build /app ./
# Defense in depth: machine-local overrides must never reach the image even if
# the build context leaks them past .dockerignore.
RUN rm -f /app/appsettings.Local.json /app/appsettings.Development.json
# Runtime prompt material read via AgentRules:CorePath (relative to content root).
COPY agent-rules/ ./agent-rules/
ENV ASPNETCORE_URLS=http://0.0.0.0:5031 \
    TaskRepository=/data/workspace
# /data/workspace: task repository (cards, metadata). /data/projects: project checkouts.
VOLUME ["/data/workspace", "/data/projects"]
EXPOSE 5031
ENTRYPOINT ["dotnet", "OrchestratorApi.dll"]
