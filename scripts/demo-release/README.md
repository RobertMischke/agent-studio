# Public demo release contract

This directory implements W34 slice S5. It turns the committed pinned seed,
an immutable product image digest, the S3 replay trace, and the S2 deployment
policy into one versioned public-demo release.

The builder generates the datastore twice in separate empty roots, compares
their recursive manifests, scans text and generated PNGs, and writes
`demo-seed-scrub-report.json`. Private source-name terms are supplied through
an unshipped file; only their count and digest enter the report. Raw matches
never enter a bundle or result artifact.

An automated pass creates a candidate bundle. Deployment additionally requires
a human review JSON document bound to both the generated content and manifest
digests:

```json
{
  "decision": "approved",
  "reviewer": "Reviewer identity",
  "reviewedAt": "2026-08-17T09:00:00.000Z",
  "reviewedContentDigest": "<digest from the candidate scrub report>",
  "reviewedManifestDigest": "<digest from the candidate scrub report>",
  "exceptions": []
}
```

Build and verify a release:

```sh
node scripts/demo-release/build-demo-release.mjs \
  --release 2026.08.1 \
  --output-dir /safe/output/2026.08.1 \
  --product-image sha256:<immutable-image-digest> \
  --replay-trace /approved/replay-trace.json \
  --deployment-policy /approved/public-demo-policy.json \
  --source-terms-file /private/source-terms.txt \
  --human-review /approved/human-review.json

node scripts/demo-release/verify-demo-release.mjs \
  --bundle /safe/output/2026.08.1/agent-studio-demo-2026.08.1.tar.gz \
  --extract-to /safe/empty/verification-root \
  --expected-bundle-digest sha256:<digest from the approved release record> \
  --require-approved
```

The deployment scripts under `deploy/demo-runtime/` extract every reset into a
new release directory. Reset requires the operator-pinned archive digest, keeps
a read-only pristine release payload beside a fresh writable runtime, and uses
that payload rather than runtime drift for rollback. Required start, browse-and-denial probe, and atomic
switch hooks run before the `current` link changes. Failures retain the prior
healthy runtime and remove only the isolated candidate. Successful cuts retain
only current and previous, discarding older writable runtime drift. The systemd timer runs
the same replacement service at boot and every six hours; operators use the
same service for deploy and on-demand replacement.

S5 does not provision a host, edge, identity, firewall, or DNS. Those are S6.
