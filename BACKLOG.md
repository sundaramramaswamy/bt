# Backlog

## Optimization
- [x] ~~Filter generated/SDK headers from mtime dirty sweep~~
      Done: uses CAExcludePath from binlog
- [ ] **FlatBuffers cache** — replace gzip+JSON graph cache with FlatBuffers
      for zero-copy deserialization.  Current JSON deser ~700ms on large
      graphs; FlatBuffers would be <5ms (mmap, no alloc, no parse).
      NuGet: `Google.FlatBuffers`.  Define `.fbs` schema, `flatc --csharp`.
      Rebuild string→node dictionaries on load (still much faster than JSON).
- [ ] **NativeAOT** — add `<PublishAot>true</PublishAot>` to eliminate .NET
      startup (~150ms → ~5ms).  Requires dropping `PackAsTool` (incompatible
      with AOT; distribute as standalone binary instead).  Source-generated
      JSON already AOT-safe.  System.CommandLine 2.0.5 + StructuredLogger OK.
- [ ] Parallel mtime stat calls — `Parallel.ForEach` over file set for dirty
      detection; OS metadata cache makes this scale well.
- [ ] Cached mtimes — store last-known mtimes in cache; on `dirty`, only
      re-stat files whose parent directory mtime changed (NTFS updates dir
      mtime on child writes).

## Features
- [x] Live progress display for `build` (ninja/FASTBuild-style line refresh)

## Known limitations
- System/SDK headers outside repo root are invisible to mtime checks.
  Toolchain updates (e.g. VS update changing `stdio.h`) require a full
  `msbuild -bl` rebuild. By design — checking SDK paths on every sweep
  would be slow and almost never useful.
