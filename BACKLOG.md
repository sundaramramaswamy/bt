# Backlog

## Optimization
- [x] ~~Missing headers of cpp in `graph -f`~~
  Analysed: `CollectBackward` only walks `FileToProducer` (1:1), missing
  the `SyntheticProducers` (1:N) map used by mtime/dirty paths.  Fresh
  parse shows 0 headers; cache hit shows 1 random header.  Fixed: default
  `graph -f` now shows clean build chain (no `#include` edges); `--headers`
  flag on `graph` and `srcs` opts into full tlog header display.
- [x] ~~Filter generated/SDK headers from mtime dirty sweep~~
      Done: uses CAExcludePath from binlog
- [x] ~~FlatBuffers cache~~ — replaced gzip+JSON with FlatSharp (FlatBuffers).
      Sorted string table with int indices; ~700ms → ~20ms cache load.
- [x] ~~NativeAOT~~ — ~14 MB native binary, ~30–70ms startup (was
      ~210–370ms managed).  Requires rd.xml to preserve StructuredLogger
      reflection metadata (Assembly.GetTypes + Activator.CreateInstance
      for binlog deserialization).
- [x] ~~Parallel mtime stat calls~~ — `Parallel.ForEach` over file set before
      dirty topo-walk; sequential loop does dictionary lookups instead of I/O.
- [x] ~~Cached mtimes~~ — **won't fix**: NTFS does not update parent directory
      mtime on child content writes (only on create/delete/rename), so the
      dir-mtime invalidation strategy would miss edits.  Parallel stat is the
      correct approach.

## Features
- [ ] `build --profile` — report per-command wall-clock time to identify build
      bottlenecks
- [ ] `build -f` prereq-missing warning — `build -f Tracing.cpp` assumes
      prerequisites (e.g. `pch.pch`) exist.  If a build-artifact input is
      missing on disk, warn the user and suggest `build` (no `-f`) or
      `build -f pch.cpp`.  Analysis: `GetAffectedCommands` walks forward
      only (source → consumers → outputs); it never discovers the `/Yc`
      producer of `pch.pch` because that's a co-input, not upstream.  The
      wave executor seeds `produced` with out-of-plan outputs without
      checking file existence, so `cl.exe` fails at runtime.  Plain `build`
      (no `-f`) handles this correctly via `GetDirtyCommandsByMtime` which
      topo-sorts all commands and propagates dirtiness through
      `dirtyOutputs`.
- [ ] Include tree reconstruction — tlog data is flat (MSVC's file tracker
      records every `CreateFile`, no nesting).  To reconstruct the actual
      `#include` hierarchy would need `/showIncludes` (indented tree) or
      `/sourceDependencies` (JSON, MSVC 16.7+) integration.  Both require
      the flag to be enabled at build time.  Currently bt's `--headers`
      shows the flat tlog set which is complete but unstructured.

## Robustness
- [x] ~~Version-stamp the cache~~ — cache version field (int) auto-invalidates
      on bt upgrade.  Bump `CacheVersion` in GraphCache.cs when graph
      structure changes.

## Known limitations
- System/SDK headers outside repo root are invisible to mtime checks.
  Toolchain updates (e.g. VS update changing `stdio.h`) require a full
  `msbuild -bl` rebuild. By design — checking SDK paths on every sweep
  would be slow and almost never useful.
- `build` replays SetEnv-provided environment (INCLUDE, LIB, PATH, etc.)
  from the binlog. Env vars set by other means (e.g. shell profile) are
  inherited from the current process but not captured.
- Inferred sources use peer flags. Per-file `<ClCompile>` metadata
  (optimization overrides, extra defines, etc.) is not applied — the peer's
  flags are mirrored and a warning is emitted. Run `msbuild -bl` once for
  exact flags.
- Sources added via `.props`/`.targets` imports are not detected by inference
  (requires full MSBuild property evaluation to enumerate). Add such files
  directly to the `.vcxproj` or do a full rebuild first.
- New headers are invisible to dirtiness tracking until the next `msbuild -bl`.
  `bt build` invokes `cl.exe` directly without MSBuild's file tracker, so no
  tlog is written and no header→source edges are added to the graph.
  Concretely: after `bt build` compiles a source that includes a brand-new
  header, subsequent edits to that header will not mark the source as dirty
  until `msbuild -bl` runs and writes a fresh tlog.  Affects all three cases:
  (1) new `.h` included by an existing `.cpp` — dirty on source mtime the
  first time, but future `.h`-only edits won't propagate until next full build;
  (2/3) new `.h` and `.cpp` added together — same gap after the first `bt build`.

## Optimization
- [ ] Cache project file mtimes alongside the binlog timestamp so inference
      is skipped on cache-hit paths entirely (currently re-runs on every
      invocation, but costs only N mtime stats normally).
