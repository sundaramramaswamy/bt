# bt agent skill

Fast incremental C++/MSBuild builds — skip MSBuild on the inner loop.

## What it does

This skill teaches the agent to use [bt](https://github.com/sundaramramaswamy/bt)
for incremental C++ builds instead of invoking MSBuild directly.  When you edit
a few `.cpp` or `.h` files, bt builds in seconds — not minutes.

Activates when you ask the agent to compile, build, or query build dependencies
in a repo with `.vcxproj` files.

**Examples:**
- "Build only the files I changed"
- "What rebuilds if I edit this header?"
- "Watch my sources and build on save"
- "Show me what's dirty in this project"

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
