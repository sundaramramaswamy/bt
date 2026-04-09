# Privacy & Telemetry

`bt` collects anonymous usage telemetry to help improve the tool.

## What's collected

- Command name (e.g. `build`, `dirty`, `watch`)
- Flag names only — no values (e.g. `-j`, `--dry-run`)
- bt version string
- Success or failure (boolean)
- Invocation count (for `watch` — per-rebuild)

## What's NOT collected

- File paths, repo names, or directory structures
- Usernames, machine names, or IP-derived identity
- Source code, build output, or command-line values
- Environment variables or credentials

## Where it goes

Data is sent to Azure Application Insights via a fire-and-forget `curl.exe`
POST.  The instrumentation key is write-only (ingest, not query).

## Opting out

Set the environment variable `BT_NO_TELEMETRY` to any non-empty value:

```powershell
# PowerShell (persistent)
[Environment]::SetEnvironmentVariable('BT_NO_TELEMETRY', '1', 'User')

# cmd (persistent)
setx BT_NO_TELEMETRY 1

# bash (add to ~/.bashrc)
export BT_NO_TELEMETRY=1
```

When set, telemetry is completely disabled — no data is sent.
