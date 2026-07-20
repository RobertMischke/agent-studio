# Runbook rehearsal evidence

The certificate-renewal and Runner-secret-rotation runbooks in
[networked-task-server.md](../setup/networked-task-server.md) are executable, not
just prose. This page records how each one is exercised and the evidence a
rehearsal produces. Re-run any time before a real incident; append the fresh
output here or to the operator evidence log.

## What exercises each runbook

| Runbook | Executable rehearsal | Reproduced by |
|---|---|---|
| Certificate renewal | `deploy/networked/rehearse-runbooks.sh cert-selftest` (self-contained, self-signed, no network) and `cert-check <domain>` (live) | `RunbookRehearsalTests.Certificate_renewal_runbook_invariants_hold` |
| Runner secret rotation | `deploy/networked/rehearse-runbooks.sh rotate <origin> <runner-id>` (live, owner session) | `RunbookRehearsalTests.Runner_secret_rotation_runbook_is_exercised` |

The shell harness drives the operator-visible API workflow and writes a
**secret-free** evidence log. The deterministic tests pin the fail-closed
invariants — old secret refused after revoke, new secret still claims, identity
revoke closes everything, renewed certificate serial rotates and expiry
extends — so the runbooks stay exercised in CI without exposing any secret.

## Certificate-renewal self-test (captured run)

`EVIDENCE_LOG=... deploy/networked/rehearse-runbooks.sh cert-selftest`, executed
2026-07-20 (self-signed test certificate; no production certificate involved —
the certificate *is* the evidence):

```text
cert-selftest: begin
cert-selftest: current  serial=1A02A48C65D5CEE0FC2ADD5D7833665979C8C6EB notAfter='Aug 19 14:55:52 2026 GMT'
cert-selftest: renewed  serial=30BDBC10E8A1CB2393A59D5DF5090361EBBB2AE2 notAfter='Oct 18 14:55:52 2026 GMT'
cert-selftest: renewed expiry is later than current: OK
cert-selftest: current cert has 29d remaining (>= 21d threshold)
cert-selftest: PASS — renewal invariants exercised (serial rotates, expiry extends, cert parses)
```

Invariants asserted: the renewal issues a new serial, the renewed expiry is
strictly later than the current one, both certificates parse, and the 21-day
pre-expiry alert threshold is computed.

## Runner-secret-rotation rehearsal (invariants)

`RunbookRehearsalTests.Runner_secret_rotation_runbook_is_exercised` walks the
runbook against the real security store and asserts, in order:

1. A runner is enrolled and its first credential authenticates.
2. An overlapping credential is minted (`POST /api/auth/runners/{id}/credentials`);
   both old and new credentials authenticate during the overlap window.
3. The old credential is revoked (`DELETE .../credentials/{oldId}`); the old
   secret now fails closed while the new one still authenticates.
4. For host loss, the whole identity is revoked (`DELETE /api/auth/runners/{id}`);
   every credential fails closed without waiting for expiry.
5. The captured evidence names credential ids and outcomes but contains neither
   secret (asserted).

Real TLS transport for the same credential path — a real Runner connecting
outbound over HTTPS with its service credential, cleartext rejected — is proven
separately by `RealTlsTransportTests` against a real Kestrel loopback listener
with a self-signed certificate.
