FROM node:22-alpine
ARG HARNESS_REVISION=unknown
ARG HARNESS_IDENTITY=unset
LABEL org.opencontainers.image.revision="${HARNESS_REVISION}" \
      com.agentstudio.remote-harness.identity="${HARNESS_IDENTITY}"
WORKDIR /harness
COPY tools/remote-test-suite/compose-fault-proxy.mjs ./
ENTRYPOINT ["node"]
