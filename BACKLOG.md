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
- [ ] Cache project file mtimes alongside the binlog timestamp so inference
      is skipped on cache-hit paths entirely (currently re-runs on every
      invocation, but costs only N mtime stats normally).

## Features
- [ ] `update --pre` — include pre-release versions in update check.
      Currently all releases are CI pre-releases (`1.0.0-ci.*`), so the
      flag is meaningless until stable releases exist.
- [ ] `build --profile` — report per-command wall-clock time to identify build
      bottlenecks
- [x] ~~`build -f` prereq-missing warning~~ — No longer applicable.
      `build [targets]` now uses make-style target-oriented scoping:
      `GetCommandCone` walks backward from output targets (or forward
      from sources) to determine the command cone, then
      `GetDirtyCommandsByMtime(scope)` handles mtime checking within
      that cone.  The old `GetAffectedCommands` forward-only walk is
      no longer used by `build` or `dirty`.
- [ ] Include tree reconstruction — tlog data is flat (MSVC's file tracker
      records every `CreateFile`, no nesting).  To reconstruct the actual
      `#include` hierarchy would need `/showIncludes` (indented tree) or
      `/sourceDependencies` (JSON, MSVC 16.7+) integration.  Both require
      the flag to be enabled at build time.  Currently bt's `--headers`
      shows the flat tlog set which is complete but unstructured.
- [ ] Direct XamlCompiler.exe invocation — currently bt invokes
      `msbuild /t:MarkupCompilePass1` (~0.1s MSBuild eval overhead).
      XamlCompiler.exe (`input.json` → `output.json`) would skip MSBuild
      evaluation, but is broken: errors are silently swallowed (known bug
      microsoft/microsoft-ui-xaml#10027) and `GetAssemblyItems()` in
      `CompileXamlInternal.cs` crashes with a NullRef when any
      `ReferenceAssemblies` are provided (no null guard on the list
      parameter).  JSON schema is `CompilerInputs` in
      `Microsoft.UI.Xaml.Markup.Compiler.MSBuildInterop.dll`.  Revisit if
      Microsoft fixes #10027.

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
