# Quality Studio package source

This directory is an immutable local NuGet source for the first Agent Studio
Quality Studio pipeline slice. It prevents the runtime from depending on an
unpublished package feed while keeping all rule implementations inside the
Quality Studio-owned package.

- Package: `AgentOrchestrator.CodeQuality` `0.1.0-agt2655`
- Quality Studio source commit: `96a9801a0a28dc8bc4324d759cb10ec8bf630232`
- Coordinated deliveries: QS-90 rule library and QS-91 analysis core package
- SHA-256: `8b91d87c4e9487437c157f017bf228d97a9f11fbd9d67cbc54b1774d665e41ef`

Replace this source only with a published package carrying the same or a newer
reviewed contract. Do not copy Quality Studio rule content into Agent Studio.
