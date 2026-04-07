# bt

**Fast incremental builds for MSBuild/C++ projects.**

Edit a `.cpp`, rebuild in seconds — not minutes.  `bt` reads MSBuild's binary
log, builds an exact file-level dependency graph, and replays only the dirty
compile/link/copy steps directly.  No MSBuild overhead on the inner loop.

```
bt dirty                      # what needs rebuilding? (mtime-based)
bt build                      # rebuild only what's stale
bt watch --run .\deploy.ps1   # watch sources, rebuild on save, deploy
bt bins MyHeader.h            # what rebuilds when this header changes?
bt srcs MyApp.dll             # what sources feed into this binary?
bt graph -f MyHeader.h        # Graphviz DOT subgraph around a file
```

## Getting started

``` powershell
# One-time: full solution build with binary logging
msbuild MySolution.sln -bl

# Query
bt bins MyHeader.h            # downstream — what rebuilds
bt srcs MyApp.dll             # upstream — what feeds in
bt srcs --headers MyFile.cpp  # upstream + all #include headers

# Build
bt dirty                      # build plan from file timestamps (mtime)
bt dirty MyFile.cpp           # build plan for specific file(s)
bt build                      # rebuild only what's stale
bt build -f MyFile.cpp        # rebuild only what this file affects
bt build -c MyFile.cpp        # compile only — skip link/lib
bt build -n                   # dry-run — show commands without executing
bt watch                      # watch sources, rebuild on save
bt watch --run .\deploy.ps1   # watch + run command after each build

# Tools
bt compiledb                  # generate compile_commands.json for clangd
bt graph -p MyProject         # DOT graph filtered to one project
bt graph -f MyFile.cpp        # DOT subgraph around one file
bt graph -f MyFile.cpp --headers  # include #include edges
```

The graph is cached to `.bt/<name>.fb` (FlatBuffers) — subsequent runs skip
binlog parsing unless the binlog changes.

## Commands

| Command | Description |
|---------|-------------|
| `bins <files>` | Downstream dependency tree — what rebuilds when `<file>` changes |
| `srcs <files>` | Upstream dependency tree — what sources feed into `<file>` (`--headers` for includes) |
| `dirty [files]` | Topo-sorted build plan (default: mtime-based; explicit files override) |
| `build [files]` | Execute dirty commands in parallel waves (`-j N`, `--dry-run`) |
| `watch` | Watch sources and rebuild on change (`--debounce`, `--run <cmd>`) |
| `update` | Check for and install updates from GitHub (`--check` for dry run) |
| `compiledb` | Generate `compile_commands.json` for clangd / clang-tidy |
| `cache` | Parse binlog and cache dependency graph |
| `graph` | Emit Graphviz DOT graph (pipe to `dot`, `d2`, etc.) |

### Graph filters

```
bt graph -f ButtonTest.xaml           # subgraph reachable from/to that file
bt graph -f ButtonTest.xaml --headers # include #include edges
bt graph -p XaBenchPack               # only nodes from that project
bt graph -p XaBench -f main.cpp       # combine (AND)
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--binlog <path>` | `msbuild.binlog` | Path to binary log (also tries `msbuild_debug.binlog`, `msbuild_release.binlog`) |
| `--color <mode>` | `auto` | ANSI colours: `auto`, `always`, `never` |
| `--version` | — | Print version and exit |
| `-j <N>` | CPU cores | Max parallel commands for `build` |
| `-n, --dry-run` | — | Print commands without executing (`build` only) |
| `-c, --compile-only` | — | Compile only — skip link/lib (`build` only) |
| `--headers` | — | Include `#include` edges (`graph -f`, `srcs`) |
| `--debounce <ms>` | `300` | Debounce delay before rebuilding (`watch` only) |
| `--run <cmd>` | — | Run a command after each successful rebuild (`watch` only) |
| `-o <path>` | `compile_commands.json` | Output file for `compiledb` |

## What's in the graph

`bt` models builds as a **command-based DAG**: `File → Command → File`.

| Task | What it captures |
|------|-----------------|
| CL | `.cpp` → `.obj` (1:1 per source) |
| Link | `.obj` → `.exe` / `.dll` |
| Lib | `.obj` → `.lib` |
| MIDL | `.idl` → `.winmd` |
| CompileXaml | `.xaml` → `.xbf` (plus cppwinrt `.g.h`/`.g.cpp` conventions) |
| mdmerge | Unmerged `.winmd` → Merged `.winmd` |
| makepri | Resources → `.pri` |
| Copy | Payload assembly into AppX layout |
| AppxManifest | `.appxmanifest` → `AppxManifest.xml` |
| AppxRecipe | Payload → `.build.appxrecipe` |
| `#include` (tlog) | `.h` → `.cpp` (precise per-source, from CL.read.1.tlog) |

## How it works

1. **Parse** — Reads `msbuild.binlog` via [MSBuild Structured Logger](https://github.com/KirillOsenkov/MSBuildStructuredLog). Each MSBuild task becomes a `CommandNode` with typed inputs and outputs.

2. **Tlog enrichment** — If CL tracker logs (`.tlog`) exist in intermediate dirs, `bt` reads them to wire precise `#include` edges: `header.h → [#include] → source.cpp`. Falls back to conservative `ClInclude` if tlogs are missing.

3. **Cache** — The graph serializes to `.bt/<name>.fb` (FlatBuffers with a sorted string table and integer indices). Invalidated when the binlog's timestamp changes.

4. **Query** — `bins`/`srcs` walk the graph forward/backward, printing a tree. `dirty` computes a topo-sorted build plan using file timestamps (like `make`/`ninja`). `graph` emits DOT for visualization.

5. **Build** — `build` executes dirty commands in parallel, invoking `cl.exe`, `link.exe`, etc. directly — no MSBuild overhead. Environment variables (`INCLUDE`, `LIB`, `PATH`, etc.) are captured from the binlog's `SetEnv` tasks and replayed per-project.

6. **Inference** — On every run, `bt` compares each project file's mtime against the binlog's. If a `.vcxproj` (or imported `.vcxitems`) is newer, `bt` parses it for `<ClCompile>` sources not yet in the graph and synthesises compile/link commands by mirroring flags from a peer source in the same project. This lets you add a `.cpp` to a project and immediately `bt build` — no full MSBuild run required. A warning is emitted if the new source has per-file metadata (optimization overrides etc.); in that case the peer's flags are used and a full rebuild is recommended for exact flags.

## Building

For a local, self-contained build:

``` powershell
# Development
dotnet run --project src/Bt -- dirty

# Release (NativeAOT — single native binary, no .NET runtime needed)
dotnet publish src/Bt -c Release -r win-arm64   # or win-x64, linux-x64
# Output: src/Bt/bin/Release/net8.0/<rid>/publish/bt.exe (~14 MB)
```

## Installing

### From NuGet feed (pre-built)

``` powershell
# One-time: install bt.exe to ~/tools (already on PATH)
nuget install Bt -Source <feed> -PreRelease -OutputDirectory $env:TEMP\bt-pkg -ExcludeVersion
Copy-Item "$env:TEMP\bt-pkg\Bt\tools\win-$(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {'arm64'} else {'x64'})\bt.exe" ~\tools\
Remove-Item $env:TEMP\bt-pkg -Recurse
```

### Publishing to a feed

``` powershell
# From repo root — builds win-x64 + win-arm64, packs, pushes
tools\scripts\publish.ps1

# Single RID only
tools\scripts\publish.ps1 -RID win-arm64
```

On first run, `publish.ps1` prompts for feed name and URL, saved to
`tools/scripts/feed.config` (gitignored).

## Caveats

- **CreateWinMD is skipped.** C++/WinRT projects run `link.exe /WINMD:ONLY`
  (the `CreateWinMD` target) to extract `.winmd` metadata before the real link.
  `bt` skips this step — it's a structural metadata extraction, not an
  inner-loop build concern.  If you add/remove/rename WinRT runtime classes,
  do a full `msbuild` rebuild.

- **Inference uses peer flags for new sources.** If a newly added `<ClCompile>`
  has per-file metadata (e.g. `<Optimization>Disabled</Optimization>`), `bt`
  warns and falls back to mirroring the peer's flags.  Run `msbuild -bl` once
  to get the exact flags into the binlog.

- **Inference does not follow `.props`/`.targets` imports.** Sources added
  via imported property sheets require MSBuild evaluation to enumerate.
  Add such files directly to the `.vcxproj`, or do a full rebuild first.

## Requirements

- .NET 8+ SDK (build-time only; published binary is a native executable)
- A binary log from a full solution build (`msbuild -bl`)
