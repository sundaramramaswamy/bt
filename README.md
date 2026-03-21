# bt

**MSBuild incremental build tool.**  
Parses a binary log, builds a file-level dependency graph, and lets you query and rebuild from it.

```
bt bins TestDataItem.h        # what gets rebuilt when this header changes?
bt srcs XaBench.exe           # what sources feed into this binary?
bt graph | dot -Tsvg -o g.svg # full Graphviz DOT graph
```

## Why

MSBuild solutions can have thousands of files across dozens of projects.
When you touch one header, you want to know *exactly* what rebuilds — not guess.

`bt` extracts the real dependency graph from the build itself (not project files)
by reading the structured binary log that MSBuild already produces. Every CL
compile, Link, MIDL, XAML compile, mdmerge, Copy, and packaging step becomes an
edge in the graph. Tlog files add precise `#include` tracking per source file.

## Getting started

``` powershell
# One-time: full solution build with binary logging
msbuild MySolution.sln -bl

# Explore
bt bins MyHeader.h            # tree of everything downstream
bt srcs MyApp.exe             # tree of everything upstream
bt dirty                      # build plan from file timestamps (mtime)
bt dirty MyFile.cpp           # build plan for specific file(s)
bt build                      # rebuild only what's stale
bt build -n                   # dry-run — show commands without executing
bt watch                      # watch sources, rebuild on save
bt compiledb                  # generate compile_commands.json for clangd
bt graph -p MyProject         # DOT graph filtered to one project
bt graph -f MyFile.cpp        # DOT subgraph around one file
```

The graph is cached to `.bt/<name>.fb` (FlatBuffers) — subsequent runs skip
binlog parsing unless the binlog changes.

## Commands

| Command | Description |
|---------|-------------|
| `bt bins <files>` | Downstream dependency tree — what rebuilds when `<file>` changes |
| `bt srcs <files>` | Upstream dependency tree — what sources feed into `<file>` |
| `bt dirty [files]` | Topo-sorted build plan (default: mtime-based; explicit files override) |
| `bt build [files]` | Execute dirty commands in parallel waves (`-j N`, `--dry-run`) |
| `bt watch` | Watch sources and rebuild on change (`--debounce ms`) |
| `bt compiledb` | Generate `compile_commands.json` for clangd / clang-tidy |
| `bt cache` | Parse binlog and cache dependency graph |
| `bt graph` | Emit full Graphviz DOT graph (pipe to `dot`, `d2`, etc.) |

### Graph filters

```
bt graph -f ButtonTest.xaml       # subgraph reachable from/to that file
bt graph -p XaBenchPack           # only nodes from that project
bt graph -p XaBench -f main.cpp   # combine (AND)
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--binlog <path>` | `msbuild.binlog` | Path to binary log (also tries `msbuild_debug.binlog`, `msbuild_release.binlog`) |
| `--color <mode>` | `auto` | ANSI colours: `auto`, `always`, `never` |
| `-j <N>` | CPU cores | Max parallel commands for `build` |
| `-n, --dry-run` | — | Print commands without executing (`build` only) |
| `--debounce <ms>` | `300` | Debounce delay before rebuilding (`watch` only) |
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

## Building

For a local, self-contained build:

``` powershell
# Development
dotnet run --project src/Bt -- dirty

# Release (NativeAOT — single native binary, no .NET runtime needed)
dotnet publish src/Bt -c Release -r win-arm64   # or win-x64, linux-x64
# Output: src/Bt/bin/Release/net8.0/<rid>/publish/bt.exe (~7.5 MB)
```

## Caveats

- **CreateWinMD is skipped.** C++/WinRT projects run `link.exe /WINMD:ONLY`
  (the `CreateWinMD` target) to extract `.winmd` metadata before the real link.
  `bt` skips this step — it's a structural metadata extraction, not an
  inner-loop build concern.  If you add/remove/rename WinRT runtime classes,
  do a full `msbuild` rebuild.

## Requirements

- .NET 8+ SDK (build-time only; published binary is a native executable)
- A binary log from a full solution build (`msbuild -bl`)
