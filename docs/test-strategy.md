# Test Strategy and Test-Driven Specification

Scope: a generator that writes a seeded file of a requested byte size with a controllable share of repeated string parts, and a sorter that orders `<Number>. <String>` lines by string ascending, then number ascending, and stays correct and memory-bounded at about 100 GiB. The design under test (external merge sort, loser-tree k-way merge, pooled byte buffers with line descriptors, range-partitioned single-pass merge) is in [design-spec.md](design-spec.md).

### Settled behavioural contract

Every case below assumes these rules.

| Area | Rule |
|---|---|
| Separator | The first period. One following space is consumed if present; otherwise the string part starts at the next byte. |
| Number | Signed 64-bit, full range, leading zeros and an explicit sign accepted. Out of range is malformed. |
| Ordering | String part (ordinal over raw bytes), then number, then the raw line bytes as a deterministic final tie-break. |
| Case | Ordinal and case-sensitive: uppercase before lowercase. |
| Malformed input | Fail fast on the first malformed line, reporting byte offset, line number and the line truncated to 128 bytes. |
| Maximum line length | Configurable, default 64 KiB. A longer line is malformed. |
| Encoding | Never validated. Invalid UTF-8 passes through and orders by byte value. |
| Input terminators | `\n` and `\r\n` both accepted; exactly one `\r` before the `\n` is stripped. |
| Output terminators | Always `\n`, including after the last line. |
| Trailing region | The empty region after the final terminator is not a line. |
| Empty string part | Valid; orders before every non-empty string part. |
| Empty input | Valid; empty output, exit 0. |
| Capacity check | About twice the input size of free temp space is verified before any work; a shortfall fails immediately. |
| Test-facing knobs | `--max-line` and `--memory` are configurable so tests can reach large-input paths cheaply. |
| Output path | Must not name the input (full-path comparison, case-insensitive on Windows and macOS): exit 3 before any I/O. Links are not detected but are safe, because the output is replaced, never truncated in place (MP-14). |
| Sort stability | Moot: the third level makes equality mean byte-identical. Not tested. |
| Single run | Moved to the output path; a cross-volume move falls back to a staged copy. The run file survives neither path. |
| Buffer hygiene | Buffers are not cleared on release; consumers read only up to the recorded length. |
| Generator sizing | The largest whole number of lines not exceeding the target. Never overshoots, never truncates a line. |

A lenient mode that diverts malformed lines is a named extension, not built.

---

## Part 1: Test strategy

### Tiers

| Tier | Proves | Location | Cases |
|---|---|---|---|
| Unit (LP, OC, CM, CB, CS, KM, RP, RC, OS, SF, MP, BP, MB, SC, GN, GW) | Parsing, ordering, chunking, sorting, merging, placement, planning, pooling, budgeting, capacity, generation | Per-slice folders | 222 |
| Property (PB) | The composed pipeline matches an independent oracle on random inputs; structural invariants hold | `Properties/` | 24 |
| Integration (IT) | Line-format shapes against real files; capacity probe on a real volume and a UNC path; cross-volume placement | `Integration/` | 9 |
| End-to-end and verify (ET, VF) | Whole runs through `SortCommand.RunAsync` and `VerifyCommand.RunAsync` | `EndToEnd/` | 20, 14 |
| Startup and CLI (TR, PR, CR, CL) | Private temp directory, progress wrapper, exit-code mapping, input/output identity guard | `Infrastructure/`, `Cli/`, `CommandLineTests.cs` | 7, 4, 7, 5 |
| Streaming layer (SL) | Backpressure, prompt cancellation, every spill joined before a strategy returns | `RunGeneration/` | 5 |
| Benchmarks and manual runs | Throughput, allocation, memory flatness, real large-file behaviour | `benchmarks/` | Never in the gate |

The correctness-bearing tier is the cheapest one: every ordering decision sits behind a dependency-free seam testable with in-memory bytes.

Every case carries `[Trait("Case", "XX-NN")]`. The document lists 317 IDs; 315 have a trait, and PB-07 and GN-13 are marked not implemented. Diff the two sets with `grep -oE '^\| [A-Z]{2}-[0-9]+ \|' docs/test-strategy.md` against `grep -rhoE 'Trait\("Case", *"[^"]+"' tests`. IDs are never reused (LP-23 and LP-24 are retired).

### The oracle

The highest-value test sorts a random file through the full pipeline and requires byte identity with a naive in-memory sort (PB-01, PB-02, PB-13, PB-24). It tests the seams where external sorts break (chunk, run and pass boundaries), reaches combinations nobody writes by hand, and rejects every failure mode a partial oracle such as "output is non-decreasing" would accept: dropped, duplicated or mangled lines, and wrong terminators. CsCheck shrinks failures to two or three lines.

The oracle must share no code with production, or a comparator bug produces the same wrong output on both sides. `Support/NaiveReferenceSort.cs` uses no `FileSorter.LineFormat` type. Its line splitting is `Support/NaiveLineFormat.cs`, the suite's single test-side reading of the end-of-file rule, pinned against `ChunkReader`/`RunCursor` (PB-10, PB-11) and against the contract (PB-15 to PB-18). PB-24 carries its own inline oracle so that it doesn't depend on either.

Mutation checks: swapping the primary and secondary keys in `LineOrder.Compare` fails PB-01 and PB-02; comparing the prefix as signed `long` fails PB-24.

### Determinism and the third comparison level

- Generator: same seed and size give byte-identical output on the same .NET version (GN-05). `System.Random`'s sequence is not promised across major versions.
- Sorter: same input gives identical bytes across parallelism, budgets and pipelines (PB-02, CS-08, ET-01, ET-03, PB-12).

Leading zeros and an explicit `+` let two lines tie on both stated keys while differing in bytes. Without the raw-bytes level their order would depend on chunk boundaries, which depend on the budget, so the oracle would fail intermittently. Generated numbers are canonical, so only hand-written cases (OC-13, OC-14, OC-17) and `LineEntryGen`'s deliberate twins reach this level.

### Proving the 100 GiB claim at small scale

The claim is that the program's buffers depend on configuration alone. Given that, capacity is arithmetic plus disk, and a real 100 GiB run only confirms it. The line-length limit is what bounds the reader's carry-over, the last input-dependent state on the read path.

1. Flat working set: `--flatness` (a benchmark mode, outside the test gate) sorts about 1, 10 and 100 MiB at a fixed 16 MiB budget, each in a fresh process, sampling the high-water mark. Tolerance is 60%.
2. Forced multi-pass merge at a minimal budget: output correct and pass count equal to the planner's prediction (MP-10, ET-09, ET-04, PB-24).
3. Bounded occupancy: the pool never exceeds its ceiling (BP-06), and no plan's worst case exceeds the budget (MB-01 to MB-15, PB-20 to PB-23).

### Fail fast

A sorted output of a file that could not be fully read is not possible, and a plausible wrong answer is worse than a loud failure. The cost is that one bad byte late in a large file discards the work. Two boundary rules matter more because of it: treating the trailing region as a line would reject every well-formed file (CB-05), and an unstripped `\r` would silently corrupt the sort key with no error (CB-07, CB-14).

### What isn't unit-testable

- Throughput on real hardware: benchmarks and recorded manual runs ([measurements.md](measurements.md)).
- Real disk exhaustion mid-run caused by another process: write failures are injected; the real condition is manual.
- OS and file-system behaviour (abrupt termination, locked outputs, cross-volume moves): integration tests where a seam isn't possible; the move-to-copy decision itself sits behind a `tryMove` seam (KM-12, KM-13).
- Thread interleavings: tests cover observed schedules only. The design confines phase one's shared state to the pool and run registry, and phase-two workers share only an interlocked progress counter.
- Long-tail effects at real scale: residual risk, Part 3.

### Organisation

The test project mirrors the production slices (`LineFormat/`, `RunGeneration/`, `Merging/`, `Planning/`, `Infrastructure/`, `Cli/`); cross-slice tiers get their own folders. Method names state action, condition and result.

---

## Part 2: Unit test specification

### Unit 1: Line parser (LP)

`LineParser` finds the first period, parses the number and returns the string extent, with no decoding or allocation. The line-length limit is enforced by the splitter (CB-12, CB-16), not here.

| ID | Condition | Expected |
|---|---|---|
| LP-01 | `415. Apple` | 415; extent `Apple` |
| LP-02 | `1. Apple. Banana is yellow` | 1; extent `Apple. Banana is yellow` |
| LP-03 | `7.Apple` (no space) | 7; extent `Apple` |
| LP-04 | `7. . leading dot` | 7; extent `. leading dot` |
| LP-05 | Two spaces after the period | Only one consumed; extent starts with a space |
| LP-06 | `415 Apple` (no period) | Malformed |
| LP-07 | `abc. Apple` | Malformed |
| LP-08 | `12x. Apple` | Malformed |
| LP-09 | `42. ` | 42; empty extent |
| LP-10 | `88.` | 88; empty extent |
| LP-11 | `007. Apple` | 7; raw bytes kept for the tie-break |
| LP-12 | `long.MaxValue` and `long.MinValue` | Exact values |
| LP-13 | One beyond each end | Malformed, never wrapped |
| LP-14 | `-5. Apple` | -5 |
| LP-15 | `-. Apple` | Malformed |
| LP-16 | `0. Apple` | 0 |
| LP-17 | Trailing spaces in the string part | Kept verbatim |
| LP-18 | `.` | Malformed |
| LP-19 | Empty line | Malformed |
| LP-20 | `12345` | Malformed |
| LP-21 | Non-ASCII string part | Exact bytes, undecoded |
| LP-22 | Invalid UTF-8 string part | Parses; exact bytes, no substitution |
| LP-25 | Malformed line deep in a multi-line input, far longer than 128 bytes (in `ChunkReaderTests`, since the reader owns offset and line number) | Report names offset and line number; preview truncated to 128 bytes by `MalformedLineException.PreviewOf` |
| LP-26 | NUL around or inside digits, lone sign, `++1`, `1+`, spaces around digits | Malformed (a trailing NUL is not ignored) |
| LP-27 | `-0`, `+5`, `007`, `long.MinValue`, 25 digits with leading zeros in range | Each parses to its value |

### Unit 2: Ordering comparator (OC)

`LineOrder.Compare`: string part ordinal over bytes, then number, then raw line bytes. Pure, allocation-free, never decodes.

| ID | Condition | Expected |
|---|---|---|
| OC-01 | `9. Apple` vs `1. Banana is yellow` | Apple first |
| OC-02 | `30. Apple` vs `4. Apple` | 4 first |
| OC-03 | Byte-identical lines | Equal both ways |
| OC-04 | `Ban` vs `Banana` | Prefix first |
| OC-05 | Empty vs `A` | Empty first |
| OC-06 | `apple` vs `Apple` | Uppercase first |
| OC-07 | Non-ASCII with known code-point order | Code-point order |
| OC-08 | ASCII vs leading non-ASCII | ASCII first |
| OC-09 | All pairs of a curated set | Antisymmetric |
| OC-10 | All triples of the set | Transitive |
| OC-11 | All pairs of the set | Total |
| OC-12 | Same lines at different buffer offsets | Same result |
| OC-13 | `007. Apple` vs `7. Apple` | Strict order by raw bytes |
| OC-14 | That pair in both argument orders and buffers | Same winner every time |
| OC-15 | `-5`, `0`, `5`, same string | Ascending numerically |
| OC-16 | Invalid UTF-8 differing in one byte | Byte order, no failure |
| OC-17 | `+5. Apple` vs `5. Apple` | Strict order by raw bytes |
| OC-18 | `Ban` vs an extension filling the 8-byte cached prefix | Prefix first |
| OC-19 | 0x00 vs 0x01 inside the cached prefix | Byte order |
| OC-20 | `AAAAAAAAX` vs `AAAAAAAAY` | Ordered by the ninth byte |
| OC-21 | Empty vs a string starting with 0x00 | Empty first |
| OC-22 | `ab` vs `ab\0` vs `ab\0c`; 8 bytes vs its 9-byte extension | Shorter first in each pair |

### Unit 3: Line descriptor and chunk model (CM)

A chunk is one byte buffer plus a descriptor array (offset, length, number, string offset, prefix). Sorting permutes descriptors only.

| ID | Condition | Expected |
|---|---|---|
| CM-01 | Lines of differing lengths | Each extent exact, no terminator bytes |
| CM-02 | Reverse-ordered chunk, sorted | Buffer bytes unchanged |
| CM-03 | First line is the minimum, then the maximum | Correct extent and position |
| CM-04 | Last line with and without a terminator | Correct extent |
| CM-05 | One line | One descriptor; sort is a no-op |
| CM-06 | Empty chunk | No descriptors; sort and spill succeed |
| CM-07 | Numbers across the full range | Descriptor number matches the parser |
| CM-08 | Sorted descriptors written out | Permutation of the input lines |
| CM-09 | `\r\n` and mixed input | Every output line ends in a single `\n` |
| CM-10 | `LineDescriptor.TryCreate` on a valid line at a non-zero offset, and on a line with no period | True with all fields correct; false with a default descriptor |
| CM-11 | `LineStager.TryAdd`: fit, exact fill, overflow, oversized line on full and empty buffer, empty flush | Staged lines go out in one write; oversized lines written directly then `\n`; empty flush writes nothing |

### Unit 4: Chunk boundary splitter (CB)

`LineCursor` splits a block plus carry-over into lines, strips terminators and a leading BOM, and enforces `--max-line`. Carry-over never exceeds the limit plus one byte (a pending `\r`).

| ID | Condition | Expected |
|---|---|---|
| CB-01 | Block ends on a terminator | All lines; empty carry |
| CB-02 | Block ends mid-line | Tail carried and joined correctly |
| CB-03 | Line spanning three or more blocks, within the limit | Emitted once, intact |
| CB-04 | No final terminator | Final line emitted, no phantom line |
| CB-05 | File ends with a terminator | No line for the trailing region; success |
| CB-06 | `\n` only | Correct boundaries |
| CB-07 | `\r\n` throughout | No `\r` in any extent; no empty lines |
| CB-08 | `\r` and `\n` split across blocks | One boundary, no stray `\r` |
| CB-09 | Mixed terminators | All boundaries correct |
| CB-10 | Leading BOM | Not part of the first line |
| CB-11 | BOM then a terminator | No malformed line |
| CB-12 | Line one byte over the limit, across blocks | Malformed at the limit; carry bounded |
| CB-13 | Empty input | No lines |
| CB-14 | `\r\r\n` | One `\r` stripped, one kept as content |
| CB-15 | Lone `\r` mid-line | Ordinary content |
| CB-16 | Line one byte under the limit, across blocks | Emitted normally |
| CB-17 | Block ends on the `\r` of a max-length `\r\n` line | Carried, not rejected |
| CB-18 | Line one over the limit ending `\r\n`, presented whole | Malformed at the line start |
| CB-19 | `\r\n` input with `stripCarriageReturn: false` | `\r` kept in every extent |

### Unit 5: In-memory chunk sorter (CS)

`ChunkSorter.Sort`: an MSD radix over the string bytes, with introsort for ranges of 32 or fewer. Output is a non-decreasing permutation. CS-14 to CS-20 pad with a filler line that sorts last so that the chunk exceeds 32 lines and the radix actually runs.

| ID | Condition | Expected |
|---|---|---|
| CS-01 | Already sorted | Unchanged |
| CS-02 | Reverse order | Ascending |
| CS-03 | All lines identical | Same content |
| CS-04 | Same string, mixed numbers incl. negatives and leading zeros | By number, then raw bytes |
| CS-05 | One line | Unchanged |
| CS-06 | Empty | Unchanged |
| CS-07 | Duplicates, mixed lengths | Same multiset |
| CS-08 | Repeated runs and different chunk splits | Identical order |
| CS-09 | All strings share a top byte | Matches the comparator |
| CS-10 | Empty and non-empty strings | Empty first |
| CS-11 | Same top byte, differ at byte 2 | By byte 2 |
| CS-12 | Same 8 bytes, differ at byte 9 | By byte 9 |
| CS-13 | Strings shorter than 8 bytes | Prefix before extension, both before an unrelated bucket |
| CS-14 | Shared 8-byte head, differ at bytes 9 and 10 | By first differing byte |
| CS-15 | 8 bytes vs its 9-byte extension | Shorter first |
| CS-16 | Head alone, head + 0x00, head + `A` | In that order |
| CS-17 | `ab`, `ab\0`, `ab\0\0` | By length |
| CS-18 | Shared 20 bytes, past the depth cap | By first differing byte |
| CS-19 | Shared 12 bytes | By byte 13 |
| CS-20 | Two empty strings among enough long ones to pass the introsort cutoff | Empties first, by number |

### Unit 6: K-way merge and single-run placement (KM)

`KWayMerge` merges sorted runs through a loser tree; memory is proportional to the run count. `RunPlacement.Place` moves a single run to the output, falling back to a copy staged as `*.partial` and renamed into place; `PlaceEmpty` writes empty output the same way.

| ID | Condition | Expected |
|---|---|---|
| KM-01 | One run | Identical output |
| KM-02 | Two interleaving runs | Merged, each line once |
| KM-03 | Ten or more runs | Correct total order |
| KM-04 | One long run, several one-line runs | Correct; long run drains |
| KM-05 | Empty runs at first and last position | As if absent |
| KM-06 | All runs empty | Empty output |
| KM-07 | Zero runs | Empty output |
| KM-08 | Same line in three runs | Three adjacent copies |
| KM-09 | Tied heads differing in raw bytes; fully identical heads | Raw bytes decide; exact count |
| KM-10 | Set where a double advance would drop a line | Count equals input total |
| KM-11 | Large lazily consumed run set | Incremental output |
| KM-12 | One run, default move | Exact bytes; no copy |
| KM-13 | `tryMove` reports cross-volume failure | Copied; no error surfaced |
| KM-14 | Placed by move and by copy | No temp file remains |
| KM-15 | Lines longer than the staging buffer, mid-run and last | Written directly, intact |
| KM-16 | Middle run's cursor construction fails | Exception propagates; all three streams disposed |
| KM-17 | Run file undeletable after the copy fallback | `Place` doesn't throw; output correct |
| KM-18 | Fixed-width lines, window holding an exact number | Reads = windows + 1 |
| KM-19 | Successful copy fallback | No `*.partial` remains |
| KM-20 | Copy fallback over an existing output | Output replaced |
| KM-21 | Output locked against the final move | `DestinationReplaceFailedException`; old bytes kept; staging removed |
| KM-22 | Read-only run after copy fallback (Windows) | No throw; stderr names it; removed at `Dispose` |
| KM-23 | `PlaceEmpty` with no output and with a non-empty one | Both empty; no `*.partial` |
| KM-24 | `PlaceEmpty` on a locked output (Windows) | `DestinationReplaceFailedException` (exit 3); bytes kept |
| KM-25 | Copy fails before the rename (missing source) | Plain `IOException` (exit 5); bytes kept |
| KM-26 | Existing output, default move (Windows) | Replaced by rename (creation time kept) |
| KM-27 | Three buffer sets for two runs; one set for two runs | Merges; `ArgumentOutOfRangeException` with streams disposed |

### Unit 6a: Range partition and merge slices (RP, RC, OS, SF)

`RangePartitioner` splits one merge into key ranges as byte offsets per run. Every line in an earlier slice is strictly below every line in a later one, so byte-identical lines stay together. `RunSliceStream` reads one slice; `OutputSliceStream` refuses writes outside its range.

| ID | Condition | Expected |
|---|---|---|
| RP-01 | Five runs, 400 keys, four workers | Offsets are line starts, monotone, cover each run; output offsets are running sums |
| RP-02 | Six runs, 90 keys, five workers | Every earlier slice strictly below every later one |
| RP-03 | Three runs of one repeated line, four workers | Three empty slices; `Imbalance` infinite |
| RP-04 | Three one-line runs, eight workers | Complete, ordered, at least five empty slices |
| RP-05 | Two keys, five workers (duplicate splitters) | Empty slices, no overlap |
| RP-06 | Malformed run line | `MalformedLineException`, not an aggregate |
| RP-07 | Empty runs mixed in; all runs empty | Correct partition; no partition |
| RP-08 | Random sweep of runs, lines, keys, workers | RP-01 and RP-02 hold |
| RP-09 | Sample capped by `SampleLineByteBudget`, located three times | Same well-formed partition each time |
| RC-01 | Window and descriptor capacity end exactly at the slice end | Exactly the slice's lines |
| RC-02 | Same, three windows deep | Exactly 12 lines |
| RC-03 | Slice ends at the bound without filling descriptors | Exactly 4 lines |
| RC-04 | `start == end` | No lines |
| RC-05 | Slice starting mid-file | Its own lines only |
| RC-06 | Slice reaching end of file | Includes the final line |
| RC-07 | Random sweep | Exactly the slice's lines |
| RC-08 | `DisposeAsync` | Inner stream disposed once |
| OS-01 | Slice `[4, 8)` | Write lands at offset 4 |
| OS-02 | Write crossing the slice end | `InvalidOperationException`; nothing written |
| OS-03 | Exact fill | Allowed |
| OS-04 | Write to `[2, 2)`; `end < start` | Throws; constructor throws |
| OS-05 | `DisposeAsync` | Inner stream disposed once |
| SF-01 | `SparseFile.TryMarkSparse` on a new file | Sparse attribute set (Windows; skipped elsewhere) |
| SF-02 | Block written at 60 MiB in a sparse 64 MiB file | Block intact; hole reads zero |
| SF-03 | `TryClearSparse` on a fully written file | True; attribute gone |
| SF-04 | Partitioned vs sequential merge (in `MergeExecutorTests`) | Identical bytes; output not left sparse where clearing works |

`MergeExecutorTests` also has an untraited case: a failing worker surfaces its own exception, with every run handle closed.

### Unit 7: Merge planner and executor (MP)

`MergePlanner.Plan` groups runs into passes. Groups are within the fan-in, balanced, never of size one, and the run count strictly decreases. MP-11 to MP-18 drive `MergeExecutor`.

| ID | Condition | Expected |
|---|---|---|
| MP-01 | Runs < fan-in | One pass, one group |
| MP-02 | Runs = fan-in | One pass |
| MP-03 | Runs = fan-in + 1 | Two passes; leftover carried forward |
| MP-04 | Three or more passes needed | Correct count; decreasing |
| MP-05 | One run | Zero passes (placement handles it) |
| MP-06 | Zero runs | Empty plan |
| MP-07 | Fan-in 0 or 1 | Fails at plan time |
| MP-08 | Remainder of one across fan-ins | No group of size one |
| MP-09 | Uneven division | Group sizes differ by at most one |
| MP-10 | Plan vs real merge | Executed passes = predicted |
| MP-11 | Read-only first-pass run, fan-in 3 (Windows) | Merge completes; stderr names the run once; removed at `Dispose` |
| MP-12 | All-identical runs, four workers | Falls back to sequential (`SingleSlice`); bytes identical |
| MP-13 | Hot key with keys on both sides, four workers | Still `Parallel`; bytes identical |
| MP-14 | Output hard-linked to another file | Linked file untouched |
| MP-15 | Malformed line in a run, sequential | Exception names that run, its line 2 and offset |
| MP-16 | Cancel in a non-final pass | `OperationCanceledException`; existing output kept; no run left |
| MP-17 | Cancel in the final pass | Partial output removed; no run left |
| MP-18 | Cancel a partitioned merge | `OperationCanceledException`, not aggregate; all run files deletable |

### Unit 8: Buffer pool (BP)

`BufferPool` hands out a fixed set of buffers. Acquisition waits when the pool is exhausted, and foreign or double releases fail.

| ID | Condition | Expected |
|---|---|---|
| BP-01 | Acquire, release | Buffer of configured size; availability restored |
| BP-02 | Exhausted pool | Acquire waits; nothing allocated |
| BP-03 | Pending acquire, then release | Completes with that buffer |
| BP-04 | Consumer fails | Buffer returned |
| BP-05 | Repeated cycles | No buffer outstanding twice |
| BP-06 | More consumers than the ceiling | Ceiling never exceeded; all complete |
| BP-07 | Foreign or double release | Throws |
| BP-08 | Configured size | Every buffer at least that size |
| BP-09 | Ceiling of one | Serialised; both complete |
| BP-10 | Reissued buffer with stale bytes, shorter fill | Results depend only on the recorded length |

### Unit 9: Memory budget (MB)

`MemoryBudget.Calculate` turns `--memory`, `--parallelism` and `--max-line` into chunk size, fan-in, read-ahead and merge workers that fit the budget in both phases.

| ID | Condition | Expected |
|---|---|---|
| MB-01 | Normal budget | Chunk > 0, fan-in ≥ 2, both phases within budget |
| MB-02 | Budget below the minimum | Throws naming the minimum |
| MB-03 | Parallelism 1 | Valid plan |
| MB-04 | Parallelism too high for the budget | Fails clearly; chunk never below `--max-line` |
| MB-05 | Any valid budget | Fan-in × read-ahead + output within budget |
| MB-06 | Short-line worst case | Chunk reduced for descriptor overhead |
| MB-07 | Larger budget | Chunk and fan-in not smaller |
| MB-08 | Same inputs repeated | Identical plan; no machine-state dependence |
| MB-09 | Fan-in fed to the planner | Accepted |
| MB-10 | Fan-in at `MaxMergeFanIn` with budget left over | Read-ahead grows; phase two within budget |
| MB-11 | 4 GiB, 1.5 GiB, parallelism 3 and 1 | `MergeParallelism` 8, 3, 3, 1 |
| MB-12 | 2 MiB; parallelism 4 and 48 at their minimum budgets | `MergeParallelism` 1, 1, 2 |
| MB-13 | Sweep 300 MiB to 4.25 GiB | Within budget; workers monotone, reaching 8 |
| MB-14 | Budget around the `Array.MaxLength` chunk boundary | Chunk clamped to `Array.MaxLength` |
| MB-15 | `1L << 60` and `long.MaxValue` | Viable plan, no overflow |

### Unit 10: Capacity precheck (SC)

`TempCapacity` is the pure decision. `VolumeCapacity.Check` (in `Cli/`) probes the volumes, uses `EvaluateSameVolume` when temp and output share one, otherwise `Evaluate` plus `EvaluateOutputVolume`, and throws `PreflightException` on a shortfall.

| ID | Condition | Expected |
|---|---|---|
| SC-01 | Ample free space | Sufficient |
| SC-02 | Below twice the input | Insufficient, with required and available figures |
| SC-03 | Exactly the requirement | Per the documented boundary |
| SC-04 | Zero input | Sufficient |
| SC-05 | Free space unknown | Unknown, never treated as zero |
| SC-06 | Input sizes across magnitudes | About twice the input |
| SC-07 | `EvaluateSameVolume` at and one below 2× + margin | Sufficient; Insufficient |
| SC-08 | `EvaluateOutputVolume` at and below 1× + margin; null; near `long.MaxValue` | Boundary as SC-03; Unknown; clamps |

### Unit 11: Generator (GN)

`LineComposer` and `FileWriter` emit whole canonical lines up to the byte target. Repeated string parts come from a pool rather than chance.

| ID | Condition | Expected |
|---|---|---|
| GN-01 | Few-hundred-KB target | At or below target, within one line |
| GN-02 | Target mid-line | Crossing line omitted |
| GN-03 | Duplicate ratio > 0, and = 0 | Deliberate reuse; zero repeats at 0 |
| GN-04 | Several ratios | Measured ratio within tolerance |
| GN-05 | Same seed twice | Byte-identical |
| GN-06 | Different seeds | Different |
| GN-07 | Target below one line | Empty file |
| GN-08 | Target 0 | Empty file |
| GN-09 | Generated file through `LineParser` (in `GeneratorGrammarTests`) | Every line parses, within the limit |
| GN-10 | Large file | Wide spread of valid numbers |
| GN-11 | Generated file | Varied lengths, multi-word and multi-byte content |
| GN-12 | Generated bytes | No `\r`; every line terminated |
| GN-13 | *Not implemented.* Same output across parallelism | The generator is single-threaded, so there is nothing to vary. Write this first if generation is parallelised. |
| GN-14 | Exact size of the first two composed lines | Two lines sharing a string |
| GN-15 | 512, 733, 1,024, 2,001 bytes × seeds | At least one repeat |
| GN-16 | One byte below `MinimalForcedPairByteLength` | No throw; within target; deterministic |
| GN-17 | 100 bytes, seed 42 | At least one repeat |
| GN-18 | Sizes 40–200 × seeds 1–20 | Every file of 2+ lines has a repeat |

### Generator output placement (GW)

`tests/TestFileGenerator.Tests/OutputPlacementTests.cs` drives `StagedOutput.Write`. It writes to `*.partial` and moves over the output only on success.

| ID | Condition | Expected |
|---|---|---|
| GW-01 | Existing stale output | Replaced; no `*.partial` |
| GW-02 | Output locked against the move | Throws; old bytes kept; no `*.partial` |
| GW-03 | Cancelled from the first progress callback | `OperationCanceledException` (exit 130); old bytes kept; no `*.partial` |

---

### Property-based tests (PB)

`tests/FileSorter.Tests/Properties/`, using CsCheck. `LineEntryGen` produces up to 24 printable-ASCII entries and gives every fifth one a twin (zero-padded or `+`) so the third comparison level is reached. PB-24 covers bytes at or above 0x80.

| ID | Condition | Expected |
|---|---|---|
| PB-01 | Random file, random final termination, one-chunk budget, `SortCommand.RunAsync` | Identical to `NaiveReferenceSort` |
| PB-02 | Same, at a 304-byte chunk and at one chunk | Both identical to the oracle |
| PB-03 | Generated descriptors | `LineOrder` antisymmetric |
| PB-04 | Generated descriptors | Transitive |
| PB-05 | Generated descriptors | Total; reflexive |
| PB-06 | `ChunkReader` over a tiny pool | Lines concatenated with `\n` equal the input |
| PB-07 | *Not implemented.* Generator round trip | GN-05, GN-06 and GN-09 already cover it |
| PB-08 | Arbitrary byte strings incl. 0x00, one a mutation of the other | `Compare` sign matches a reference without the prefix fast path |
| PB-09 | Up to 400 arbitrary-byte entries | `ChunkSorter` equals `Array.Sort` with `LineOrder.Compare` |
| PB-10 | `ChunkReader`: random lengths up to the limit, mixed terminators, BOM, trailing `\r`, short reads, some over-length lines | Lines and counters match `NaiveLineFormat.Split`; error offset and line match |
| PB-11 | `RunCursor`, same shape, no BOM | Same |
| PB-12 | Random run sets over a tiny vocabulary, 1–8 workers | Partitioned = sequential = oracle; partitioned draws counted |
| PB-13 | PB-01's files at a three-worker plan (`--max-line` 64, parallelism 66, 4,362,432 bytes) | Identical to the oracle; three workers |
| PB-14 | Up to 900 heavily tied entries, plus 200,000-line stress shapes | `ChunkSorter` equals `Array.Sort` |
| PB-15 | `2. Banana\n1. Apple` | `1. Apple\n2. Banana\n` |
| PB-16 | `2. Banana\n1. Apple\r` | `1. Apple\n2. Banana\n` |
| PB-17 | `1. Apple\n2. Banana\n\r` | Two lines |
| PB-18 | BOM only | Empty |
| PB-19 | `\r` only | Empty |
| PB-20 | 20,000 random `(parallelism, --max-line, budget)` draws | Throws for `budgetBytes`, or a plan within budget meeting every minimum |
| PB-21 | Same draws | Viable exactly at and above `MinimumViableBudget` |
| PB-22 | `--max-line` at the CLI ceiling, parallelism 1–64 | Viable at the minimum; one byte less throws |
| PB-23 | Two budgets, one larger | Chunk and fan-in not smaller |
| PB-24 | 12–16k lines with UTF-8, high bytes, NUL, signed and padded numbers; multi-pass | Identical to an inline unsigned-byte oracle |

### Integration tests (IT)

`tests/FileSorter.Tests/Integration/`: real files through `SortCommand.RunAsync`, checked against `NaiveReferenceSort`.

| ID | Condition | Expected |
|---|---|---|
| IT-01 | One terminated line | Matches oracle |
| IT-02 | No final terminator | Last line present |
| IT-03 | `\n` only | Matches oracle |
| IT-04 | `\r\n` only | Matches oracle |
| IT-05 | Mixed | Matches oracle |
| IT-06 | Leading BOM | Matches oracle |
| IT-07 | `TempCapacity.Evaluate` with real free space, tiny input | Sufficient |
| IT-08 | `--temp` and output on different real volumes | Exit 0; correct; no run left |
| IT-09 | `VolumeCapacity.Check` with a UNC path (Windows) | No exception; stderr warns free space is unknown |

### Streaming-layer tests (SL)

`RunGeneration/BackpressureAndCancellationTests.cs`, run against both pipelines. `RunGenerationStrategyTests` covers bounded in-flight chunks, buffer release on failure and cancellation, and exception identity. In SL-03 to SL-05 a spill is held on a gate, and the test checks that the strategy has not completed until the spill is released.

| ID | Condition | Expected |
|---|---|---|
| SL-01 | One spill held, parallelism 1 | Reader stalls; resumes on release |
| SL-02 | Cancel 30 ms into a ~12.5 s run | `OperationCanceledException` within 10 s |
| SL-03 | Spill held while a sibling spill throws | Task incomplete until release; `IOException`; no run created after completion |
| SL-04 | Spill held while the reader hits a malformed line | As SL-03, `MalformedLineException` |
| SL-05 | Spill held while the caller cancels | As SL-03, `OperationCanceledException` |

### Startup ownership tests (TR)

`Infrastructure/TemporaryRunSetTests.cs`. Each invocation gets a private subdirectory under `--temp`.

| ID | Condition | Expected |
|---|---|---|
| TR-01 | One set | Runs in a direct child of `--temp` |
| TR-02 | Two sets, same parent | Different directories |
| TR-03 | Just constructed | No subdirectory yet |
| TR-04 | Pre-existing parent, `Dispose` | Parent kept; private directory removed |
| TR-05 | `Delete` on a held run (Windows) | No throw; removed at `Dispose` |
| TR-06 | Held run released before the next `Delete` (Windows) | Removed by that `Delete` |
| TR-07 | Run still held at `Dispose` (Windows) | One stderr line naming the directory |

### Console run tests (CR)

`Cli/ConsoleRunTests.cs` drives `ConsoleRun.Run`, the exit-code mapping that `SortCommand` and `VerifyCommand` share. Signal handling (`src/Shared/ConsoleCancellation.cs`) can't be exercised in-process.

| ID | Condition | Expected |
|---|---|---|
| CR-01 | Body returns 4 | Exit 4; token live |
| CR-02 | `IOException` | Exit 5; `I/O error: <message>` |
| CR-03 | `UnauthorizedAccessException` | Exit 5; same format |
| CR-04 | `OperationCanceledException` | Exit 130; silent |
| CR-05 | `MalformedLineException` | Exit 1; message unchanged |
| CR-06 | `InvalidOperationException` | Propagates |
| CR-07 | `PreflightException` with code 2 | Exit 2; message unchanged |

### Progress-wrapper tests (PR)

`Infrastructure/ProgressReporterTests.cs` drives `ProgressReporter.RunWithProgressAsync`, which `RunGenerationDriver` and `SortCommand` use.

| ID | Condition | Expected |
|---|---|---|
| PR-01 | Work returns 42 | 42; reporter cancelled and awaited |
| PR-02 | Work throws | Same exception; reporter cancelled and awaited |
| PR-03 | Result-less overload | Reporter cancelled and awaited |
| PR-04 | `TickAsync` cancelled before first tick | Completes quietly; no line |

### Command-line tests (CL)

`tests/FileSorter.Tests/CommandLineTests.cs` calls `CommandLine.TryParseOptions` for the input/output identity guard.

| ID | Condition | Expected |
|---|---|---|
| CL-01 | `data.txt data.txt` | Rejected |
| CL-02 | Relative vs absolute, both orders | Rejected |
| CL-03 | `a/../data.txt` | Rejected |
| CL-04 | Case-only difference (Windows) | Rejected |
| CL-05 | Genuinely different files | Accepted |

### End-to-end tests (ET)

`EndToEnd/` (`SortRoundTripTests`, `SortTempCleanupTests`, `SortFailureTests`, sharing `SortHarness`) drives `SortCommand.RunAsync`, or `ConsoleRun` where an exit code is asserted.

| ID | Condition | Expected |
|---|---|---|
| ET-01 | `\r\n` lines at exactly `--max-line`, two budgets | Identical `\n`-only sorted output |
| ET-02 | Final unterminated line ending `\r` | `1. Apple\n2. Banana\n`; `--verify` passes |
| ET-03 | Three-worker vs one-worker plan | Identical; equals oracle; no run left |
| ET-04 | Content ending `\r` before `\r\n`, at single-run, multi-run, three-worker and multi-pass shapes | All equal the oracle and verify; plan and "phase one produced N run(s)" line asserted |
| ET-05 | Sorting `1. a\r\r\n`, then sorting the output again | `1. a\r\n`, then `1. a\n` |
| ET-06 | Unrelated `run-00000001.tmp` beside the output | Untouched |
| ET-07 | Output named `run-00000001.tmp` | Correct output |
| ET-08 | Two concurrent sorts, same `--temp` | Both correct; nothing left |
| ET-09 | Minimum budget, several runs | Multi-pass; private directory removed |
| ET-10 | Malformed last line after several runs have spilled | Exit 1; private directory removed; no output |
| ET-11 | Empty input, read-only output | Exit 3 naming the output; bytes kept |
| ET-12 | One run, read-only output | Exit 3 naming the output; bytes kept |
| ET-13 | One run, stale unlocked output | Replaced |
| ET-14 | Empty input, directory at the output path | Exit 3; directory untouched |
| ET-15 | One run, directory at the output path | Exit 3; directory untouched |
| ET-16 | Multi-run merge, directory at the output path | Exit 5; `I/O error:` line, no stack trace |
| ET-17 | `--memory` 1 MiB at parallelism 16 | Exit 3 naming the minimum; nothing created |
| ET-18 | `2. Banana\n1. Apple\n\n` | Malformed at line 3; no output |
| ET-19 | Unsupportable `--max-line`/`--parallelism` | Exit 3 saying no budget can support it |
| ET-20 | Cancelled after phase one | `OperationCanceledException`; output kept; no run left |

### Verify tests (VF)

`EndToEnd/VerifyTests.cs` drives `VerifyCommand.RunAsync`. Verify checks adjacent order with the sorter's own comparator, and compares line count and an order-independent hash (sum of per-line FNV-1a) between input and output. A mismatch exits 4.

| ID | Condition | Expected |
|---|---|---|
| VF-01 | Correct output | Exit 0 |
| VF-02 | Swapped adjacent lines | Exit 4 naming the line |
| VF-03 | Dropped line | Exit 4; counts differ |
| VF-04 | Duplicated line replacing another | Exit 4; hashes differ |
| VF-05 | `\r\n` input, `\n` output | Exit 0 |
| VF-06 | Malformed output line | Exit-1 diagnostic naming the output |
| VF-07 | Input ending in an unterminated `\r` | Exit 0 |
| VF-08 | Malformed input line | Diagnostic names the input |
| VF-09 | Content `\r` kept before `\n` | Exit 0 |
| VF-10 | Output ends `\r`, no `\n` | Exit 4, `OutputNotTerminated` |
| VF-11 | Empty input, output `\r` | Exit 4, `OutputNotTerminated` |
| VF-12 | Output missing its final `\n` | Exit 4, `OutputNotTerminated` |
| VF-13 | Unreadable output (Windows) | Exit 3 |
| VF-14 | Token cancelled before the call | `OperationCanceledException`; files released |

### Benchmarks

`benchmarks/FileSorter.Benchmarks/` is BenchmarkDotNet and is never run by `dotnet test`.

- `RunGenerationBenchmarks` (real spills), `SchedulerOverheadBenchmarks` (no-op spill) and `SpillWorkBenchmarks` (sort-only or write-only) separate scheduling from I/O. Phase one is measured alone because phase two is the same for both pipelines.
- Channels is the baseline and Akka is reported against it, with `[MemoryDiagnoser]`.
- `ActorSystem` is created in `[GlobalSetup]` so its ~100 ms startup isn't charged per iteration. Stateful objects are rebuilt in `[IterationSetup]`.
- Input comes from `SyntheticInput`, which wraps the generator's `LineComposer` and `FileWriter`.
- Runs use 2 launches × 20 iterations; three iterations give error bars wider than the effect.
- `ChunkSortBenchmarks` times `ChunkSorter.Sort` on chunks from 16 to 320 MiB. `MergeBenchmarks` times `KWayMerge`, and `PartitionedMergeBenchmarks` times `MergeExecutor` at 1, 2, 4 and 8 workers.
- `--flatness` and `--flatness-parallel-merge` run each input size in a fresh process, because generating input in the same process inflates GC-reported memory. The residual 41% spread is GC bookkeeping, hence the 60% tolerance.
- Results are recorded with machine and disk in [measurements.md](measurements.md).

---

## Part 3: Coverage and risk

### Evidence mapping

| Criterion | Evidence |
|---|---|
| Code quality | Each unit is tested without doubles. The seams (splitter vs file I/O, merge vs files, placement vs volume, capacity decision vs probe, composer vs writer) show the structure. |
| Performance | KM-12 (single-run move), MP-05/08/09 (no wasted passes), MP-10, CM-02, CM-07, LP-22, OC-16, BP-10. Benchmarks and [measurements.md](measurements.md) give throughput. |
| Memory | BP-02/04/05/06/07/09/10, MB-01 to MB-15, PB-20 to PB-23, CB-03, CB-12, KM-11, SC-01 to SC-08, KM-14, `--flatness`. |
| Test coverage | Byte-identity properties PB-01, PB-02, PB-13 and PB-24, mutation-checked. Determinism: OC-13, OC-14, CS-08, MB-08, GN-05, PB-02. Every case is traceable by trait. |
| Concurrency | BP-06; PB-12 and ET-03 (partitioned = sequential); SL-01 to SL-05; `RunGenerationStrategyTests`; MB-08. |
| Edge cases | LP-02 to LP-27; CB-05, CB-07, CB-08, CB-12, CB-14 to CB-19; OC-13, OC-14, OC-17; KM-05 to KM-09, KM-13; MP-02, MP-05, MP-06, MP-08; SC-03, SC-05; GN-02, GN-07, GN-08; ET-11 to ET-19. |

### Decisions

- **Byte-identity oracle over "is sorted".** A partial oracle accepts dropped and duplicated lines.
- **The oracle shares no production code.** Shared code would let the same bug pass on both sides.
- **CsCheck for properties.** It shrinks failures to minimal inputs and reports a reproducible seed.
- **No stability test.** The third comparison level makes stability unobservable.
- **No timing assertions in tests.** They are flaky. Throughput is measured by benchmarks.
- **Flatness is a benchmark mode, not a tagged test.** It stays out of the gate by construction.
- **GN-13 and PB-07 stay listed, unimplemented.** One has nothing to vary; the other duplicates GN-05, GN-06 and GN-09.

### Residual risks

- **Real 100 GiB behaviour.** A 100 GiB run is recorded (313.4 s at 6 GiB, one merge pass, `--verify` clean), but it was checked by `--verify` rather than an independent sort, and its input repeats one 20 GiB block, so it is more duplicate-heavy than generated data. See README "Limits".
- **Absolute performance.** The suite proves the sort is bounded and correct, not that it is fast.
- **Concurrency.** Only observed interleavings are tested. Shared state is limited to the pool, the run registry and one interlocked progress counter.
- **Partition balance.** Byte-identical lines can't be split across slices. When the partition has at most one non-empty slice, the merge falls back to sequential (MP-12), but a lopsided partition still runs in parallel.
- **Disk exhaustion after the precheck.** If another process fills the disk mid-run, only manual checks cover it.
- **Fail-fast cost.** A late malformed byte discards the whole run.
- **Configuration space.** Budget, parallelism and line-length combinations aren't enumerated. Hostile settings fail loudly (MB-02, MB-04, ET-17, ET-19) rather than degrading silently.
