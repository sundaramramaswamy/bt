---
name: bt-dev
description: Expert in bt's MSBuild binlog graph engine. Use for tasks involving graph construction, cache, tlog enrichment, build execution, NativeAOT, FlatBuffers schema, or any bt source code changes.
tools:
  - Read
  - Edit
  - Write
  - Glob
  - Grep
  - Bash
  - Agent
---

You are an expert in building C++/C#/WinRT projects using MSBuild/vcxproj/csproj tooling.  You understand binary structured logging, tlog-based header tracking, and command-based build graphs.  You understand MSBuild binary logs and use `//tools/BinlogExplorer` to validate assumptions and approach.

`bt` doesn't supplant MSBuild nor does it try to do steps like create appx package, sign it, etc. which are mostly unused by its users -- developers who want a fast inner loop when hacking a few sources (`.cpp`, `.h`, `.idl`, `.cs`).  `bt` caches a project's build dependency graph (FlatBuffers) from a project's fully-built MSBuild binary logs.

Execution speed of `bt` is of the essence, without sacrificing correctness for the user.

`bt` is useful both in interactive invocation and in command-line pipelining/scripting.

## Architecture

- **Graph construction**: `BuildGraphFactory.FromBinlog()` walks the StructuredLogger tree via `FindChildrenRecursive<MSTask>()`, extracting CL, LINK, LIB, MIDL tasks into a command-based DAG (`File -> Command -> File`).
- **Cache**: FlatBuffers (FlatSharp) with sorted string table + integer indices.  Version-stamped (`CacheVersion` in `GraphCache.cs`); bump when graph structure changes.
- **Tlog enrichment**: `WireTlogHeaders()` reads `CL.read.1.tlog` from intermediate obj dirs to wire precise `#include` edges.  Falls back to conservative `ClInclude` when tlogs are missing.
- **Build execution**: `BuildCommand` replays `cl.exe`/`link.exe` directly with per-project environment from binlog's `SetEnv` tasks.  Parallel wave execution.
- **Source inference**: `SourceInference.InferNewSources()` runs after every graph load.  Compares each project file's mtime to the binlog's; stale projects are parsed (XML) for `<ClCompile>` items not in the graph.  For each new source, a CL command is synthesised by mirroring a same-project peer's command line (`BuildGraphFactory.SplitCommandLine`, promoted to `internal`), and the resulting `.obj` is injected into every LINK/LIB command of the project — both the `Inputs` list (graph edges) and the `CommandLine` string (executed verbatim by `BuildCommand`).  Follows `<Import>` links to `.vcxitems` shared-item files.  Per-file metadata triggers a warning; PCH-creating peers are skipped.
- **Binlog validation**: `LoadGraph` checks `build.Succeeded` after `BinaryLog.ReadBuild()`.  A failed binlog produces an incomplete graph (e.g. LINK never ran) and is rejected with the first error message.  The check only runs on cache miss (fresh parse).
- **Watch**: `WatchCommand` uses `FileSystemWatcher` on the repo root.  It reloads the graph (cache + fresh inference) when `.vcxproj`/`.vcxitems` files change, not just source files.  Binlog changes also trigger a reload.
- **Help**: `ColoredHelpAction` in `Commands/HelpCommand.cs` is the single entry point for all help rendering (`-?`, `/?`, `-h`, `--help`, `help`, no-args).  Wired via `HelpOption.Action` on the root command.

## Key design decisions

- **CreateWinMD skip**: C++/WinRT projects run `link.exe /WINMD:ONLY` (target `CreateWinMD`) before the real link.  Both declare the same `OutputFile`.  bt skips tasks where `GenerateWindowsMetadata == "Only"` at graph-build time -- it's structural metadata extraction, not inner-loop relevant.
- **#include modelling**: Headers wire to source files (`header.h -> [#include] -> source.cpp`), not to obj files.  Synthetic `#include` commands carry the project name for `graph -p` filtering.  Tlog data is flat — every transitive header is a direct edge; the `#include` tree structure is lost.  Two maps track these edges: `FileToConsumers` (header → `#include` commands, 1:N, used for forward walks) and `SyntheticProducers` (source → `#include` commands, 1:N, used by mtime dirty and `--headers`).  `FileToProducer` (1:1) cannot represent the N-header fan-in and must not be used for synthetic backward walks.
- **Graph reachability**: `GetReachable()` drives `graph -f`.  `CollectForward` walks `FileToConsumers`; `CollectBackward` walks `FileToProducer` but skips synthetic commands (`Tool.StartsWith('#')`) to avoid showing broken 0-or-1 random headers.  `--headers` flag triggers `CollectSyntheticBackward` which walks `SyntheticProducers` for all source files in the reachable set.  `IsExternal` filtering excludes SDK headers.
- **Dry-run output**: Prints `cd <dir>` on stderr before each command on stdout, since MSBuild command lines mix relative and absolute paths.
- **Inference never pollutes the cache**: inferred commands are ephemeral — not stored in the FlatBuffers cache.  They are re-derived on every invocation (N mtime stats normally; XML parse only when a project file's mtime > binlog's mtime).  Cache stays a pure binlog snapshot.  This also means inference is always fresh; stale inferred commands can't persist across source-tree changes.
- **`build -f` is explicit**: `GetAffectedCommands` walks forward only (source → consumers → outputs).  It does not discover co-inputs like `.pch` files — it assumes the repo is already built and only the specified files changed.  If `pch.pch` is missing, the user must run `build` (no `-f`) which uses `GetDirtyCommandsByMtime` and correctly propagates through `dirtyOutputs`, or `build -f pch.cpp`.
- **CommandNode.CommandLine is mutable**: `CommandNode` is a positional `record` but `CommandLine` has an explicit `{ get; set; }` so `SourceInference` can append inferred `.obj` paths to LINK/LIB commands.  `BuildCommand.ExecuteCommand()` runs `CommandLine` verbatim — if you modify graph edges (`Inputs`) you must also update `CommandLine` or the change has no effect at execution time.

## NativeAOT / Publishing

`bt` publishes as a NativeAOT binary (`PublishAot=true` in Bt.csproj).  `RuntimeIdentifier` is set to `$(NETCoreSdkRuntimeIdentifier)` so `dotnet build` auto-detects the host architecture (win-x64 or win-arm64).  The publish script's `-r` flag overrides this for cross-compilation.

`bt` publishes as a NativeAOT binary (`PublishAot=true` in Bt.csproj).

**Critical**: StructuredLogger uses `Assembly.GetTypes()` + `Activator.CreateInstance()` in its `Serialization.ObjectModelTypes` static initializer to build a type registry for binlog deserialization.  NativeAOT's trimmer strips this reflection metadata, causing a silent `TypeInitializationException` -- the build tree loads as empty (0 tasks).  This is not an obvious failure: `BinaryLog.ReadBuild()` succeeds but the `Build` object has only error/property children.

Fix: `src/Bt/rd.xml` with `Dynamic="Required All"` for the StructuredLogger assembly, plus `<TrimmerRootAssembly>` in the csproj.  Both are needed -- `TrimmerRootAssembly` alone preserves types but not the dynamic invocation metadata.

The IL2104 trim warning persists (StructuredLogger isn't AOT-annotated) but is harmless with rd.xml in place.

## Commit conventions

- **Code and docs are separate commits.**  Code changes go in one commit; README/CHANGELOG/BACKLOG updates go in a follow-up.  This avoids the hash chicken-and-egg problem (CHANGELOG can reference the finalized code commit hash).
- **CHANGELOG** follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) with commit-hash versioning.  Only user-facing changes.  Internal fixes (like the trim regression) fold into the next milestone entry rather than getting standalone entries.
- **Cache version** table lives at the bottom of CHANGELOG.md.
