# URL Preview diagnostics

URL Preview confirms process, TCP, HTTP, and renderable content readiness before
it reports Running. A spawned process or open port is not sufficient.

## Diagnosis classes

| Diagnosis | Meaning | Operator action |
| --- | --- | --- |
| `not-started` | No listener is reachable and no validated start is in progress. | Start the configured service or open quick setup. |
| `starting` | The process exists and activity-based readiness validation is still running. Recent stdout or stderr output extends the wait. | Watch the live console while the cold build continues. |
| `command-unavailable` | The shell could not find or launch the configured command. | Install the tool or correct the command. |
| `invalid-cwd` | The resolved working directory does not exist. | Select an existing directory in quick setup. Saved rules require absolute paths; only unsaved quick-setup candidates may use paths relative to the project repository. |
| `process-exited` | The process ended before readiness was confirmed. | Expand technical details and inspect the bounded stdout and stderr tails. |
| `port-never-opened` | The process stayed alive, but the configured port never accepted a connection before console silence or the startup limit. | Correct the port or server binding. |
| `timeout` | TCP or HTTP readiness ended after console silence or the absolute startup limit. | Verify the health target and tune the appropriate start-rule bound. |
| `http-error-response` | The readiness target returned HTTP 4xx or 5xx. | Fix the target or server response. |
| `content-not-renderable` | HTTP succeeded, but content was empty, non-renderable, or blocked by `X-Frame-Options` or CSP `frame-ancestors`. | Use a page that returns HTML, adjust frame policy, or open externally. |
| `invalid-configuration` | Required URL or start configuration is missing or malformed. | Complete URL Preview quick setup. |
| `running` | The port opened, HTTP succeeded, and renderable content was returned. | No recovery is needed. |

Technical details contain the command, resolved cwd, configured port, process
creation and exit evidence, bounded output tails, TCP reachability, HTTP status,
content readiness, iframe readiness where known, blocking frame policy, and
timeout state. Terminal startup diagnostics identify `process-exit`,
`silence-timeout`, or `startup-limit` explicitly. Common token, password,
secret, API key, Authorization, and URL
credential patterns are redacted before the response reaches the UI. Output is
tail-bounded to 8 KiB per stream.

## Activity-based startup bounds

`ReadinessTimeoutSeconds` now means console silence. It defaults to 30 seconds.
As long as the owned process is alive and stdout or stderr produced a line
inside that window, URL Preview keeps waiting and streams the current output.
Existing stored values are not rewritten: an existing value of 20 now means 20
seconds without process output.

`StartupTimeoutSeconds` is the independent absolute ceiling. It defaults to 600
seconds and is configurable per start rule even when console output remains
active. The Preview panel stays in `Starting · console active` until readiness
succeeds or one of the three terminal causes is observed: process exit, silence
timeout, or startup-limit exhaustion.

## Quick setup

Open Project Hub, then Settings. URL Preview quick setup is the first section.
It scans package scripts, Angular configuration, and README run instructions.
Select a suggestion or enter the command, working directory, port, preview or
health URL, console-silence timeout, and maximum startup time manually. The source, such as `readme`,
`package-json`, or `manual`, remains visible after save.

Opening Settings from a failed Preview targets that URL's editor directly. If
Preview already found a safe matching suggestion, its start and readiness
fields are prefilled for review without replacing the saved label or preview
URL.

Use **Test setup** before saving. The test uses the same bounded start and
readiness path as Preview, renders the same diagnostic result, and stops the
temporary validation process when the test completes. Preview can apply a safe
matching detected rule directly from a setup failure. Otherwise, save the
reviewed rule in Settings, return to the open Preview, and choose **Retry**. A
full application reload is not required.
