# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versions are identified by commit hash (short).

## [652514a] - 2026-04-16

### Added
- **CompileXaml execution**: dirty `.xaml` files now trigger both XAML
  compilation passes via a single `msbuild` invocation per project
  (`/t:MarkupCompilePass1;SelectClCompile;MarkupCompilePass2`).
  Pass1 produces `.g.h`, `.g.cpp`, `.xbf`; Pass2 produces
  `XamlTypeInfo.g.cpp`.  bt then compiles the generated files via
  direct CL and links.  `SolutionDir` is passed so paths resolve
  correctly outside a solution build.

### Fixed
- **`bt build` hangs in shells with debugger env vars**: shell vars
  like `_NT_SYMBOL_PATH`, `DBGHELP_*`, `PGOBuildMode` leaked into
  child processes, causing link.exe to hang on symbol server lookups.
  Child process environment is now built exclusively from the binlog.
- **Synthetic `#include` overwrites real producers in cache**: when
  loading from cache, `#include` commands were written to
  `FileToProducer` (1:1), overwriting real producers like CompileXaml.
  Now only stored in `SyntheticProducers` (1:N).
- **`dirty`: all inputs at same max mtime now reported**: when
  multiple inputs shared the newest timestamp, only one was shown
  as the trigger.  Now all same-mtime inputs are listed.

### Changed
- **`build` / `dirty` are now target-oriented (make-style)**:
  args are targets you want up-to-date, not "changed sources."
  `bt build MyApp.dll` walks backward to find dirty inputs and
  builds only what that target needs.  Sources (`.cpp`, `.h`)
  still work — they walk forward.  Both paths now use mtime-based
  dirty detection (previously explicit files skipped mtime).
  `-c` is a unified post-filter; no separate code path.

## [8d7df6c] — 2026-04-09

### Added
- `bt update` now shows relevant CHANGELOG blurb.
- Telemetry opt-out: set `BT_NO_TELEMETRY=1` to disable all telemetry.

## [d30ccc4] — 2026-04-09

### Fixed
- **`bt build` on a fresh CMD hangs at LINK**: only 6 per-project
  SetEnv vars were replayed; global env vars (TEMP, VCToolsInstallDir,
  etc.) from the MSBuild process were missing.  Now extracts the full
  environment from the binlog's Environment folder.

## [9279fe5] — 2026-04-07

### Added
- `bt update` — self-update from GitHub Releases.  Checks the latest
  release, compares commit counts, downloads the matching architecture
  zip (win-x64 / win-arm64), and swaps the running binary in-place.
  `--check` flag for version check without downloading.  Respects
  `GITHUB_TOKEN` for private repos / rate limiting.

## [4b4c6e4] — 2026-04-07

### Fixed
- **Failed binlog no longer blocks bt**: when a cached graph exists
  but the latest binlog is from a failed build, bt warns and reuses
  the last good cache instead of exiting.  Previously required a
  successful `msbuild /bl` before bt would work again.

## [feb2e45] — 2026-04-02

### Added
- `graph --headers` — when used with `-f`, includes all tlog-recorded
  `#include` headers in the subgraph (repo-local only, SDK headers
  excluded via `IsExternal`).
- `srcs --headers` — shows tlog-recorded headers in the upstream
  dependency tree.  Greppable flat list of all headers a source
  transitively includes.

### Changed
- Copy commands use `File.Copy` instead of shelling out to `cmd.exe`.
  Works in non-standard shells (e.g. MSYS2), no subprocess overhead.
  Dry-run shows absolute paths, no `cd` line.

### Fixed
- **`graph -f` showed broken `#include` edges**: depending on cache
  state, 0 headers (fresh parse) or 1 random header (cache hit) were
  shown.  Default now shows a clean build chain with no `#include`
  edges.  Use `--headers` to opt into the full set.
- **False rebuilds from intermediate timestamps**: `build` no longer
  triggers spurious LIB/LINK/Copy rebuilds when co-located build
  intermediates (e.g. `pch_hdr.src`) have newer timestamps than
  outputs.

## [be4f896] — 2026-03-25

### Added
- **Failed-binlog guard**: `bt` now rejects binlogs from failed builds,
  printing the first error and exiting.  Previously a broken binlog
  produced an incomplete graph (e.g. missing LINK commands) that
  silently compiled but never linked.

### Changed
- `/?` and subcommand help (e.g. `bt build /?`) now show the same
  coloured output as `--help` / `-?`.
- `bt watch` detects `.vcxproj` / `.vcxitems` edits and reloads the
  graph (re-running inference).  Previously, removing a source from
  the project file had no effect until the watcher was restarted.

### Fixed
- **Inferred objs missing from linker**: inferred `.obj` files were
  added to the graph edges but not to the LINK/LIB command line.
  Compile succeeded, link used the stale command.
- `--color never` is now respected on all help paths (previously
  ignored when `--help` / `-?` / `/?` triggered the help action).
- `bt --color never help` no longer fails with "unrecognized command".

## [17f1648] — 2026-03-25

### Added
- **Source inference**: when a `.vcxproj` (or imported `.vcxitems`) has a
  newer mtime than the binlog, `bt dirty` / `bt build` automatically detect
  new `<ClCompile>` sources not yet in the graph and synthesise compile/link
  commands by mirroring flags from a peer source in the same project.
  Add a `.cpp`, run `bt build` — no full MSBuild run required.
  Per-file metadata (e.g. per-file optimisation overrides) triggers a warning
  and falls back to peer flags; run `msbuild -bl` once for exact flags.

## [e3797be] — 2026-03-23

### Added
- `build -c` / `--compile-only` — run only first-level compile
  commands (CL, MIDL, CompileXaml), skipping link/lib/packaging.
  Works with explicit files (`bt build -c foo.cpp`) or mtime-dirty
  mode (`bt build -c`).  Useful for fast syntax-check workflows and
  MCP tools.

## [459dfd1] — 2026-03-22

### Fixed
- **Watch missed pre-existing dirty files**: `bt watch` only caught
  changes after startup.  Now runs an initial mtime-based dirty check
  (and `--run` if the build succeeds) before entering the watch loop.
- **False `pch.cpp → *.obj` edges in graph**: `.pch` was classified
  as a hidden intermediate, so the graph walk collapsed it and drew
  direct edges from `pch.cpp` to every object file.  `.pch` is now
  dev-visible, correctly showing `pch.cpp → pch.pch → *.obj`.

## [38f7480] — 2026-03-22

### Fixed
- **Build deadlock on command failure**: downstream commands waited
  forever for outputs that would never be produced.  Now transitively
  skipped with a summary count.
- **PDB contention in parallel CL**: multiple `cl.exe` processes
  fighting over a shared `.pdb` file.  `/FS` (force synchronous PDB
  writes) is now injected automatically.
- **Watch infinite rebuild loop**: cppwinrt-regenerated headers
  triggered FileSystemWatcher, causing endless build cycles.  External
  paths (CAExcludePath) are now filtered in watch.
- **Watch triggering on editor temps**: Emacs lock files (`.#file`),
  autosave (`#file#`), Vim swap (`.swp`/`.swo`), and backups (`~`)
  are now silently skipped.
- **Progress line wrapping**: ANSI escape codes inflated padding
  calculation, causing lines to wrap instead of overwriting in place.
- **`--run` stdout buffering**: output from the post-build command was
  captured and printed only after exit.  Now inherits the terminal
  directly for real-time output.

### Changed
- Dirty detection pre-stats all files in parallel (`Parallel.ForEach`)
  before the sequential topo walk.
- Build plan line no longer shows a misleading parallel count.

## [1c0c6af] — 2026-03-21

### Added
- `bt help` as alias for `bt --help`.
- `bt watch --run <cmd>` — run a command after each successful rebuild.
- Cache version stamping (v2): auto-invalidates on bt upgrade.
- NativeAOT publish — ~14 MB native binary, ~30–70 ms startup
  (was ~210–370 ms managed).

### Fixed
- **CreateWinMD producing wrong binary**: `link.exe /WINMD:ONLY`
  (CreateWinMD target) was selected over the real Link, silently
  producing a `.winmd` instead of the `.exe`.  Now skipped at
  graph-build time.
- `#include` synthetic commands now carry the project name, fixing
  `graph -p` omitting all non-generated headers.
- `build --dry-run` shows working directory (`cd <dir>`) so printed
  commands are reproducible.

## [d28fcd1] — 2026-03-19

### Changed
- **Graph cache rewritten**: JSON+gzip replaced with FlatBuffers.
  Sorted string table with integer indices.  ~700 ms → ~20 ms load.

## [7ab790e] — 2026-03-19

### Added
- `bt watch` — watch source files and rebuild on change (`--debounce`).
- `bt cache` — parse binlog and cache graph without building.

## [a3dc044] — 2026-03-19

### Added
- Per-project environment replay from binlog SetEnv tasks (INCLUDE,
  LIB, PATH, etc.) so `bt build` can invoke cl.exe / link.exe
  directly.

### Fixed
- Build deadlock when plan contained up-to-date intermediate outputs.
- Command-line exe parsing for unquoted paths with spaces.
- stdout/stderr pipe deadlock in command execution.
- Stale mtime after incremental link: touch outputs on success.

## [fffbdc8] — 2026-03-13

### Added
- `bt compiledb` — generate `compile_commands.json` for clangd/clang-tidy.

## [07e40ed] — 2026-03-13

### Added
- `bt build [files]` — parallel wave execution of dirty commands.
  `-j N` for parallelism, `--dry-run` to preview.
- mtime-based dirty detection (make/ninja-style), removing git
  dependency.
- Live ninja-style build progress (line refresh on TTY).
- PCH (`/Yc`, `/Yu`) support in dependency graph.
- External header filtering via CAExcludePath.

## [a3c29d6] — 2026-03-13

### Added
- `bt dirty [files]` — topo-sorted build plan from changed files.
  Dirty sources shown as dependency trees.

### Changed
- Renamed `outs` → `bins`, `affected` → `dirty`.

## [e7b0c82] — 2026-03-13

### Added
- Precise tlog-based `#include` tracking (CL.read.1.tlog), with
  conservative ClInclude fallback.
- Header includes modelled as `.h` → `.cpp` (not `.h` → `.obj`).
- Tlog ALLCAPS path normalization to actual filesystem casing.
- `--file` and `--project` filters for `graph` subcommand.
- Graph cache with binlog-timestamp staleness detection.

### Changed
- `outputs-of` / `sources-of` renamed to `bins` / `srcs`, shown as
  dependency trees with tool labels.

## [d29211d] — 2026-03-13

### Added
- mdmerge step (unmerged → merged `.winmd`).
- makepri step (`resources.pri`).
- AppxManifest generation.
- AppxPackageRecipe (`.exe` + `.pri` + manifest → `.appxrecipe`).
- Copy tasks in graph.

## [a053b9c] — 2026-03-13

### Added
- MIDL task in graph — `.idl` files now tracked.
- CompileXaml and IDL → generated-header graph edges.
- BinlogExplorer tool for binlog introspection.

## [7f5802d] — 2026-03-13

### Added
- Graphviz DOT graph output (`bt graph`).
- `--binlog <path>` flag.
- `--color auto|always|never` flag.
- ANSI terminal colours.

## [5ec46bf] — 2026-03-13

### Added
- Initial CLI: parse binlog, build command-based DAG, query
  upstream/downstream dependencies.

### Cache version history

| Cache | Commit | What changed |
|:---:|:---:|---|
| 1 | d28fcd1 | Initial FlatBuffers schema |
| 2 | bd14db9 | Skip CreateWinMD; project on `#include` commands |
| 3 | 67d71a2 | Global env from binlog Environment folder |
| 4 | 652514a | CompileXaml Pass1+Pass2; `#include` excluded from `FileToProducer` in cache |
