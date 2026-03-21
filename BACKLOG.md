# Backlog

## Optimization
- [x] ~~Filter generated/SDK headers from mtime dirty sweep~~
      Done: uses CAExcludePath from binlog
- [x] ~~FlatBuffers cache~~ — replaced gzip+JSON with FlatSharp (FlatBuffers).
      Sorted string table with int indices; ~700ms → ~20ms cache load.
- [ ] **NativeAOT** — spike validated (spike/native-aot, deleted).
      Managed: ~210–370ms startup, AOT: ~30–70ms.  `bt dirty` warm: 33ms.
      Binary: 7.5 MB (vs ~66 MB managed single-file).
      Requires: drop `PackAsTool` + `PublishSingleFile`, replace with
      `<PublishAot>true</PublishAot>`.  StructuredLogger emits trim
      warning (IL2104) but works at runtime.  FlatSharp OK.
- [ ] Parallel mtime stat calls — `Parallel.ForEach` over file set for dirty
      detection; OS metadata cache makes this scale well.
- [ ] Cached mtimes — store last-known mtimes in cache; on `dirty`, only
      re-stat files whose parent directory mtime changed (NTFS updates dir
      mtime on child writes).

## Features
- [x] Live progress display for `build` (ninja/FASTBuild-style line refresh)

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
