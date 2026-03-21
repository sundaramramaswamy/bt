# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versions are identified by commit hash (short).

## [bd14db9]

### Added
- `bt help` as alias for `bt --help`.
- `bt watch --run <cmd>` — run a command after each successful rebuild.
- Cache version stamping (v2): auto-invalidates on bt upgrade.

### Fixed
- **CreateWinMD producing wrong binary**: `link.exe /WINMD:ONLY`
  (CreateWinMD target) was selected over the real Link, silently
  producing a `.winmd` instead of the `.exe`.  Now skipped at
  graph-build time.
- `#include` synthetic commands now carry the project name, fixing
  `graph -p` omitting all non-generated headers.
- `build --dry-run` shows working directory (`cd <dir>`) so printed
  commands are reproducible.

## [d28fcd1]

### Changed
- **Graph cache rewritten**: JSON+gzip replaced with FlatBuffers.
  Sorted string table with integer indices.  ~700 ms → ~20 ms load.

## [7ab790e]

### Added
- `bt watch` — watch source files and rebuild on change (`--debounce`).
- `bt cache` — parse binlog and cache graph without building.

## [a3dc044]

### Added
- Per-project environment replay from binlog SetEnv tasks (INCLUDE,
  LIB, PATH, etc.) so `bt build` can invoke cl.exe / link.exe
  directly.

### Fixed
- Build deadlock when plan contained up-to-date intermediate outputs.
- Command-line exe parsing for unquoted paths with spaces.
- stdout/stderr pipe deadlock in command execution.
- Stale mtime after incremental link: touch outputs on success.

## [fffbdc8]

### Added
- `bt compiledb` — generate `compile_commands.json` for clangd/clang-tidy.

## [07e40ed]

### Added
- `bt build [files]` — parallel wave execution of dirty commands.
  `-j N` for parallelism, `--dry-run` to preview.
- mtime-based dirty detection (make/ninja-style), removing git
  dependency.
- Live ninja-style build progress (line refresh on TTY).
- PCH (`/Yc`, `/Yu`) support in dependency graph.
- External header filtering via CAExcludePath.

## [a3c29d6]

### Added
- `bt dirty [files]` — topo-sorted build plan from changed files.
  Dirty sources shown as dependency trees.

### Changed
- Renamed `outs` → `bins`, `affected` → `dirty`.

## [e7b0c82]

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

## [d29211d]

### Added
- mdmerge step (unmerged → merged `.winmd`).
- makepri step (`resources.pri`).
- AppxManifest generation.
- AppxPackageRecipe (`.exe` + `.pri` + manifest → `.appxrecipe`).
- Copy tasks in graph.

## [a053b9c]

### Added
- MIDL task in graph — `.idl` files now tracked.
- CompileXaml and IDL → generated-header graph edges.
- BinlogExplorer tool for binlog introspection.

## [7f5802d]

### Added
- Graphviz DOT graph output (`bt graph`).
- `--binlog <path>` flag.
- `--color auto|always|never` flag.
- ANSI terminal colours.

## [5ec46bf]

### Added
- Initial CLI: parse binlog, build command-based DAG, query
  upstream/downstream dependencies.

### Cache version history

| Cache | Commit | What changed |
|:---:|:---:|---|
| 1 | d28fcd1 | Initial FlatBuffers schema |
| 2 | bd14db9 | Skip CreateWinMD; project on `#include` commands |