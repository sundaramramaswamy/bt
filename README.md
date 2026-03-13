# bt

**MSBuild dependency graph explorer.**  
Parses a binary log, builds a file-level dependency graph, and lets you query it.

```
bt outs TestDataItem.h        # what gets rebuilt when this header changes?
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

```bash
# One-time: full solution build with binary logging
msbuild MySolution.sln -bl

# Explore
bt outs MyHeader.h            # tree of everything downstream
bt srcs MyApp.exe             # tree of everything upstream
bt graph -p MyProject         # DOT graph filtered to one project
bt graph -f MyFile.cpp        # DOT subgraph around one file
```

The graph is cached to `.bt/graph.json` — subsequent runs skip binlog parsing
unless the binlog changes.

## Commands

| Command | Description |
|---------|-------------|
| `bt outs <files>` | Downstream dependency tree — what rebuilds when `<file>` changes |
| `bt srcs <files>` | Upstream dependency tree — what sources feed into `<file>` |
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

3. **Cache** — The graph serializes to `.bt/graph.json`. Invalidated when the binlog's timestamp changes.

4. **Query** — `outs`/`srcs` walk the graph forward/backward, printing a tree. `graph` emits DOT for visualization.

## Requirements

- .NET 10+ SDK
- A binary log from a full solution build (`msbuild -bl`)
