# Agent Studio frontend: production Angular build served statically by Caddy,
# API calls reverse-proxied to the orchestrator API.
# Build context is the repository root: docker build -f frontend/Dockerfile .
FROM node:22-alpine AS build
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
# Angular's application builder emits dist/frontend/browser; older layouts
# emit dist/frontend directly - normalise both into /studio-dist.
RUN npx ng build frontend \
 && (test -d dist/frontend/browser && mv dist/frontend/browser /studio-dist || mv dist/frontend /studio-dist)

FROM caddy:2-alpine
COPY deploy/compose/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /studio-dist /srv/studio
EXPOSE 80
