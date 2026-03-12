# bt — Build Tool for incremental MSBuild builds

`bt` reads an MSBuild binary log, extracts the project dependency graph, and
rebuilds only the projects affected by your changed files.

## Quick start

```
# 1. Produce a binlog from a full solution build (one-time or when deps change)
msbuild MySolution.sln -bl

# 2. Parse the binlog and cache the dependency graph
bt graph

# 3. Rebuild only what's affected by your changes
bt build --changed          # uses git to detect changed files
bt build src/Foo/Bar.cpp    # or specify files explicitly
```

## How it works

1. **Parse** — Reads `msbuild.binlog` using
   [MSBuild Structured Log](https://github.com/KirillOsenkov/MSBuildStructuredLog)
   to extract every project, its source files, and its `ProjectReference` edges.

2. **Graph** — Builds an in-memory directed acyclic graph (DAG) of project
   dependencies.  Each node carries the list of files that belong to that
   project.

3. **Cache** — Serializes the graph to `.bt/graph.json` so subsequent commands
   skip the parse step.  The cache is invalidated automatically when the binlog
   is newer than the cache.

4. **Resolve** — Given a set of touched files, finds the owning project(s) and
   computes the transitive closure of all downstream dependents.

5. **Build** — Invokes `msbuild` on each affected project in topological
   (dependency-first) order.

## Binlog assumptions

`bt` expects the binlog to be produced from a **full solution build**:

| Assumption | Why |
|---|---|
| Generated with `msbuild -bl` | Only binary logs contain the structured data we need. |
| Full solution build, not a single-project build | We need the complete set of projects and references to build an accurate graph. |
| Default filename `msbuild.binlog` at repo root | Convention over configuration. |
| Build ran to completion (success or failure) | Partial logs may be missing projects that were never evaluated. |
| Projects are `.vcxproj` (C/C++) or `.csproj` | `bt` extracts `ClCompile` and `Compile` items as source files. |
| `ProjectReference` used for inter-project deps | `bt` does not track NuGet package or raw assembly references. |

## Cache

The graph cache lives at `.bt/graph.json` relative to the working directory.
It stores:

- Project nodes (path, source files, references)
- Dependency edges
- Timestamp of the source binlog

The cache is automatically rebuilt when the binlog's last-write time is newer
than what's recorded in the cache.

## Requirements

- .NET 10+ SDK (to run `bt` itself)
- MSBuild / Visual Studio Build Tools (to build your solution)
