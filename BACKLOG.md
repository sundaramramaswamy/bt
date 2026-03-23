# Backlog

## Optimization
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
