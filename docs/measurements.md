# Measured Results

Every number the README's "Results" section refers to, measured on one machine, each figure saying how many samples stand behind it.

**Machine.** AMD Ryzen 7 7840HS, 8 physical / 16 logical cores. Windows 11. .NET 10.0.11. NVMe SSD. Release builds, one sort at a time, input, temporary files and output on the same volume.

**Input.** `TestFileGenerator` at seed 42, average line length 37.64 bytes. The 20 GiB file holds 570,483,043 lines and hashes to `2e91107033e198d4`; the 100 GiB file is described in section 2.

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

The merge is the unreliable half because of the drive: eight workers share one write queue, so its time tracks how the volume happens to feel that minute. Phase one is CPU-bound enough to be worth comparing.

---

## 1. The reference sort, at 20 GiB

Ten consecutive sorts at `--memory 4GiB` on the default `akka` pipeline. Section 4 reads the same series, interleaved with ten `channels` sorts, as a pipeline comparison.

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

**The output verifies**: 570,483,043 lines and hash `2e91107033e198d4` on both sides. The suite passes: 492 tests, none skipped, warnings as errors.

**Twenty back-to-back sorts leave the drive no time to recover, and the totals show it.** The first run finished in 56.3 s with 27.8 s of summed output-wait; the rest sat between 67 and 75 s with three to four times that wait. Phase one, the CPU-bound half, moved far less: 30.6 s to 41.2 s.

**Peak working set runs above `--memory`.** Sampled every 500 ms by a launching script during a 4 GiB and a 6 GiB sort, it peaked at 4,170.7 MiB and 6,756.3 MiB — 1.8% and 10.0% over budget. The budget bounds the program's own buffers, not the process; the residual is CLR, GC and Akka.Streams overhead.

---

## 2. At 100 GiB

The target size, on a 720 GB volume with the 300 GiB free that input, temporary files and output need together.

**The input is a concatenation, and that matters.** 20 GiB + 60 GiB + 20 GiB: 107,374,182,353 bytes, 2,849,095,397 lines. Both sources come from seed 42, so the 60 GiB file's first 20 GiB is byte-identical to the 20 GiB one — checked, not assumed — and that block appears three times. The extra ties work the third comparison level harder and skew the bucketing pass: right file for a capacity test, wrong one for the comparator's average case.

Eight runs, in the order taken:

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

Slice imbalance was 1.01x everywhere but the last run, which reported 1.00x. Every run took **one merge pass**, against a fan-in ceiling of 2,048, and left no temporary files. Two were verified: 2,849,095,397 lines and hash `beaa3e37467111a8` on both sides, in 268.1 s and 277.2 s. The matching hash is what says the outputs carry the same lines; there is no byte oracle at this size, since one costs a second full sort.

**No budget effect is visible between 6 and 8 GiB.** Phase one averages 166.7 s over three runs at 8 GiB and 169.5 s over five at 6 GiB — 1.7% apart, with the larger budget nominally *ahead* but well inside a per-budget spread of 21.6 s and 35.3 s.

**The totals say even less, because output-wait dominates them.** It ranges from 279.5 s to 1325.0 s with nothing in the merge different: the 448.2 s run and the 304.7 s run share a budget, a run count and a merge pass, and differ only in how the drive felt.

**The effect exists in the code; it is too small to see here.** `MemoryBudget.Calculate` makes `ChunkSize` linear in the budget with no ceiling, and `ChunkSorter.Sort` sets phase one's rate. `ChunkSortBenchmarks` sweeps the chunk sort across chunk size, single-threaded:

| Chunk | Mean | ms per MiB | vs previous |
|---|---|---|---|
| 16 MiB | 142.3 ms | 8.89 | — |
| 32 MiB | 391.6 ms | 12.24 | +37.6% |
| 64 MiB | 919.5 ms | 14.37 | +17.4% |
| 110 MiB (a 4 GiB budget) | 1,743.0 ms | 15.85 | +10.3% |
| 166 MiB (a 6 GiB budget) | 2,880.3 ms | 17.35 | +9.5% |
| 221 MiB (an 8 GiB budget) | 3,963.8 ms | 17.94 | +3.4% |
| 320 MiB | 6,285.9 ms | 19.64 | +9.5% |

Cost per byte rises monotonically and never plateaus, but 166 to 221 MiB — exactly 6 to 8 GiB — is **3.4%** of the chunk sort. At its 84% share of spill-worker time that is under 3% of phase one: about 5 s against the 20 to 35 s spread the table above shows at a fixed budget. The steep part is lower down — 221 MiB against 64 MiB is 19.9% per byte.

**Phase one is write-bound at this size, and that is why it spreads** — 152.6–191.3 s, proportionally the same spread as the 20 GiB series' 30.6–41.2 s. It writes the whole input back out as run files, and at 100 GiB none of that fits in the 23 GiB of RAM that absorbs much of it at 20 GiB.

---

## 3. Against GNU sort

An external reference, on a 20 GiB file from the same generator and machine. Worth recording because the two programs can be made to produce the *same bytes*, not merely similar-looking output.

**The orders coincide.** `LC_ALL=C sort -k2 -k1,1n` reproduces the three ordering levels exactly: `-k2` compares field two to end of line byte-ordinally; `-k1,1n` compares field one numerically, ignoring the trailing period and honouring leading zeros and signs; and GNU's last-resort whole-line comparison, applied unless `-s` is given, is the third. Outputs are byte-identical at 8 MiB, 1 GiB (SHA-256 agreeing) and 20 GiB.

**The flags favour GNU, deliberately.** `-S 4G` matches `--memory 4GiB`; `--parallel=16` is the logical core count; `-T` puts GNU's temporaries on the volume holding input and output, the contention the sorter runs under. Compression is off on both sides. `LC_ALL=C` is required for the byte order and also strips locale collation, making GNU faster.

| | Sample 1 | Sample 2 |
|---|---|---|
| This sorter, `--memory 4GiB` | 54.2 s | 73.6 s |
| GNU sort 8.32, `-S 4G --parallel=16 -k2 -k1,1n` | 374.4 s | 373.3 s |

Run ABBA — sorter, GNU, GNU, sorter — on one drive with 320 GB free.

**The ratio is 5.1x to 6.9x**, and that width is entirely this sorter's own: GNU reproduces to 0.3%, while the sorter's two samples differ by 36% — the same output-wait swing section 1 shows across ten runs, with phase one and slice imbalance unmoved.

**The multi-key comparison is not the story.** `LC_ALL=C sort` with no key options — whole-line order, so a bound rather than a competitor — took 344.6 s, putting the two keys at under 8% of GNU's 374 s. The rest is external-sort machinery: GNU merges single-threaded through a 16-way tree where this sorter range-partitions 186 runs across eight workers in one pass.

**The largest caveat, and it is not small.** This is the coreutils 8.32 build shipped with Git for Windows, so every read and write crosses the `msys-2.0.dll` POSIX emulation layer — a real handicap, not attributable to GNU's algorithm. WSL would not settle it cheaply either, reaching NTFS through a bridge slower than msys. Read these rows as *against GNU sort on this platform*, not against GNU at its best; what they establish is that both programs were asked for the same answer and one produced it several times faster.

---

## 4. Phase one: Akka against Channels

Phase one is the only part that differs between `--pipeline akka` and `--pipeline channels`; phase two is the same code either way. Two instruments measure it: real 20 GiB sorts, and a micro-benchmark.

### 4.1 The real measurement: 20 GiB, ten runs each

The twenty sorts of section 1, alternating in ABBA blocks (A B B A, five times), timed by the sorter's `phase one produced N run(s)` line. **All twenty produced exactly 186 runs**, so both pipelines did identical work.

| Block | Akka | Channels | Difference |
|---|---|---|---|
| 1 | 33.35 s | 32.40 s | +0.95 s |
| 2 | 38.55 s | 36.45 s | +2.10 s |
| 3 | 37.30 s | 36.85 s | +0.45 s |
| 4 | 36.65 s | 35.60 s | +1.05 s |
| 5 | 37.15 s | 35.80 s | +1.35 s |
| **All ten each** | **36.60 s** | **35.42 s** | **+1.18 s (3.3%)** |

**Akka is slower by about 3%, and the result is stable** — it lost all five blocks. Paired over them: mean +1.18 s, standard deviation 0.61 s, t(4) = 4.34, 95% confidence interval [0.43, 1.93] s.

**Pairing is what makes that visible.** The drive drifted upward through the series (first run 30.6 s, the rest between 34 and 41 s), and an unpaired comparison over the raw twenty gives t = 1.08, which says nothing. ABBA blocks put each pipeline at the same mean position within its block, cancelling the drift.

**Total sort time does not show the difference**: 69.28 s against 68.56 s. The merge swamps it — summed output-wait had a standard deviation of 20.3 s for Akka and 8.0 s for Channels, against a phase-one difference of 1.18 s.

### 4.2 The micro-benchmark

`--filter "*RunGenerationBenchmarks*"`: identical generated bytes, a real `ChunkSpiller` writing real run files, the same `MemoryPlan`, over an 8 MiB input at a 2 MiB budget. Two launches of five warmup and twenty measured iterations, the whole matrix run **five times**.

| Method | Parallelism | Mean, across five runs | Allocated |
|---|---|---|---|
| Channels | 1 | 62.2 – 63.7 ms | 2.24 MB |
| Akka | 1 | 68.2 – 76.3 ms | 2.30 MB |
| Channels | 4 | 26.6 – 48.7 ms | 4.52 MB |
| Akka | 4 | 28.2 – 58.5 ms | 4.61 MB |

**At parallelism 1 the ranges do not overlap:** Akka is slower in all five runs, by 9 to 21%.

**At parallelism 4 the benchmark measures nothing.** Both pipelines swing across a range twice as wide as any difference between them, and neither ordering survives a repeat. Two samples of that row would read as a result; five say it is noise.

**Allocation reproduces to the digit:** Akka allocates 2–3% more, every run, both parallelisms.

### 4.3 Where the extra time goes

`SpillWorkBenchmarks` measures the halves of a spill separately — sorting without writing, writing without sorting. Five runs, same shape.

| Half | Parallelism | Channels | Akka |
|---|---|---|---|
| Sort | 1 | 39.7 – 42.1 ms | 47.6 – 56.8 ms |
| Write | 1 | 44.7 – 49.9 ms | 57.1 – 66.9 ms |
| Sort | 4 | 11.1 – 14.2 ms | 13.9 – 26.8 ms |
| Write | 4 | 26.4 – 35.6 ms | 27.4 – 36.6 ms |

**At parallelism 1 Akka loses both halves cleanly** — neither row's ranges overlap, consistent with 4.2 and with the 20 GiB series. **At parallelism 4 neither row separates**: the sort halves only touch at the edges (Akka's best 13.9 ms against Channels' worst 14.2 ms) and the write halves overlap outright.

**This is what the `Task.Run` in `AkkaRunGeneration` exists for.** `SelectAsyncUnordered` invokes its mapper on the fused stage's own actor thread, and `ChunkSpiller.SpillAsync` runs synchronously up to its first incomplete await, which is *after* the sort. Without the offload every chunk would be sorted on the stream's single thread, one at a time. What it cannot remove is its own per-chunk hop — the obvious home for the residual.

### 4.4 Scheduling is not where the cost is

`SchedulerOverheadBenchmarks` runs the identical shape with a spill that only releases the pool slot — the disk taken out. Five runs.

| Parallelism | Channels | Akka | Akka's allocation |
|---|---|---|---|
| 1 | 10.5 – 12.2 ms | 11.3 – 14.1 ms | 6.6 – 6.8x Channels' |
| 4 | 13.9 – 14.5 ms | 12.8 – 21.0 ms | 4.8 – 5.0x Channels' |

Against the same shape with a real spiller, the realistic run costs **five to seven times the no-op one at parallelism 1, two to four at parallelism 4** — a small multiple. Three things follow:

- **The no-op figure bounds scheduling from above rather than measuring it**: it still reads and parses the whole 8 MiB through `ChunkReader`, work common to both pipelines.
- **The scheduler's own share is the gap *between* them** — −0.3 ms to +2.6 ms at parallelism 1, where both are stable. At parallelism 4 it spans −1.1 ms to +7.0 ms, but that upper end is Akka's unstable rows, not a repeatable difference.
- **Allocation is where Akka's machinery shows** — five to seven times Channels' with the disk removed, 14.28 KB against 94–96 KB at parallelism 1, which the budgeted 64 KiB spill buffers flatten to the realistic table's 2–3%.

### 4.5 The bottom line

Akka costs about 3% of phase one at 20 GiB and 2–3% more allocation. It is still the default, and not defended on speed: it rests on composition and failure semantics, which the README explains. An operator who wants the 3% back can pass `--pipeline channels`.

---

## 5. The merge-worker dimension

`MergeBenchmarks` drives `KWayMerge.MergeAsync` directly and has no merge-worker axis. `PartitionedMergeBenchmarks` covers it, driving `MergeExecutor.ExecuteAsync` — `RangePartitioner` included — over 250 run files from a 128 MiB input, with `MergeParallelism` forced to 1, 2, 4 and 8 on one plan built for 8 workers at 256 MiB.

| Merge workers | Mean | Allocated |
|---|---|---|
| 1 | 1,264.7 ms | 9.36 MB |
| 2 | 1,039.1 ms | 19.72 MB |
| 4 | 705.9 ms | 29.97 MB |
| 8 | 592.3 ms | 50.46 MB |

**A 53.2% drop from one worker to eight**, tapering across the doublings (−17.8%, −32.1%, −16.1%) rather than scaling linearly — the same straggling and shared write queue the worker spreads in sections 1 and 2 show.

**Allocation rises roughly with the worker count**, matching `MemoryPlan.WorstCasePhaseTwoBytes`'s linear-in-`MergeParallelism` shape: each worker opens its own cursors and output buffer.

**The one-worker row is not comparable with `MergeBenchmarks.Disk`** (1,256.3 ms from disk, 660.1 ms from memory): it forces the eight-worker plan's much smaller per-worker window down to one worker rather than solving fresh for one worker at that budget.

---

## 6. Inside the chunk sort

Three instruments over real 110.7 MiB chunks of the 20 GiB file at the target budget, 3,194,299 descriptors each.

**Where the first bucketing pass leaves the work.** 48 of 256 top-byte buckets are populated, which is what the generator's 97-word vocabulary allows; the largest holds 8.1% of the chunk, and the sixteen above 65,536 descriptors hold 60.5% between them. Hence the byte-by-byte descent: a second byte splits the biggest to at most 45.6% of its parent.

**What the remaining comparisons cost.** A counting comparer over the production sort shape reports 57,996,758 comparisons for 3,194,299 lines, 18.2 per line. **31.1% are decided by the cached prefix alone, in registers; 68.9% fall through into the string bytes**, examining 14.7 bytes each. The deep radix removes that fall-through, reading one byte per descriptor for a whole range instead.

**What the depth cap costs on degenerate data.** Three million lines, median of five, against a single-level bucketing baseline; a positive number is *slower* than no deep radix at all:

| Shape | Baseline | Shipped (guarded, cap 16) |
|---|---|---|
| One 40-byte string part for every line | 1,305.9 ms | +11.0% |
| One 200-byte string part for every line | 2,292.3 ms | +6.2% |
| Empty string part for every line | 829.1 ms | +1.0% |
| Shared 30-byte head, four distinct tails | 1,304.3 ms | +2.0% |

Every range the descent enters on such input is a block of byte-identical keys — exactly what the depth cap bounds. A cap of 32 is faster on real data but costs +20.0% on the 200-byte shape where 16 costs +6.2%, which is why 16 ships. Real chunks have 48 distinct first bytes, not one, which is also why these instruments avoid a synthetic vocabulary.

---

## 7. The memory-flatness matrices

`-- --flatness` sorts roughly 1, 10, and 100 MiB of generated input at one fixed 16 MiB budget, **each size in its own freshly launched process**. That isolation is load-bearing: in one process the *previous* size's allocation churn inflates the *next* size's reading, which looks exactly like a leak that scales with input and is not one.

Peak is the running maximum of `GC.GetTotalMemory(false)`, sampled on a 5 ms background timer throughout each sort, not a single read afterwards. The harness drives `ChannelRunGeneration` directly: it measures the memory claim, which is scheduler-independent, since both strategies are bound by the same buffer pool.

| Input | Peak managed heap | Peak vs budget |
|---|---|---|
| 1 MiB | 15.96 MiB | −0.3% |
| 10 MiB | 17.51 MiB | +9.4% |
| 100 MiB | 22.54 MiB | +40.9% |

Spread, largest peak over smallest: 41.2%, against a stated tolerance of 60%.

**The climb is GC bookkeeping, not growth in the retained set.** The byte count immediately after `BufferPool` construction — the configured footprint the guarantee describes — is flat at roughly 15 MiB whatever the input size, checked directly. Generation budgets and segment counts grow with the *number* of collections a run triggers, which scales with chunk count at a fixed budget, not with what is retained.

**This matrix never exercises the partitioned merge.** At the harness's parallelism (4), `--max-line` (4,096) and assumed mean (32), `MemoryBudget.Calculate` needs roughly 50 MiB before `MergeParallelism` reaches 2, so every figure above is a `MergeParallelism` 1 figure. `-- --flatness-parallel-merge` reruns it at 64 MiB — the smallest round budget clearing that threshold — over the same sizes plus 1 GiB, with every worker confirming `MergeParallelism` 2.

| Input | Peak managed heap | Peak vs budget |
|---|---|---|
| 1 MiB | 63.94 MiB | −0.1% |
| 10 MiB | 67.57 MiB | +5.6% |
| 100 MiB | 74.10 MiB | +15.8% |
| 1 GiB | 82.26 MiB | +28.5% |

A 28.6% spread against the same tolerance, tighter than the 16 MiB matrix's 41.2% — the partitioned path adds a small fixed amount of state on top of a larger floor, and its overshoot is the same GC residual. Both matrices are one recorded run, not a bound on every machine.
