# Hodozip performance comparison

- baseline: phase3-v031-sequential (2026-11-07 06:46:23, sequential)
- current: phase4-parallel (2026-11-07 06:47:31, parallel)
- machine: MYDESKTOPPC, 16 logical processors

| Fixture | Archive | Entries | Baseline (ms) | Current (ms) | Speedup | Baseline MiB/s | Current MiB/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| large-compressible | 2.5 MiB | 16 | 5,336 | 806 | 6.62x | 191.9 | 1,271.0 |
| many-small-files | 1.5 MiB | 10,000 | 28,577 | 8,210 | 3.48x | 0.2 | 0.6 |
| store-heavy | 256.0 MiB | 128 | 944 | 184 | 5.14x | 271.1 | 1,394.9 |
| representative-7z | 1.5 MiB | 80 | 3,104 | 3,111 | 1.00x | 51.5 | 51.4 |

Notes:

- Parallel extraction is enabled for non-encrypted ZIP only. 7z, RAR, TAR, MSI, and encrypted ZIP use the sequential path.
- representative-7z therefore measures the sequential path in both runs; the small difference is run-to-run noise.
- The large-compressible fixture expands to 1 GiB (16 x 64 MiB entries).
