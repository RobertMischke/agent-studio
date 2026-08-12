# Quality Studio analysis core package

This repository-local NuGet source contains the first in-process package used by
the Agent Studio quality-analysis pipeline slice. It is a temporary durability
bridge until Quality Studio publishes the package through an approved feed.

`AgentOrchestrator.CodeQuality.0.1.0-agt2655.nupkg` was built from Quality Studio
`main` at `59941d8e93734a4840a63c407597f3925bbbb1d6` with these additive deliveries:

- QS-90 named rule library: `9e9461f1025afa18c6082a3f82f5649bfdcdf4e3`
- QS-91 analysis core package: `1be25369af83c9aff6548c0382bce9b63c6dd9a0`
- AGT integration registration: the QS-90 `quality-rules` sensor is registered in
  the QS-91 named analysis facade.

The combined package source commit recorded in the NuGet metadata is
`96a9801a0a28dc8bc4324d759cb10ec8bf630232`.

Package SHA-256:
`8b91d87c4e9487437c157f017bf228d97a9f11fbd9d67cbc54b1774d665e41ef`.

The rule documents remain owned by Quality Studio and are embedded in the
package. Agent Studio does not carry a second copy of their source or wording.
