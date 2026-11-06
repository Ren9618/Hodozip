# cozip GPU spike

This directory contains the throwaway helper used to evaluate
[bea4dev/cozip](https://github.com/bea4dev/cozip) as a possible GPU ZIP
decompression backend for Hodozip v0.4.0. It is **not** part of the application
or the release assets.

- cozip commit: `85a1e17b74882506c2f2f12a2b092ee72e9a3d7f`
- License: MIT (cozip and this helper)
- Target: Windows x64, Rust stable

## Build

```powershell
cargo build --release
```

## Usage

```powershell
# Hybrid mode (initializes the GPU backend, as the cozip desktop app does)
.\target\release\hodozip-gpu-spike.exe <archive.zip> <output-dir>

# CPU-only mode (does not initialize the GPU backend)
.\target\release\hodozip-gpu-spike.exe <archive.zip> <output-dir> --cpu
```

The helper prints `init_ms`, `extract_ms`, entry count, and input/output byte
counts. Measure wall-clock time around the process separately.

## Result

cozip's ZIP decompression is CPU-only at this commit. Hybrid mode pays for GPU
initialization without accelerating decompression, so Hodozip did not adopt it.
See [`tests/perf/reports/gpu-cozip-evaluation.md`](../../tests/perf/reports/gpu-cozip-evaluation.md)
for the measurements and decision.
