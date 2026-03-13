# Backlog

## Optimization
- [ ] Filter generated/SDK headers from mtime dirty sweep
      (false positives from cppwinrt projections under `Generated Files\`)
- [ ] System/SDK headers outside repo root are invisible to mtime checks;
      toolchain updates (e.g. VS update changing `stdio.h`) require a full rebuild

## Features
- [ ] Live progress display for `build` (ninja/FASTBuild-style line refresh)
