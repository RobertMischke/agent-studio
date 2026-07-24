# AGT-2194 management fix-round verification

Date: 2026-07-24

## Owner-command authorization

Regression:
`ManagementApiTests.WhitespacePaddedOwnerCommand_IsRejectedForOperatorBeforeAudit`

The request uses the operator-attributed local client and submits
` runner-credential-rotate ` with surrounding whitespace. The management
service normalizes the command once, checks the owner-only command set against
that normalized value, and uses the same value for dispatch and audit. The
test verifies HTTP 403 `owner-required` and verifies that no audit file was
created for the rejected command.

Focused result:

- Test run successful.
- Total: 9.
- Passed: 9.
- Failed: 0.

## Non-development backup and restore verification

Regression:
`ManagementApiTests.BackupCreate_VerifiesRealArchive_OutsideDataDirectory`

The verification hosted the backend with:

- Hosting environment: `Production`.
- Server data directory:
  `/var/tmp/agent-studio-server-data/AGT-2194/server-a47f16a477a94646ba545dd68a69e399`.
- Backup directory: the configured sibling directory outside the server data
  root.

The test entered maintenance through the management API, created a real ZIP
backup through `POST /api/v1/management/commands`, verified that its durable
checksum manifest exists, then ran `restore-verify` through the same API. It
asserted a successful restore verification and an empty verification staging
area after cleanup. The isolated server and backup directories were removed
by the test fixture after verification.

Focused result:

- Test run successful.
- Total: 1.
- Passed: 1.
- Failed: 0.
- Duration: 3.4746 seconds.
