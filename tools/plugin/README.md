# bt agent skill

Fast incremental MSBuild/C++ builds -- skip MSBuild on the inner loop.

## What it does

This skill teaches the agent to use [bt][] for incremental builds instead of
invoking MSBuild directly.  bt reads a binary log, builds an exact file-level
dependency graph, and replays only the dirty steps -- compile, link, lib, MIDL,
CompileXaml, mdmerge, makepri, AppxManifest -- in seconds, not minutes.

Activates when you ask the agent to compile, build, or query build dependencies
in a repo with `.vcxproj` files.

**Examples:**
- "Build only the files I changed"
- "What rebuilds if I edit this header?"
- "Watch my sources and build on save"
- "What sources feed into this DLL?"

[bt]: https://github.com/sundaramramaswamy/bt

## What bt handles

CL, Link, Lib, MIDL, CompileXaml, mdmerge, makepri, AppxManifest, Copy -- and
precise `#include` edges from tracker logs.  Newly added `.cpp` files are
inferred automatically; no full rebuild needed.

## Prerequisites

- A Windows machine with a C++/MSBuild repo
- A binary log from a prior full build (`msbuild -bl`)

bt is auto-installed from GitHub Releases on first use if not already in PATH.

## Install as a personal skill

```
robocopy tools\plugin\skills\bt-build %USERPROFILE%\.copilot\skills\bt-build /s
```

Then restart the CLI or run `/skills reload`.

## Source

Maintained alongside bt at <https://github.com/sundaramramaswamy/bt>
under `tools/plugin/`.
