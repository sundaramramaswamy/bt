---
name: bt-build
description: >-
  Fast incremental C++/MSBuild builds using bt. Use when the user wants to
  compile, build, check what's dirty, or query build dependencies in a repo
  that has .vcxproj files. Prefer bt over invoking MSBuild directly for
  inner-loop source changes (.cpp, .h, .idl, .xaml, .appxmanifest).
allowed-tools: shell
---

# bt — Fast Inner-Loop Builds

`bt` reads an MSBuild binary log, builds a file-level dependency graph, and
replays only the dirty compile/link/MIDL/XAML/makepri steps directly — no
MSBuild overhead.

## Before you start

### 1. Check if bt is installed

Run `bt --version`.  If bt is not found, run the `install-bt.ps1` script from
this skill's directory to download and install it from GitHub Releases.

### 2. Check for a binary log

bt requires a binary log from a prior full build.  Search for `*.binlog` files
in the repo (common locations: repo root, build output directories).  If
multiple exist, ask the user which one to use — pass it via `--binlog <path>`.

If no binlog exists, tell the user:

> bt needs a one-time full build with binary logging.  Run:
>
> ```
> msbuild MySolution.sln -bl
> ```
>
> This captures the full dependency graph.  After this, bt handles
> incremental builds in seconds instead of minutes.

Do NOT attempt to run bt without a binlog — it will fail.

## Common workflows

### Build what's stale (most common)

```
bt build                      # mtime-based — build only dirty files
```

### Build only what a specific file affects

```
bt build -f MyFile.cpp        # forward walk from that file
```

### Compile only (skip link)

```
bt build -c                   # compile dirty sources, don't link
bt build -c -f MyFile.cpp     # compile only what this file affects
```

### Dry run — see what would build

```
bt build -n                   # print commands without executing
```

### Watch and build on save

```
bt watch                      # build on file change
bt watch --run .\deploy.ps1   # build + run a command after
```

### Query the dependency graph

```
bt dirty                      # what needs rebuilding right now?
bt dirty MyFile.cpp           # build plan for specific file(s)
bt bins MyHeader.h            # what rebuilds when this header changes?
bt srcs MyApp.dll             # what sources feed into this binary?
bt srcs --headers MyFile.cpp  # upstream sources + all #include headers
```

## When to use bt vs MSBuild

| Scenario | Use |
|----------|-----|
| Edited `.cpp`/`.h`/`.idl`/`.xaml`/`.appxmanifest` | `bt build` |
| Added a new `.cpp` to a `.vcxproj` | `bt build` (inference handles it) |
| Edited resources tracked by makepri | `bt build` |
| First build of a repo | `msbuild -bl` |
| Added/removed WinRT runtime classes | `msbuild -bl` |
| Changed `.props`/`.targets` imports | `msbuild -bl` |
| Need AppX packaging, signing, etc. | `msbuild` |
| Per-file metadata (optimization overrides) | `msbuild -bl` then `bt build` |

## Key options

| Option | Description |
|--------|-------------|
| `--binlog <path>` | Path to binary log (default: `msbuild.binlog`) |
| `-j <N>` | Max parallel commands (default: CPU cores) |
| `-n, --dry-run` | Print commands without executing |
| `-c, --compile-only` | Compile only — skip link/lib |
| `--debounce <ms>` | Debounce delay for `watch` (default: 300) |
| `--run <cmd>` | Run command after each successful `watch` rebuild |

## Important notes

- bt replays `cl.exe`/`link.exe` directly — it does NOT invoke MSBuild.
- The first run after a binlog change parses and caches the graph (~700ms).
  Subsequent runs use the cache (~20ms).
- New sources added to `.vcxproj` are inferred automatically — no full
  rebuild needed.  bt warns if per-file metadata can't be mirrored exactly.
- Headers not yet in the tlog (newly added `#include`s) won't trigger
  rebuilds until the next `msbuild -bl`.
