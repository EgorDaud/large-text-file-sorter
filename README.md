# Large Text File Sorter

Two console programs on .NET 10. **`TestFileGenerator`** writes a `<Number>. <String>` file of a given size, reproducibly from a seed, with a controllable proportion of lines sharing a string part. **`FileSorter`** sorts such a file of around a hundred gigabytes in bounded memory, by string part, then by number to break a tie:

```
415. Apple                              1. Apple
30432. Something something something    415. Apple
1. Apple                       ──▶      2. Banana is yellow
2. Banana is yellow                     30432. Something something something
```

**A 100 GiB sort at a 6 GiB budget took 313.4 s**, the fastest of five runs at that budget; GNU `sort` took 374.4 s on a 20 GiB file this one sorted in 54.2 s, the faster of two paired samples ([Results](#results) has the rest). This snapshot has 595 passing tests (68 generator, 527 sorter) and warning-free Release builds, checked by CI on Linux and Windows. The 20 and 100 GiB sorts and the GNU comparison were recorded on the 2026-09-09 build, with the `akka` pipeline.

**Reviewing this?** [Run it in four commands](#quick-start) · [Open the code](#where-things-are) · [Check the numbers](#results) · [What it doesn't do](#limits)

---

## Quick start

Requires only the .NET 10 SDK. Build, generate 100 MiB, sort it, verify:

```bash
dotnet build -c Release
dotnet run -c Release --project src/TestFileGenerator -- data.txt --size 100MiB --seed 42
dotnet run -c Release --project src/FileSorter -- data.txt sorted.txt --memory 256MiB
dotnet run -c Release --project src/FileSorter -- --verify data.txt sorted.txt
```

`--verify` prints 2,892,340 lines and a matching hash for both files, then `verified`. Both programs write only to stderr, so stdout stays empty on success.

---

## Command line

`sorter <input> <output> [options]`, `sorter --verify <input> <output>`, `generator <output> --size 100GiB [options]`.

| Option | Default | Meaning |
|---|---|---|
| `--temp` | the output file's directory | Parent of this invocation's run files; see [Disk requirements](#disk-requirements) |
| `--memory` | 1GiB | Budget for the program's own buffers, in both phases; not a process working-set limit |
| `--max-line` | 64KiB | Longest accepted line; a longer line is malformed |
| `--parallelism` | processor count | Concurrent sort-and-spill operations in phase one, and the ceiling on phase two's merge workers |
| `--pipeline` | channels | Run-generation scheduler: `channels` or `akka` |
| `--verify` | — | Checks `<output>` is a valid sort of `<input>`, for results too large to re-sort |
| `--size` | — | Generator output size; `B`, `KiB`, `MiB`, `GiB` or a bare byte count, here and in the sorter |
| `--seed` | 0 | Same seed, byte-identical generator output, on the same .NET version (`System.Random`'s seeded sequence may change across major versions) |
| `--duplicate-ratio` | 0.1 | Proportion of lines sharing a string part, over the whole file. `0` means none; any positive ratio guarantees at least one shared pair |

`<input>` and `<output>` must be different files: the same path, however spelled, exits 3 before anything is read or written. Symlinks and hard links to the input are not detected.

`--memory` and `--max-line` exist mainly for testing: a small budget forces a multi-pass merge over a few kilobytes, a small line limit forces the oversized-line path.

### Exit codes

| Code | Program | Meaning |
|---|---|---|
| 0 | both | Success |
| 1 | sorter | Malformed input; byte offset, line number and a truncated preview on stderr |
| 2 | sorter | Insufficient space on the temp or output volume; required, available and the directory examined |
| 3 | both | Invalid arguments, including an unreplaceable destination |
| 4 | sorter | `--verify` found the output out of order, unterminated, or disagreeing with the input's line count or hash |
| 5 | both | An I/O failure after startup validation passed, such as a full disk; the sorter prints one `I/O error:` line, the generator one line naming the output file |
| 130 | both | Cancelled |

A failed generator write exits 5 and leaves an existing destination untouched. The sorter exits 3 for an unwritable destination on empty or single-run input, 5 when a multi-run merge cannot open it.

The first Ctrl+C or SIGTERM cancels, cleans up and exits 130; a second Ctrl+C kills the process without cleanup. SIGHUP is not handled, so a run under `nohup` outlives the session. A kill during a multi-run merge can leave a partial destination file.

---

## Where things are

Each folder is one feature slice; `Infrastructure/` holds the cross-cutting plumbing.

```
src/FileSorter/   Program.cs (dispatch)
                  Cli/            SortCommand, VerifyCommand, argument parsing,
                                  free-space preflight, exception-to-exit-code mapping
                  Planning/       memory budget and temp-capacity arithmetic
                  Infrastructure/ temp lifetime, progress, unbuffered streams
                  LineFormat/     parser, cursor, comparator
                  RunGeneration/  chunk read, radix sort, spill, buffer pool, both pipelines
                  Merging/        loser tree, splitters, pass planner, partitioned merge, placement
                  Verification/   --verify's two scans
src/TestFileGenerator/   Program.cs, CommandLine.cs, GenerateCommand.cs, Generation/ (vocabulary, line
                  composition, writing, staged output)
src/Shared/       behaviour both programs must agree on: ExitCodes, ConsoleCancellation (Ctrl+C and
                  SIGTERM), StagingFile (write-then-replace), ByteSize, Arguments
tests/            follows the production folders, plus Integration/, EndToEnd/,
                  Properties/, Support/
benchmarks/       chunk sort, run generation, scheduler overhead, spill work, merge,
                  partitioned merge, memory flatness
```

The line grammar is deliberately not shared beyond the period separator; a test feeds generated output through the sorter's parser so format drift fails immediately.

[docs/design-spec.md](docs/design-spec.md) has the type-level contracts and the memory arithmetic in closed form; [docs/test-strategy.md](docs/test-strategy.md) the behavioural contract, test identifiers and residual risks; [docs/measurements.md](docs/measurements.md) the timings, hardware and measurement limits.

---

## How it works

Classic external merge sort, in two phases.

**Phase one, run generation.** The input is read sequentially into pooled buffers, each parsed once into a chunk: the raw bytes plus a descriptor per line holding its offset, length, parsed number and string-part offset. Sorting reorders descriptors only; line bytes are never moved, copied or decoded. Chunks are sorted and spilled concurrently while the reader fills the next. The sort is an in-place MSD radix sort; the first eight bytes of each string part are cached in the descriptor, so early levels never touch line bytes, and small or deep buckets fall back to a comparison sort.

**Phase two, merging.** Runs are merged k-way through a loser tree holding one head per run. When the run count exceeds the fan-in, a planner groups runs into passes, each strictly reducing the count until one remains.

When one pass covers every run, the usual case, it is **split by key range across workers**. Sampled quantiles become splitter keys; a binary search in each run finds where each splitter falls, which gives every worker its exact output range. The output is preallocated once (sparse on Windows) and workers write disjoint ranges **with no coordination**; each writer refuses writes outside its slice, and byte counts are checked per worker and in total.

When phase one produces exactly one run, it is moved to the output path rather than rewritten; across volumes this falls back to a staged copy.

### The memory guarantee

**The program's own buffers are a function of configuration alone, independent of input size**, so capacity follows from arithmetic plus free disk, not from a large run that happened to fit.

```
phase one:  (parallelism + 2) × (chunkSize + descriptorArray) + chunkSize + parallelism × spillBuffer            ≤ budget
phase two:  mergeWorkers × fanIn × (2 × readAheadBuffer + descriptorArray) + mergeWorkers × outputBuffer + mergeMetadata  ≤ budget
```

Both phases share one budget. The figures here are the solver's arithmetic, not measurements. At 4 GiB with `--parallelism` 8 or more and the other defaults the solve gives 8 merge workers, a fan-in of 2,048 each and windows of 87,193 bytes: one pass over 100 GiB, with all but 410 KB of the budget used. Each worker's window is an eighth of a sequential merge's, so eight workers cost no more window memory than one. The bounded line length keeps every line inside a chunk, which bounds the carry between reads.

**One term scales with input:** the run path list, estimated at about 110 KB at the 4 GiB target but roughly 25 MB for a 4 MiB budget sorting 100 GiB. **And it bounds buffers, not process working set** (see [Results](#results)).

### Disk requirements

Free space on `--temp` and the output volume is checked before any byte is read (exit 2). One shared volume, the default, needs roughly twice the input. Another process filling the volume mid-run is not covered.

`--temp` names a *parent*: each invocation creates and always removes its own `sorter-<pid>-<8 hex>` subdirectory, so concurrent sorts can share it. `--temp` itself is never removed.

The generator, empty input and the single-run placement stage into `<destination>.<8 hex>.partial` and rename it into place, so an existing destination survives a failure. **A multi-run merge is the exception:** staging would double the disk needed on the run most likely to be sized against the disk's limit, so it unlinks the destination and writes afresh. Unlinking rather than truncating means an output linked to the input never writes through to it.

### Verifying a result

`sorter --verify <input> <output>` reads both files once, in parallel, in about 8.2 MiB regardless of size. It checks the output is non-decreasing under the sorter's order and that both files agree on line count and an order-independent hash of every line. It reuses `LineParser` and `LineOrder.Compare`, so cannot catch their defects, and its summed hash would miss a dropped and an inserted line whose hashes cancel.

---

## Results

| Input | Budget | Wall clock | Phase one | Merge | Passes |
|---|---|---|---|---|---|
| 20 GiB | 4 GiB | **56.3 s** | 30.6 s, 186 runs | ~26 s, 8 workers | 1 |
| 100 GiB | 6 GiB | **313.4 s** | 156.0 s, 617 runs | ~157 s, 8 workers | 1 |

One Windows machine, with input, temporary files and output on one NVMe volume. The 20 GiB output was `--verify`d; of the eight 100 GiB runs, two were, and the record does not say whether the 313.4 s run was one of them. **GNU `sort` took 374.4 s where this one took 54.2 s** (the other sample: 373.3 s against 73.6 s), in a paired series on a 20 GiB file under flags chosen to favour GNU, and its output is byte-identical: an outside oracle at a scale the test suite cannot reach.

[docs/measurements.md](docs/measurements.md) has the hardware, per-run diagnostics, GNU methodology and memory-flatness matrices. Three findings there qualify the table:

- **The 20 GiB row is the fastest of ten consecutive runs**, on a rested drive; the median was 69.9 s, the gap being output-wait.
- **The 100 GiB row is the fastest of five runs at 6 GiB**, which took 313.4–387.9 s.
- **The merge tracks the drive, not the code**: summed output-wait ranged from 279.5 s to 1325.0 s across eight 100 GiB runs at 6 and 8 GiB budgets, and wall clock followed.
- **Peak working set runs 1.8–10.0% above `--memory`**, because the guarantee bounds buffers, not the process. The 100 GiB input repeats one 20 GiB block, so it is duplicate-heavy rather than representative.

---

## Ordering rules

Decisions on what the assignment leaves open; [docs/test-strategy.md](docs/test-strategy.md) pins each one to a test.

| Area | Rule |
|---|---|
| Separator | The first period in the line. One following space is consumed if present; otherwise the string part starts at the next byte. |
| Number | An optional `+` or `-` then one or more ASCII digits, in the signed 64-bit range. Negatives and leading zeros are accepted; out-of-range values, surrounding whitespace, any other byte (including NUL) and an empty field are malformed. |
| Ordering | String part ascending, ordinal over raw bytes; then number ascending; then raw line bytes as a deterministic tie-break. |
| Case | Ordinal and case-sensitive, so uppercase orders first. Culture-aware collation varies across machines and ICU versions, so its output could not be verified byte for byte. |
| Encoding | Never validated: string parts are opaque bytes, so invalid UTF-8 passes through. Validating would decode every line on the hot path without changing the order. |
| Malformed input | Fail fast on the first malformed line (exit 1). Skipping bad lines would ship a wrong answer that looks well formed. |
| Input terminators | `\n` and `\r\n`, mixed freely; exactly one carriage return before the line feed is stripped. A bare carriage return ending the file is also a terminator. |
| Output terminators | Always `\n`, including after the last line. |
| Empty cases | An empty string part is valid and orders first; empty input gives empty output and exit 0. An empty line has no separator and is malformed, so a file ending in a doubled newline fails with exit 1 at its last line, after the whole run. |
| Generator sizing | Complete lines until the next would exceed the requested size: never overshoots, never truncates mid-line, can end up to one line short. |

**The third comparison level** exists because `007. Apple` and `7. Apple` tie on both stated keys, as do `+5. Apple` and `5. Apple`. Without it their order would depend on chunk boundaries, so two `--memory` values would produce different bytes. It also makes stability moot.

---

## Limits

### What isn't proven

- **There is no byte oracle at 100 GiB.** The 100 GiB runs that were verified are checked for order, line count and content hash against their own input; GNU `sort` supplies an independent check at 20 GiB only. The multi-pass path is exercised instead at a 256 MiB budget, where the 20 GiB file reached 3,330 concurrent temporary files and a two-pass merge at full fan-in (a development run, not recorded in [measurements](docs/measurements.md)).
- **Concurrency is argued structurally, not proven.** Phase one shares only the buffer pool and run registry; phase two's workers share only an interlocked progress counter. **One gap is real:** a pipeline aborting between the reader producing a chunk and the spiller receiving it leaks that buffer, which is benign since the pool dies with the process.

### What it deliberately doesn't do

- **No memory-mapped I/O or SIMD comparison.** Merge time followed the drive in every recorded run (summed output-wait of 28–103 s at 20 GiB and 280–1,325 s at 100 GiB, [measurements](docs/measurements.md) sections 1 and 2), and neither technique would change the write rate. The radix chunk sort ships because it addresses a measured cost.
- **A partitioned merge cannot balance adversarial data.** Identical lines cannot be split across key ranges, so one repeated key gives one worker everything. A partition with at most one non-empty slice falls back to a sequential merge, noted on stderr; any other imbalance is merged in parallel and printed.
- **Fail fast has a cost:** one bad byte late in a large file discards the work done. A lenient mode diverting malformed lines aside is deliberately not built.
- **`--memory` and `--parallelism` must be chosen together.** A budget too small for the fixed write-buffer cost is rejected with the minimum that would work, rather than silently lowering parallelism, which would let a *larger* budget produce a *smaller* chunk.
- **Re-sorting the sorter's own output is not a fixpoint.** A content `\r` before the `\n` reads back as half a terminator; that is the format's ambiguity, pinned by `SortRoundTripTests` (ET-05).

Smaller accepted costs: the radix sort's penalty of up to 11% when every string part is identical ([measurements](docs/measurements.md#6-inside-the-chunk-sort)), the splitter search's brief 16 MiB allocation outside the guarantee and the partitioned merge's larger handle count ([design spec](docs/design-spec.md#77-rangepartitioner-and-the-partitioned-merge)), and disk exhaustion after the startup check ([test strategy](docs/test-strategy.md#residual-risks)).

---

## Testing

Correctness-bearing logic sits behind plain seams (pure functions over spans, `Stream` parameters and two delegates), so the tier that proves the program right is also the cheapest. The sorter has no interfaces and the suite no mocking framework.

**The highest-value test** runs the full pipeline on a random file and asserts byte-identity with a naive in-memory reference sort, which catches wrong order, lost or duplicated lines, mangled bytes and wrong terminators alike. The reference **does not call the production comparator**, so a comparator defect cannot cancel out. Output is also asserted identical across two budgets, two degrees of parallelism, both pipelines and all four final-line terminations: the cheapest race detector in the suite.

Beyond Akka.Streams, xunit and BenchmarkDotNet (benchmarks only), the one dependency is `CsCheck`, for shrinking: a random failure is not actionable until reduced to the two or three lines that disagree.

**Benchmarks** run outside `dotnet test`: `dotnet run -c Release --project benchmarks/FileSorter.Benchmarks`, plus `--flatness` and `--flatness-parallel-merge` to measure the memory guarantee end to end.

**The build gate.** `Directory.Build.props` enables the SDK analyzers at `Recommended` (`All` for performance, reliability and usage) with `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`, so `dotnet build -c Release` is the whole lint. [CI](.github/workflows/ci.yml) builds and tests in Release on Linux and Windows, plus a Debug test leg so the `Debug.Assert` invariants run.

---

## Why Akka.Streams, and why two pipelines

Phase one's orchestration has two interchangeable implementations behind one delegate: **`channels`** (default) uses a bounded `Channel<Chunk>` and `Parallel.ForEachAsync`; **`akka`** uses `Source.UnfoldAsync` with `SelectAsyncUnordered` over sort-and-spill. Each is one short file that owns no resource.

**The buffer pool bounds in-flight work in both**: its `parallelism + 2` slots block the reader before either scheduler's own bound applies, so the backpressure tests assert on outstanding pool buffers. Akka.Streams supplies composition and failure semantics, not memory bounding, and running both pipelines exposed two asymmetries. **Exception type:** Akka wraps a stage failure in an `AggregateException` where `await` unwraps the Channels path, so without a test pinning both, one corrupt file would give different exit codes per pipeline. **Completion:** Akka's task completes the instant the graph faults while sibling spills keep writing, so that pipeline tracks every read and spill it starts, which `Parallel.ForEachAsync` gives Channels for free.

**The default is `channels`: no third-party dependency, and nothing measurable given up.** The interleaved ten-run comparison in [measurements](docs/measurements.md) (section 4.1) puts Akka 3.3% slower on phase one at 20 GiB. `--pipeline akka` remains the opt-in for its composition and failure semantics, not for speed.
