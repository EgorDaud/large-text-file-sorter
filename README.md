# Large Text File Sorter

Two console programs on .NET 10. **`TestFileGenerator`** writes a `<Number>. <String>` file of a given size in bytes, reproducibly from a seed, with a controllable proportion of lines sharing a string part. **`FileSorter`** sorts such a file in bounded memory, at roughly a hundred gigabytes — string part ascending, number only to break a tie on it:

```
415. Apple                              1. Apple
30432. Something something something    415. Apple
1. Apple                       ──▶      2. Banana is yellow
2. Banana is yellow                     30432. Something something something
```

**A 100 GiB sort at a 6 GiB budget takes 313.4 s**; GNU `sort` takes 374.4 s on a 20 GiB file this one does in 54.2 s. Recorded against this snapshot: 492 passing tests, warning-free Release builds on Linux and Windows, end-to-end sorts at 20 and 100 GiB.

**Reviewing this?** [Run it in four commands](#quick-start) · [Open the code](#where-things-are) · [Check the numbers](#results) · [What it doesn't do](#limits)

---

## Quick start

Requires the .NET 10 SDK and nothing else. Build, generate 100 MiB, sort it, verify:

```bash
dotnet build -c Release
dotnet run -c Release --project src/TestFileGenerator -- data.txt --size 100MiB --seed 42
dotnet run -c Release --project src/FileSorter -- data.txt sorted.txt --memory 256MiB
dotnet run -c Release --project src/FileSorter -- --verify data.txt sorted.txt
```

At that size the sort produces a dozen or so runs — the count follows from your core count, since the budget divides across `--parallelism + 2` buffers — and merges them in one pass. `--verify` prints 2,892,340 lines and a matching hash for both files, then `verified`. Both programs write only to stderr, so stdout stays empty on success. The same four commands sort a hundred gigabytes at larger values.

---

## Command line

`sorter <input> <output> [options]`, `sorter --verify <input> <output>`, `generator <output> --size 100GiB [options]`.

| Option | Default | Meaning |
|---|---|---|
| `--temp` | the output file's directory | Parent of this invocation's run files; see [Disk requirements](#disk-requirements) |
| `--memory` | 1GiB | Budget for the program's own buffers, in both phases; not a process working-set limit |
| `--max-line` | 64KiB | Longest accepted line; a longer line is malformed |
| `--parallelism` | processor count | Concurrent sort-and-spill operations in phase one, and the ceiling on phase two's merge workers |
| `--pipeline` | akka | Which run-generation scheduler to use |
| `--verify` | — | Checks `<output>` is a valid sort of `<input>`, for results too large to re-sort |
| `--size` | — | Generator output size; accepts `B`, `KiB`, `MiB`, `GiB` or a bare byte count, here and in the sorter |
| `--seed` | 0 | The same seed reproduces byte-identical generator output |
| `--duplicate-ratio` | 0.1 | Proportion of lines drawn to share a string part, measured over the whole file. `0` means none shares with any other; any positive ratio guarantees at least one shared pair |

`--memory` and `--max-line` are configurable because they make the expensive paths cheap to test: a small budget forces a multi-pass merge over a few kilobytes, a small line limit forces the oversized-line path on one screen.

### Exit codes

| Code | Program | Meaning |
|---|---|---|
| 0 | both | Success |
| 1 | sorter | Malformed input; byte offset, line number and a truncated preview on stderr |
| 2 | sorter | Insufficient space on the temp or output volume; required, available and the directory examined |
| 3 | both | Invalid arguments, including an unreplaceable destination |
| 4 | sorter | `--verify` found the output out of order, unterminated, or disagreeing with the input's line count or hash |
| 130 | sorter | Cancelled |

Ctrl+C and handled failures run cleanup before exit; an abrupt termination cannot, and during a multi-run merge it can leave a partial destination file. Each phase reports progress and closing diagnostics on stderr.

---

## Where things are

Vertical slices: every folder is named for a feature and holds everything that feature needs, so a change touches one folder. No folder is named for a technical layer.

```
src/FileSorter/   Program.cs (dispatch, Ctrl+C, exit codes), Startup/ (arguments, budget
                  arithmetic, free-space precheck, temp lifetime), LineFormat/ (parser,
                  cursor, comparator), RunGeneration/ (chunk read, radix sort, spill,
                  buffer pool, both pipelines), Merging/ (loser tree, splitters, pass
                  planner, placement), Verification/ (--verify's two scans)
src/TestFileGenerator/Generation/   vocabulary, line composition, writing
tests/            mirrors the production folders, plus Integration/, EndToEnd/, Properties/
benchmarks/       run generation, scheduler overhead, spill work, merge, partitioned merge
```

The two programs share no library — only the knowledge that the separator is a period. Duplicating one constant beats a shared project that would become a layer-named folder by another name, and a test feeds generated output through the sorter's parser, so format drift fails immediately.

### Where to read more

[docs/design-spec.md](docs/design-spec.md) has the type-level contracts and the memory arithmetic in closed form; [docs/test-strategy.md](docs/test-strategy.md) the behavioural contract, test identifiers and residual risks; [docs/measurements.md](docs/measurements.md) the timings, hardware and measurement limits. These three define behaviour and evidence.

---

## How it works

Classic external merge sort, in two phases.

**Phase one, run generation.** The input is read sequentially into large pooled buffers, each parsed once into a chunk: one raw byte buffer plus an array of small line descriptors holding each line's offset, length, parsed number and string-part offset. Sorting reorders the descriptor array only — line bytes are never moved, copied or decoded. Chunks are sorted and spilled to temporary run files concurrently, while the reader fills the next. The chunk sort is most-significant-digit radix, permuting in place, with the first eight bytes of each string part cached in the descriptor so the early levels split the chunk without touching a line; a bounded descent hands the remainder to a comparison sort under the full comparator.

**Phase two, merging.** Runs are merged k-way through a loser tree holding one head per run, never a whole run. When the run count exceeds the merge fan-in, a planner groups runs into passes, each strictly reducing the count until one remains.

When one pass covers every run — the ordinary case at a comfortable budget — it is **split by key range across several workers**. Sampled quantiles become splitter keys, and a binary search over byte offsets locates, for every run and every splitter, the first line at or above it. Since run files neither lose nor gain data, each worker learns the exact place in the output where its bytes belong, so the output is preallocated once (sparse on Windows) and the workers write into disjoint ranges **with no coordination at all**. Three checks guard that: each worker asserts its bytes written equal its slice length, its writer refuses any write crossing into a neighbour's range, and the executor checks the sum.

**The single-run shortcut.** When phase one produces exactly one run, phase two moves it to the output path instead of rewriting it, saving a full read and a full write. A move across volumes falls back to a staged copy.

### The memory guarantee

The guarantee does not rest on having run a hundred gigabytes. It is the stronger and cheaper claim: **the program's own buffers are a function of configuration alone, independent of input size** — after which capacity follows from arithmetic plus free disk.

```
phase one:  (parallelism + 2) × (chunkSize + descriptorArray) + chunkSize + parallelism × spillBuffer            ≤ budget
phase two:  mergeWorkers × fanIn × (2 × readAheadBuffer + descriptorArray) + mergeWorkers × outputBuffer + mergeMetadata  ≤ budget
```

Both phases fit inside the same budget, not a fresh one. At 4 GiB with the shipped defaults the solve gives 8 merge workers, a fan-in of 2,048 each and windows of 87,193 bytes apiece — enough to merge 100 GiB in one pass, using all but 410 KB of the budget. Those windows are eight times smaller than a sequential merge's, which is the point rather than a cost: 6.6 GiB of resident windows would evict the page cache the runs are read through. What makes the guarantee structural is the bounded line length, which forces every line inside a chunk and so bounds the carry between one read and the next.

Two qualifications. **One term does scale with input:** the run path list is O(input / chunk size) — about 110 KB at the 4 GiB target, but roughly 25 MB for a 4 MiB budget sorting 100 GiB. **And it covers configured buffers, not process working set**, recorded separately under [Results](#results).

[docs/design-spec.md](docs/design-spec.md) has the closed form.

### Disk requirements

Free space is checked on both `--temp` and the output volume before any byte is read; a shortfall fails immediately with the required amount, the available amount and the directory examined. Sharing one volume — the default — needs roughly twice the input. What the check cannot retire is a volume filled by another process mid-run.

`--temp` names a *parent*: each invocation creates its own `sorter-<pid>-<8 hex>` subdirectory beneath it, removed on both success and failure, so two sorts can share one `--temp` safely. `--temp` itself is never removed, since neither invocation can decide it owns it.

**Three of the four ways this program produces output never touch an existing destination until they replace it** — the generator's write, the empty-input output and the single-run copy fallback each stage into a `<destination>.<8 hex>.partial`, moved into place only once writing succeeded. **A multi-run merge is the exception:** it opens the destination with `FileMode.Create`, so a valid output already there is gone the moment the merge begins. Staging that much data would double the disk this path needs, on the run most likely to be sized against the disk's limit already.

### Verifying a result

`sorter --verify <input> <output>` reads each file once, in parallel, in about 8.2 MiB whatever the file sizes. It asserts that the output is non-decreasing under the sorter's own three-level order, and that both files agree on line count and on an order-independent hash of every line. Two caveats: it reuses `LineParser` and `LineOrder.Compare`, so it cannot catch a defect in them; and the hash is a sum, so a pathological pair of dropped-and-inserted lines whose hashes cancel could escape it.

---

## Results

| Input | Budget | Wall clock | Phase one | Merge | Passes |
|---|---|---|---|---|---|
| 20 GiB | 4 GiB | **56.3 s** | 30.6 s, 186 runs | ~26 s, 8 workers | 1 |
| 100 GiB | 6 GiB | **313.4 s** | 156.0 s, 617 runs | ~157 s, 8 workers | 1 |

Both are `--verify` clean, on one Windows machine with input, temporary files and output on one NVMe volume, so reads and writes contend. **GNU `sort` takes 374.4 s where this one takes 54.2 s**, measured as a paired series on a 20 GiB file under flags chosen to favour GNU — and produces byte-identical output, making it an outside oracle at a scale the test suite cannot reach.

[docs/measurements.md](docs/measurements.md) holds the hardware, per-run diagnostics, GNU methodology and caveats, and the memory-flatness matrices. Three things it records change how the table reads. **The 20 GiB row is the fastest of ten consecutive runs**, taken while the drive was still rested; the median across the ten was 69.9 s, and the gap is output-wait on a drive kept busy rather than anything in the sort. **The merge tracks the drive rather than the code** — summed output-wait ranged 279.5 s to 1325.0 s across eight otherwise identical 100 GiB runs, and the wall clock followed it. **Peak working set runs 1.8–10.0% above `--memory`**, because the guarantee bounds buffers rather than the process, and the 100 GiB input repeats one 20 GiB block, so it is duplicate-heavy rather than representative.

---

## Ordering rules

The assignment leaves several things unspecified. Each is decided here, identically in the code, the tests and this table; [docs/test-strategy.md](docs/test-strategy.md) argues each one and pins it to a test.

| Area | Rule |
|---|---|
| Separator | The first period in the line, not the first period-then-space pair. One space immediately following is consumed if present; otherwise the string part starts at the next byte. |
| Number | A signed 64-bit integer, full range. Negatives and leading zeros are accepted; a value outside the range is malformed, as are surrounding whitespace and an empty number field. |
| Ordering | Three levels: string part ascending, ordinal over raw bytes; then number ascending; then raw line bytes ascending as a deterministic tie-break. |
| Case | Ordinal and case-sensitive, so uppercase orders before lowercase. Culture-aware collation was rejected: it is not stable across machines or ICU versions, so the same input would sort differently on your machine than on mine, and could not be verified byte for byte. |
| Encoding | Never validated. Input is assumed UTF-8 and the string part compared as opaque bytes, so invalid sequences pass through and order by their byte values. Validating would mean decoding every line on the hot path to reject inputs the ordering does not depend on. |
| Malformed input | Fail fast on the first malformed line, reporting byte offset, line number and a truncated preview. Skipping bad lines would ship a wrong answer that looks well formed. |
| Input terminators | Both `\n` and `\r\n`, including mixed in one file; exactly one carriage return preceding the line feed is stripped. A bare carriage return ending the file is a terminator too. |
| Output terminators | Always `\n`, so output bytes are deterministic regardless of input convention. Every line is terminated, including the last. |
| Empty cases | An empty string part is valid and orders first; the region after the final terminator is not a line; empty input gives empty output and exit 0. |
| Generator sizing | Complete lines until the next would exceed the requested byte size. Never overshoots, never truncates mid-line. |

**The third comparison level** exists because two lines can tie on both stated keys while differing in their bytes: leading zeros are accepted, so `007. Apple` and `7. Apple` tie, as do `+5. Apple` and `5. Apple`. Without it their order would fall to which chunk each landed in, and chunk boundaries follow from `--memory` — so the same input under two budgets would produce different bytes. It also makes stability moot, since equality now means byte-identical.

---

## Limits

### What isn't proven

- **There is no byte oracle at 100 GiB.** That run is checked for order, line count and content hash against its own input, not against an independently produced sort; GNU `sort` supplies that outside check at 20 GiB only. The multi-pass path is exercised at the other end of the knob instead — the 20 GiB file at a 256 MiB budget reached 3,330 concurrent temporary files and a two-pass merge at full fan-in.
- Concurrency is argued structurally rather than proven. Tests cover the interleavings that happened to occur; the weight is carried by the shape — phase one's shared mutable state is the buffer pool and the run registry, and phase two's workers share only a progress counter of two interlocked adds. **One gap is real:** a pipeline aborting between the reader producing a chunk and the spiller receiving it does not return that buffer to the pool. Benign — the run is over and the pool dies with the process — but every other release path is guaranteed.

### What it deliberately doesn't do

- **Memory-mapped I/O and SIMD comparison were considered and not built.** The merge workers spend well over half their time inside awaited writes, so the sort is bound by the drive's write rate and neither technique touches that bound. The radix chunk sort is the one custom strategy that does address a measured cost — hence the one that ships, benchmarked.
- A partitioned merge cannot balance adversarial data. The slices are key ranges, so byte-identical lines cannot be split: an input of one repeated key gives one worker everything. The generated keys are uniform enough for sampled quantiles to balance the slices; nothing guarantees that for real data. The imbalance is measured and printed rather than acted on.
- **Fail fast has a real cost:** one bad byte late in a large file discards the work done so far. Still the right default, since continuing past corruption ships a wrong answer that looks well formed. A lenient mode diverting malformed lines aside is deliberately not built.
- `--memory` and `--parallelism` must be chosen together. A budget too small for the fixed write-buffer cost is rejected with the minimum that would work, rather than quietly dropping to a lower parallelism — which would let a *larger* budget produce a *smaller* chunk.
- **Sorting the sorter's own output again is not a fixpoint of it**, because a `\r` immediately before a `\n` could be a terminator's second half or content, and the settled rule treats it as the terminator. That is the format's ambiguity, not a defect; a test pins the exact loss (`SortRoundTripTests`, ET-05).

Smaller accepted costs, detailed in [docs/test-strategy.md](docs/test-strategy.md): the radix sort's ~6% penalty on long runs of byte-identical string parts, the splitter search's brief 16 MiB allocation outside the guarantee, the partitioned merge's larger handle count, and disk exhaustion arriving after the startup check.

---

## Testing

Every correctness-bearing decision sits behind a plain, dependency-free seam, so the tier that proves the program right is also the cheapest tier. Unit tests cover parsing, ordering, chunk boundaries, sorting, merging, pass planning, pooling, budget arithmetic and capacity checking; integration tests cover end-to-end runs, temp cleanup, multi-pass behaviour, cancellation and failure propagation; streaming-layer tests cover flow control, deterministic buffer release and error propagation against both pipelines. There are no interfaces in the sorter and no mocking framework anywhere — seams are pure functions over spans, `Stream` parameters and two delegates.

**The highest-value test** generates a random file, runs the full pipeline, and asserts the output is byte-identical to a naive in-memory reference sort — an oracle that rejects wrong order, dropped or duplicated lines, mangled bytes, wrong terminators and a missing trailing newline alike. That only means something if the oracle is independent, so it is written separately and **must not call the production comparator**: a comparator with a case-sensitivity defect would otherwise produce identical wrong output on both sides and the test would pass. Output is asserted identical across two budgets, two degrees of parallelism, both pipelines and all four final-line terminations — the cheapest race detector in the suite.

`CsCheck` is the only dependency beyond Akka.Streams and xunit, and it is there for shrinking: a random failure over a few dozen lines is not actionable until reduced to the two or three that disagree.

**Benchmarks** never run under `dotnet test`: `dotnet run -c Release --project benchmarks/FileSorter.Benchmarks`, with `--flatness` and `--flatness-parallel-merge` measuring the memory guarantee end to end.

**The build gate.** `Directory.Build.props` asks for `AnalysisMode=Recommended` and `EnforceCodeStyleInBuild`, and `TreatWarningsAsErrors` turns both the quality rules and the `.editorconfig` style rules into build failures — so `dotnet build -c Release` is the whole check, with no separate lint command to drift out of sync. CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs it and the tests on Linux and Windows, because a program about file paths, byte offsets and terminators should be shown to work on both.

---

## Why Akka.Streams, and why two pipelines

Phase one's orchestration ships in two interchangeable implementations behind one delegate, selectable with `--pipeline`: **`akka`** (default) uses `Source.UnfoldAsync` with `SelectAsyncUnordered` over sort-and-spill; **`channels`** uses a bounded `Channel<Chunk>` and `Parallel.ForEachAsync`, with no third-party dependency. Both are about thirty lines, and neither owns a resource.

**What bounds in-flight work is the buffer pool, in both cases** — it holds `parallelism + 2` slots, so acquisition blocks the reader before either scheduler's own bound binds. Neither supplies the backpressure; both inherit it. That matters because the alternative claim is untestable: an assertion written against a scheduler's own bound would pass against a scheduler with no flow control at all, so the suite asserts against outstanding pool buffers instead.

Akka.Streams is therefore not doing the memory-bounding work. It provides composition and failure semantics that the Channels implementation reproduces by hand, and keeping both turned that into a measured claim — two asymmetries surfaced only under comparison. **Exception type:** Akka wraps a stage failure in an `AggregateException` where `await` unwraps the Channels path, so without an assertion that both surface the same type, one corrupt file would have produced different exit codes under different pipelines. **Completion:** Akka's task completes the instant the graph faults while offloaded sibling spills keep writing, so that pipeline must track every read and spill it starts — which `Parallel.ForEachAsync` gives Channels for nothing.

**The default is `akka`, defended on those failure semantics rather than on speed;** an operator who does not need them can pass `--pipeline channels`.
