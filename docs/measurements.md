# Measured Results

Every number the README's "Results" section refers to, with the sample count behind each.

**Machine.** AMD Ryzen 7 7840HS, 8 physical / 16 logical cores. Windows 11. .NET 10.0.11. NVMe SSD. Release builds, one sort at a time, input, temporary files and output on the same volume.

**Input.** `TestFileGenerator` at seed 42, average line length 37.64 bytes. The 20 GiB file holds 570,483,043 lines and hashes to `2e91107033e198d4` (on this .NET version only: `System.Random`'s sequence is not stable across major versions). The 100 GiB file is described in section 2.

**Benchmark input changed on 2026-10-02.** The in-process benchmarks and `-- --flatness` now generate input with `TestFileGenerator`'s `LineComposer` (seeded, default 0.1 duplicate ratio) instead of a ten-word ASCII vocabulary. Earlier figures are not directly comparable with later ones.

---

## What reproduces, and what does not

| Measurement | Reproducibility | Read it as |
|---|---|---|
| Run counts, line counts, hashes | Exact | A claim |
| Allocation (BenchmarkDotNet) | To the reported digit | A claim |
| Phase one at 20 GiB | 30.4–41.2 s over twenty runs | A range, not a point |
| Phase one at 100 GiB | 152.6–191.3 s at a fixed budget | A range, not a point |
| Merge time, any size | Tracks the drive, not the code | An observation |
| Micro-benchmark time at parallelism 4 | Does not reproduce at all | Nothing |

Merge time tracks the drive because eight workers share one write queue; phase one is CPU-bound enough to compare.

---

## 1. The reference sort, at 20 GiB

Ten consecutive sorts at `--memory 4GiB` on the `akka` pipeline, the default at the time (now `channels`). Section 4 compares them with ten interleaved `channels` sorts.

| | 20 GiB @ 4 GiB, ten runs |
|---|---|
| Total | 69.9 s median, 56.3–74.7 s |
| Phase one | 36.5 s median, 30.6–41.2 s; 186 runs every time |
| Merge | one pass, every time |
| Splitter search | 0.73 s median, 0.19–0.92 s |
| Slice imbalance | 1.01x in all ten |
| Workers finished in | 21.8–32.7 s |
| Output-wait, summed over 8 workers | 83.9 s median, 27.8–103.2 s |
| Output bytes | 21,474,836,456 |
| Temporary files left behind | 0 |

The output verifies (same line count and hash on both sides); 492 tests passed at the time, none skipped, warnings as errors.

**The total's spread is the drive.** The first run took 56.3 s with 27.8 s of output-wait; back-to-back runs after it took 67–75 s with three to four times that wait.

**Peak working set runs slightly above `--memory`**: 4,170.7 MiB at 4 GiB and 6,756.3 MiB at 6 GiB (1.8% and 10.0% over), sampled every 500 ms. The budget bounds the program's buffers, not CLR, GC and Akka.Streams overhead.

---

## 2. At 100 GiB

On a 720 GB volume with the 300 GiB free that input, temporary files and output need together.

**The input is a concatenation**, 20 GiB + 60 GiB + 20 GiB: 107,374,182,353 bytes, 2,849,095,397 lines. Both sources use seed 42, so one 20 GiB block appears three times (checked byte-identical). The extra ties make it a capacity test, not the comparator's average case.

Eight runs, in order:

| Budget | Total | Phase one | Runs | Splitters | Workers | Output-wait, summed over 8 |
|---|---|---|---|---|---|---|
| 8 GiB | 448.2 s | 174.2 s | 463 | 0.84 s | 257.8–271.9 s | 1325.0 s |
| 6 GiB | 313.4 s | 156.0 s | 617 | 0.73 s | 140.3–155.3 s | 279.5 s |
| 8 GiB | 304.7 s | 152.6 s | 463 | 0.88 s | 140.8–150.2 s | 503.2 s |
| 6 GiB | 323.2 s | 169.7 s | 617 | 1.43 s | 140.5–151.0 s | 394.0 s |
| 6 GiB | 324.9 s | 171.0 s | 617 | 1.45 s | 141.3–151.3 s | 424.6 s |
| 8 GiB | 360.0 s | 173.4 s | 463 | 1.17 s | 175.3–184.5 s | 728.2 s |
| 6 GiB | 322.7 s | 159.5 s | 617 | 0.69 s | 146.9–161.5 s | 290.8 s |
| 6 GiB | 387.9 s | 191.3 s | 617 | 1.13 s | 175.7–194.4 s | 456.5 s |

Slice imbalance was 1.01x in all but the last run (1.00x). Every run took one merge pass against a fan-in ceiling of 2,048 and left no temporary files. Two were verified: 2,849,095,397 lines and hash `beaa3e37467111a8` on both sides, in 268.1 s and 277.2 s.

**No budget effect is visible between 6 and 8 GiB.** Phase one averages 166.7 s over three runs at 8 GiB and 169.5 s over five at 6 GiB, 1.7% apart, inside per-budget spreads of 21.6 s and 35.3 s. Totals are dominated by output-wait: the 448.2 s and 304.7 s runs do identical merge work.

**The effect exists but is small.** `MemoryBudget.Calculate` makes `ChunkSize` linear in the budget with no ceiling. `ChunkSortBenchmarks`, single-threaded, measured 2026-09-09 on the earlier ten-word-vocabulary input; absolute times are not comparable with the current-input run in [section 6](#the-radix-against-a-plain-introsort), but the trend matches: per-MiB cost there also rises with chunk size, from 3.8 ms at 16 MiB to 6.1 ms at 221 MiB.

| Chunk | Mean | ms per MiB | vs previous |
|---|---|---|---|
| 16 MiB | 142.3 ms | 8.89 | — |
| 32 MiB | 391.6 ms | 12.24 | +37.6% |
| 64 MiB | 919.5 ms | 14.37 | +17.4% |
| 110 MiB (a 4 GiB budget) | 1,743.0 ms | 15.85 | +10.3% |
| 166 MiB (a 6 GiB budget) | 2,880.3 ms | 17.35 | +9.5% |
| 221 MiB (an 8 GiB budget) | 3,963.8 ms | 17.94 | +3.4% |
| 320 MiB | 6,285.9 ms | 19.64 | +9.5% |

At its 84% share of spill-worker time, the 3.4% step from 6 to 8 GiB is under 3% of phase one, about 5 s against a 20 to 35 s spread at a fixed budget. The steep part is lower: 221 MiB costs 19.9% more per byte than 64 MiB.

**Phase one is write-bound at this size**, hence its spread: the run files it writes no longer fit in the 23 GiB of RAM that absorbs much of them at 20 GiB.

---

## 3. Against GNU sort

A 20 GiB file from the same generator, on the same machine.

**The outputs are byte-identical.** `LC_ALL=C sort -k2 -k1,1n` reproduces all three ordering levels; the third is GNU's last-resort whole-line comparison, applied without `-s`. Outputs match at 8 MiB, 1 GiB (SHA-256) and 20 GiB.

**The flags favour GNU.** `-S 4G` matches `--memory 4GiB`, `--parallel=16` is the logical core count, `-T` puts temporaries on the shared volume, and compression is off on both sides. `LC_ALL=C` also skips locale collation.

| | Sample 1 | Sample 2 |
|---|---|---|
| This sorter, `--memory 4GiB` | 54.2 s | 73.6 s |
| GNU sort 8.32, `-S 4G --parallel=16 -k2 -k1,1n` | 374.4 s | 373.3 s |

Run ABBA (sorter, GNU, GNU, sorter) on one drive with 320 GB free.

**The ratio is 5.1x to 6.9x.** GNU reproduces to 0.3%; the sorter's samples differ by 36%, the output-wait swing of section 1.

**The keys are not the cost.** Keyless `LC_ALL=C sort` took 344.6 s, so the two keys are under 8% of GNU's time. The rest is machinery: GNU merges single-threaded through a 16-way tree; this sorter range-partitions 186 runs across eight workers in one pass.

**Caveat.** This is coreutils 8.32 from Git for Windows, so all I/O crosses the `msys-2.0.dll` POSIX layer, a handicap unrelated to GNU's algorithm (WSL's NTFS bridge is slower still). These rows compare against GNU sort *on this platform*.

---

## 4. Phase one: Akka against Channels

Only phase one differs between `--pipeline akka` and `--pipeline channels`.

### 4.1 The real measurement: 20 GiB, ten runs each

Section 1's sorts interleaved with ten `channels` sorts in ABBA blocks, timed by the sorter's `phase one produced N run(s)` line. All twenty produced 186 runs.

| Block | Akka | Channels | Difference |
|---|---|---|---|
| 1 | 33.35 s | 32.40 s | +0.95 s |
| 2 | 38.55 s | 36.45 s | +2.10 s |
| 3 | 37.30 s | 36.85 s | +0.45 s |
| 4 | 36.65 s | 35.60 s | +1.05 s |
| 5 | 37.15 s | 35.80 s | +1.35 s |
| **All ten each** | **36.60 s** | **35.42 s** | **+1.18 s (3.3%)** |

**Akka is about 3% slower and lost all five blocks.** Paired: standard deviation 0.61 s, t(4) = 4.34, 95% confidence interval [0.43, 1.93] s. Unpaired, t = 1.08: the drive drifted upward through the series (first run 30.6 s, the rest 34–41 s), which the pairing cancels.

Total sort time hides it (69.28 s against 68.56 s) under output-wait standard deviations of 20.3 s (Akka) and 8.0 s (Channels).

### 4.2 The micro-benchmark

`--filter "*RunGenerationBenchmarks*"`: an 8 MiB input at a 2 MiB budget through a real `ChunkSpiller`, two launches of five warmup and twenty measured iterations, the whole matrix run five times.

| Method | Parallelism | Mean, across five runs | Allocated |
|---|---|---|---|
| Channels | 1 | 62.2 – 63.7 ms | 2.24 MB |
| Akka | 1 | 68.2 – 76.3 ms | 2.30 MB |
| Channels | 4 | 26.6 – 48.7 ms | 4.52 MB |
| Akka | 4 | 28.2 – 58.5 ms | 4.61 MB |

At parallelism 1 Akka is slower in all five runs, by 9 to 21%. At parallelism 4 the ranges are twice as wide as any difference: noise. Akka allocates 2–3% more, every run.

### 4.3 Where the extra time goes

`SpillWorkBenchmarks` times sorting without writing and writing without sorting. Five runs, same shape.

| Half | Parallelism | Channels | Akka |
|---|---|---|---|
| Sort | 1 | 39.7 – 42.1 ms | 47.6 – 56.8 ms |
| Write | 1 | 44.7 – 49.9 ms | 57.1 – 66.9 ms |
| Sort | 4 | 11.1 – 14.2 ms | 13.9 – 26.8 ms |
| Write | 4 | 26.4 – 35.6 ms | 27.4 – 36.6 ms |

At parallelism 1 Akka loses both halves; at parallelism 4 neither separates. The likely residual is the per-chunk hop of the `Task.Run` in `AkkaRunGeneration`, which keeps sorts off the stream's single actor thread.

### 4.4 Scheduling is not where the cost is

`SchedulerOverheadBenchmarks` runs the same shape with a spill that only releases the pool slot. Five runs.

| Parallelism | Channels | Akka | Akka's allocation |
|---|---|---|---|
| 1 | 10.5 – 12.2 ms | 11.3 – 14.1 ms | 6.6 – 6.8x Channels' |
| 4 | 13.9 – 14.5 ms | 12.8 – 21.0 ms | 4.8 – 5.0x Channels' |

The realistic run costs five to seven times this at parallelism 1 and two to four at parallelism 4, and this still includes reading and parsing, so it bounds scheduling from above. The pipeline gap is −0.3 ms to +2.6 ms at parallelism 1 (−1.1 ms to +7.0 ms at parallelism 4, from Akka's unstable rows). Akka's 14.28 KB against 94–96 KB at parallelism 1 shrinks to 2–3% once the 64 KiB spill buffers are added.

### 4.5 Conclusion

Akka costs about 3% of phase one at 20 GiB and 2–3% more allocation. The default is `channels`; `--pipeline akka` is kept for composition and failure semantics (see the README), not speed.

---

## 5. The merge-worker dimension

`PartitionedMergeBenchmarks` drives `MergeExecutor.ExecuteAsync`, `RangePartitioner` included, over 250 run files from a 128 MiB input, forcing `MergeParallelism` to 1, 2, 4 and 8 on one plan built for 8 workers at 256 MiB.

| Merge workers | Mean | Allocated |
|---|---|---|
| 1 | 1,264.7 ms | 9.36 MB |
| 2 | 1,039.1 ms | 19.72 MB |
| 4 | 705.9 ms | 29.97 MB |
| 8 | 592.3 ms | 50.46 MB |

Eight workers are 53.2% faster than one, with diminishing steps (−17.8%, −32.1%, −16.1%) from straggling and the shared write queue. Allocation grows with worker count, as `MemoryPlan.WorstCasePhaseTwoBytes` predicts. The one-worker row keeps the eight-worker plan's smaller window, so it is not comparable with `MergeBenchmarks.Disk` (1,256.3 ms from disk, 660.1 ms from memory).

### Sparse against plain preallocation

Measured 2026-09-30 on the seed-42 20 GiB file: the shipped build against one whose `SparseFile.TryMarkSparse` returns `false`, so NTFS zero-fills ahead of the higher-offset workers. One discarded warm-up, then sparse, plain, plain, sparse. Merge time is total minus phase one; output wait is summed across workers.

| Configuration | Merge workers | Merge, sparse | Merge, plain | Output wait, sparse | Output wait, plain |
|---|---|---|---|---|---|
| `--memory 1GiB` (default) | 2 | 70.8 s, 73.5 s | 70.9 s, 79.7 s | 10.8 s, 11.4 s | 21.2 s, 29.8 s |
| `--memory 4GiB` | 8 | 34.0 s, 33.4 s | 53.5 s, 53.7 s | 84.4 s, 85.0 s | 215.4 s, 218.6 s |

**Sparse preallocation is what lets the partitioned merge scale**: without it, eight workers merge 59% slower with 2.6x the output wait. Both outputs hash to `A3A86CFA…3E6F13`. It stays, against a bar set before measuring of ≥5% merge time or ≥10% output wait.

---

## 6. Inside the chunk sort

Three instruments over real 110.7 MiB chunks of the 20 GiB file at the target budget, 3,194,299 descriptors each.

**The first bucketing pass.** 48 of 256 top-byte buckets are populated (the generator's 97-word vocabulary); the largest holds 8.1% of the chunk, and the sixteen above 65,536 descriptors hold 60.5%. A second byte splits the biggest to at most 45.6% of its parent, hence the byte-by-byte descent.

**The remaining comparisons.** 57,996,758 comparisons for 3,194,299 lines, 18.2 per line. 31.1% are decided by the cached prefix; 68.9% fall through to the string bytes, examining 14.7 bytes each, which the deep radix replaces with one byte read per descriptor per range.

**The depth cap on degenerate data.** Three million lines, median of five, against single-level bucketing; positive means slower than no deep radix:

| Shape | Baseline | Shipped (guarded, cap 16) |
|---|---|---|
| One 40-byte string part for every line | 1,305.9 ms | +11.0% |
| One 200-byte string part for every line | 2,292.3 ms | +6.2% |
| Empty string part for every line | 829.1 ms | +1.0% |
| Shared 30-byte head, four distinct tails | 1,304.3 ms | +2.0% |

A cap of 32 is faster on real data but costs +20.0% on the 200-byte shape, so 16 ships.

### The radix against a plain introsort

`ChunkSortBenchmarks`, measured 2026-10-02 (2 launches, 5 warm-up and 20 measured iterations, about 17 minutes) on generator input through `SyntheticInput`, seed 20260907. `PlainIntrosort` is `Span<LineDescriptor>.Sort` with a struct comparer over the same `LineOrder.Compare` and cached prefix; `Sort` is the shipped `ChunkSorter.Sort`.

| Chunk | Plain introsort (mean ± error) | `ChunkSorter.Sort` (mean ± error) | Ratio | Allocated, plain / radix |
|---|---|---|---|---|
| 16 MiB | 130.7 ± 2.8 ms | 61.0 ± 1.5 ms | 0.47 | 88 B / 4.6 MiB |
| 32 MiB | 297.8 ± 11.9 ms | 129.6 ± 4.6 ms | 0.44 | 88 B / 11.4 MiB |
| 64 MiB | 729.1 ± 117.0 ms | 286.6 ± 7.3 ms | 0.42 | 88 B / 21.5 MiB |
| **110 MiB** | 1,155.1 ± 27.5 ms | 554.9 ± 15.1 ms | **0.48** | 88 B / 32.3 MiB |
| **166 MiB** | 1,805.4 ± 46.8 ms | 1,599.1 ± 265.0 ms | 0.89 (see below) | 88 B / 44.8 MiB |
| **221 MiB** | 2,712.8 ± 85.6 ms | 1,342.9 ± 44.1 ms | **0.50** | 88 B / 57.3 MiB |
| 320 MiB | 4,104.3 ± 201.7 ms | 2,597.0 ± 473.2 ms | 0.64 | 88 B / 79.5 MiB |

**The radix is about twice as fast at the 110 and 221 MiB (4 and 8 GiB) chunk sizes**, errors under 5% of the mean, well past the 10% bar for keeping it. The wide-error rows come from bimodal iterations; a rerun of the 166 MiB row gave 2,956 ± 521 ms plain against 984 ± 69 ms radix (0.37).

**The price is allocation.** Each leaf sort's span `Sort` call boxes the struct comparer into a delegate, 88 B per call; 54,358 leaf calls at 16 MiB and 136,086 at 32 MiB give 4.56 MiB and 11.42 MiB, matching the table.

---

## 7. The memory-flatness matrices

`-- --flatness` sorts roughly 1, 10 and 100 MiB at a fixed 16 MiB budget, each size and pipeline in a fresh process so one size's allocation churn cannot inflate the next. Peak is the running maximum of `GC.GetTotalMemory(false)`, sampled every 5 ms. The first table predates the per-pipeline split and is Channels only.

| Input | Peak managed heap | Peak vs budget |
|---|---|---|
| 1 MiB | 15.96 MiB | −0.3% |
| 10 MiB | 17.51 MiB | +9.4% |
| 100 MiB | 22.54 MiB | +40.9% |

Spread, largest peak over smallest: 41.2%, against a tolerance of 60%.

Rerun on 2026-09-30 with both pipelines (`MergeParallelism` 1; files on the D: drive):

| Input | Channels peak | Channels vs budget | Akka peak | Akka vs budget |
|---|---|---|---|---|
| 1 MiB | 16.16 MiB | +1.0% | 16.55 MiB | +3.4% |
| 10 MiB | 17.69 MiB | +10.5% | 17.96 MiB | +12.2% |
| 100 MiB | 22.76 MiB | +42.2% | 23.07 MiB | +44.2% |

Spread: 40.8% for Channels, 39.4% for Akka (whose window includes `ActorSystem` startup).

Rerun on 2026-10-02 on generator input: spreads of 48.3% and 43.7%, 100 MiB peaks of 24.55 and 24.35 MiB (Channels, Akka).

**The climb is GC bookkeeping, not retained growth.** Memory right after `BufferPool` construction is flat at roughly 15 MiB for any input; generation budgets grow with the number of collections, which scales with chunk count.

**This matrix never exercises the partitioned merge.** At parallelism 4, `--max-line` 4,096 and assumed mean 32, `MemoryBudget.Calculate` needs roughly 50 MiB before `MergeParallelism` reaches 2. `-- --flatness-parallel-merge` reruns at 64 MiB, adding 1 GiB, every worker confirming `MergeParallelism` 2 (Channels only; the command now runs both pipelines):

| Input | Peak managed heap | Peak vs budget |
|---|---|---|
| 1 MiB | 63.94 MiB | −0.1% |
| 10 MiB | 67.57 MiB | +5.6% |
| 100 MiB | 74.10 MiB | +15.8% |
| 1 GiB | 82.26 MiB | +28.5% |

A 28.6% spread: the partitioned path adds a small fixed state on a larger floor. Each matrix is one recorded run.
