# Design Specification

Companion to [test-strategy.md](test-strategy.md), whose behavioural contract is authoritative and is not restated here. Figures come from [measurements.md](measurements.md).

Scope: two .NET 10 console programs. A generator writes `<Number>. <String>` lines to a byte-size target with controllable duplicate string parts. A sorter orders such a file by string part, then number, at around 100 GB under a fixed memory budget.

---

## 1. The organising constraint

The test strategy needs five seams: the splitter apart from file I/O, the merge over in-memory sequences, placement apart from the volume, the capacity decision apart from the free-space probe, and generator composition apart from writing. As interfaces, each would have one production implementation and one test double, which is the single-implementation-interface pattern the brief names. Each seam is instead one of three things:

| Seam mechanism | Used for | Test substitution |
|---|---|---|
| A pure function over `ReadOnlySpan<byte>` | Parsing, comparison, line boundaries, budget and capacity arithmetic, merge planning | Call it with a byte array |
| A `Stream` parameter | Merge inputs and output, run spilling, generator output, sorter input | Pass a `MemoryStream` |
| A named delegate | Run-generation scheduling, the move-versus-copy decision in placement | Pass a lambda |

**The sorter defines no interfaces.** It defines two delegates (`RunGenerationStrategy`, `ChunkSpill`) and implements `IComparer<T>` once, privately, in `ChunkSorter`. The one abstraction it earns is `RunGenerationStrategy` (section 6): two production implementations that both ship, can both be selected at runtime, and are both run by the same suite.

---

## 2. Solution layout

```
src/FileSorter/                  exe  the sorter
src/TestFileGenerator/           exe  the generator
src/Shared/                      lib  ConsoleCancellation, StagingFile, ByteSize, Arguments
tests/FileSorter.Tests/          mirrors the production folders, plus EndToEnd/, Integration/, Properties/, Support/
tests/TestFileGenerator.Tests/
benchmarks/FileSorter.Benchmarks/
```

`Shared` holds only what both programs must do identically: the cancellation policy, the `.partial` staging path beside a destination and its best-effort delete, the `B`/`KiB`/`MiB`/`GiB` size syntax, and taking an option's value. The line grammar is deliberately not shared. GN-09 runs generated output through the sorter's parser, so the two cannot drift.

| `src/FileSorter/` | Contents |
|---|---|
| root | `Program`: dispatch only (`--help`; `--verify` → `VerifyCommand`; otherwise `SortCommand`) |
| `Cli/` | `SortCommand`, `VerifyCommand`, `VerifyOptions`, `CommandLine` (parsing and usage text), `SorterOptions`, `Preflight` (input, output directory, `--temp`), `VolumeCapacity` (free-space probe, same-volume check), `PreflightException`, `ConsoleRun` (cancellation wiring and the exception-to-exit-code ladder for both modes), `ExitCodes` |
| `Planning/` | `MemoryBudget`, `MemoryPlan`, `TempCapacity`, `CapacityDecision` |
| `Infrastructure/` | `TemporaryRunSet`, `ProgressReporter`, `FileStreams` |
| `LineFormat/` | `LineParser`, `LineCursor`, `LineOrder`, `LineDescriptor`, `RunHead`, `LineStager`, `MalformedLineException` |
| `RunGeneration/` | `BufferPool`, `Chunk`, `ChunkReader`, `ChunkSorter`, `ChunkSpiller`, `RunGenerationStrategy` (with `ChunkSpill`), `RunGenerationDriver`, `AkkaRunGeneration`, `ChannelRunGeneration` |
| `Merging/` | `MergePlanner`, `MergePass`, `MergeExecutor`, `KWayMerge`, `RunCursor`, `RunCursorBuffers`, `MergeProgress`, `MergeReporter`, `RunPlacement`, `OutputFile`, `DestinationReplaceFailedException`; partitioned path: `PartitionedMerge`, `RangePartitioner`, `RangePartition`, `SliceStream`, `RunSliceStream`, `OutputSliceStream`, `SparseFile` |
| `Verification/` | `OutputVerifier`, `VerificationResult` |

`src/TestFileGenerator/` contains `Program`, `CommandLine`, and `Generation/` (`GeneratorOptions`, `Vocabulary`, `LineComposer`, `FileWriter`, `StagedOutput`).

Dependencies run one way:

- `LineFormat` depends on nothing, and `Infrastructure` only on `Shared`.
- `Planning` depends on `LineFormat`.
- `RunGeneration` and `Merging` depend on those three but not on each other.
- `Verification` depends on `LineFormat` and `Infrastructure`.
- `Cli` sits on top of everything, and `Program` depends only on `Cli`.

Nearly every type is `internal`; tests reach them through `InternalsVisibleTo`.

---

## 3. Entities

### 3.1 LineDescriptor

A 32-byte struct with no reference fields:

- `Prefix`: the string part's first 8 bytes, big-endian and zero-padded.
- `Number`: parsed once.
- `StringOffset`.
- `Offset` and `Length`, packed into one `long`.

Sorting moves descriptors, never line bytes, and nothing is decoded on the hot path.

- **Packing buys field count, not bytes.** A five-field version was also 32 bytes but ran 40% slower on the in-memory merge, because RyuJIT promotes struct fields only up to four.
- **`StringOffset` is stored** so comparisons never rescan for the separator. 24 bytes is impossible: leading zeros make the distance from line start to string start unbounded.
- `Offset` and `StringOffset` are absolute in the chunk buffer, assigned after the carry-over copy.
- **`TryCreate` is the only place a descriptor is built, and `BuildPrefix` the only place a prefix is laid out.** Every reader and the test fixtures use them, so a prefix cannot disagree with its bytes.
- `MemoryPlan` reads the size with `Unsafe.SizeOf`, so the budget stays correct if a field is added.

### 3.2 Chunk and MergePass

`Chunk(PooledBuffer Buffer, int Count)` is one pool slot (bytes and descriptor array) plus a line count, so there is exactly one thing to release. Consumers read `Count`, never `Buffer.Lines.Length`.

`MergePass(Groups, CarriedForward)` holds run indices, not paths. The planner never learns that runs are files.

### 3.3 MemoryPlan

A record struct of the solved sizes. Every derived quantity is a computed member, so the arithmetic cannot disagree with what is allocated:

```
PoolCapacity           = Parallelism + 2      one slot parsing, one filling ahead, Parallelism in flight
BytesPerSlot           = ChunkSize + DescriptorCapacity × DescriptorSize
WorstCasePhaseOneBytes = PoolCapacity × BytesPerSlot + ChunkSize (pending buffer, 5.2) + Parallelism × SpillBufferSize

BytesPerRunCursor      = 2 × ReadAheadBufferSize + ReadAheadDescriptorCapacity × DescriptorSize
WorstCasePhaseTwoBytes = MergeParallelism × MergeFanIn × BytesPerRunCursor
                       + MergeParallelism × OutputBufferSize
                       + MergeMetadataBytes   loser trees, plus partition offsets when MergeParallelism > 1
```

`MergeFanIn` caps one worker's run count, not a total. Each partitioned worker opens every run and stages its own output, so `MergeParallelism` multiplies both phase-two terms. At `MergeParallelism` 1, every term reduces to the cost of the sequential merge.

### 3.4 Errors

The sorter has three exception types, and `ConsoleRun` maps each to an exit code:

| Exception | Exit code | Meaning |
|---|---|---|
| `MalformedLineException` | 1 | Carries the byte offset, line number and a truncated preview |
| `PreflightException` | its own code | The command cannot start: 3 for bad arguments, unreadable files or a budget that is too small; 2 for insufficient space |
| `DestinationReplaceFailedException` | 3 | Placement's final rename onto the output failed |

Everything else surfaces as the framework exception it already is, and I/O failures exit 5.

---

## 4. LineFormat

### 4.1 LineParser

`LineParser` is the only code that knows the grammar. `TryParse(line, out number, out stringStart)` returns `false` instead of throwing, and the caller, which knows the offset and line number, owns the diagnostic. The rules:

- The boundary is the first `.` in the line.
- The number is an optional sign and ASCII digits within the `long` range, so it cannot contain a period.
- The field's last byte must be a digit, because `long.TryParse` ignores trailing NULs.
- One space after the period is consumed if present.

The parser does no decoding, allocation, encoding validation or length checking (D2).

### 4.2 LineCursor

`LineCursor` is a `ref struct` over a supplied block. It yields complete lines and reports the trailing partial line (`CarryOffset`, `CarryLength`). It never sees a file, so the most defect-prone logic in the sorter can be tested in memory. It is the only implementation of the terminator rules, used over chunk buffers and over read-ahead windows, so the two phases cannot disagree about a stray `\r`.

- **BOM and `\r` stripping are on by default, for the user's input.** Code that reads files the sorter wrote (`RunCursor`, `OutputVerifier`'s output scan) turns both off, because those files are `\n`-terminated and a `\r` in them is content. `RangePartitioner.ReadLineAt` applies the same rule by hand.
- **D2: the maximum line length is enforced here.** The cursor must bound its own carry-over, and enforcing the limit at that point makes the memory guarantee structural (CB-12, CB-16).
- Callers await the fill before running the cursor, so no instance is live across an `await`.

### 4.3 LineOrder

`Compare(in a, bufferA, in b, bufferB)` takes two buffers because the merge compares lines from different runs. With one comparator, the two phases cannot diverge. It compares in four steps:

1. **The cached `Prefix` values**, if they differ. Zero-padding preserves order, because 0 is the minimum byte and a proper prefix sorts before its extension. This holds for any bytes, including `0x00` and invalid UTF-8.
2. **The string part**, ordinally over raw bytes.
3. **The number.**
4. **The raw line bytes.**

It allocates nothing, never decodes and never consults culture.

**The grammar requires the raw-bytes step.** `007. Apple` and `7. Apple` tie, as do `+5. Apple` and `5. Apple`. Without step 4, their order would depend on chunk boundaries, and so on the memory budget (OC-13, OC-17). With it, two lines that compare equal are byte-identical, so the output is unique.

**`IComparer<T>` is implemented once**, by `ChunkSorter`'s private `DescriptorComparer`, constructed inside the call that owns its buffer, so a descriptor can never be compared against the wrong buffer. `KWayMerge`'s loser tree uses a private static comparison.

**The reference oracle must not call any of this.** `NaiveReferenceSort` (tests `Support/`) decodes each line and compares with `SequenceCompareTo`, then numbers, then raw lines. A comparator defect therefore cannot appear on both sides of the headline property test. The oracle splits lines with `NaiveLineFormat`, which is also independent of `FileSorter.LineFormat`. `InternalsVisibleTo` exposes `LineOrder` to tests, so this rule is a convention.

---

## 5. RunGeneration

### 5.1 BufferPool

**A slot owns both the byte buffer and the descriptor array, allocated once (D9).** `AcquireAsync` waits when the pool is empty and never allocates. **This is the program's flow control, under both strategies** (section 6).

Slot indices circulate through a bounded `Channel<int>`. A `bool[]` of outstanding slots makes a double release, or the release of a foreign buffer, fail loudly. Arrays are not cleared on release, because consumers respect recorded lengths.

### 5.2 ChunkReader

`ChunkReader` is sequential: one per run, never called concurrently.

- **The next chunk's fill overlaps the current chunk's parse.** Each call:
  1. Awaits the fill for the slot it delivers.
  2. Acquires the next slot and starts its fill.
  3. Only then parses.

  Starting the fill after the parse would pass every byte-identity test and overlap nothing, a mistake only measurement catches. Fresh bytes always land at `Reserve = maxLineLength + LineCursor.WindowSlack (2)`, so the fill can start before the carry is known. The 2 bytes cover a maximum-length `\r\n` line whose fill ends on the CR.
- **When the carry fits in `Reserve`** (the common case), it is copied into `[Reserve − carry, Reserve)` and the next fill keeps running.
- **When the carry exceeds `Reserve`**, the descriptor array filled first (D9), and the next slot is rebuilt:
  1. Its fill is awaited; this is the only place the overlap is lost.
  2. The carry goes to the head of the slot.
  3. Bytes that no longer fit wait in the pending buffer, because a forward-only stream cannot re-read them.

  How often this happens depends on `AssumedMeanLineLength` (8.1).
- **End of stream is decided per fill**, from that fill's own count. With two fills in flight, a shared flag would let a later chunk's look-ahead turn this chunk's carry into a false final line and silently lose bytes (`ByteIdentityPropertyTests` and `ChunkReader` tests).
- **A bare trailing `\r` is stripped from the last fragment**, as the first half of a terminator that never arrived. Kept, it would make `--verify` fail a correct sort.
- The prefetched slot's state is one `Prefetch` record, set and cleared as a unit.
- **Release (D11).** Each call holds at most two slots and disposes whichever it still owns on any throw. `DisposeAsync` observes any fill in flight first; a stream is never disposed under an overlapped read.

### 5.3 ChunkSorter

An in-place MSD radix sort sits in front of the BCL introsort:

- **Depths 0–7** bucket on byte *d* of `Prefix` (256 buckets), which is exactly the comparator's first step.
- **Depths 8 and beyond** bucket on string byte *d* into 257 buckets, where bucket 0 means the string part has ended.
- **Bucket 0 sorts first and is never recursed into**, because a proper prefix precedes its extension, even one continuing with `0x00`. At depth 8 it can hold `"ab"` and `"ab\0"`, so the full comparator finishes it.
- **A level where everything lands in one bucket** is retried one byte deeper in the same stack frame.
- **The descent stops at depth 16 or at 32 descriptors**, and `Span.Sort` with `DescriptorComparer` finishes. Both limits are performance bounds only.

The sort works in place because the budget has no term for a second descriptor array. It need not be stable, because the introsort finishes with the full comparator. The base case uses the BCL sort although an insertion sort measured about 5% faster, because the BCL sort is correct and tuned. Counting arrays are `stackalloc`ed (about 2 KiB per frame), bounded by the depth cap.

### 5.4 ChunkSpiller

`SpillAsync` matches `ChunkSpill`. It sorts the chunk, writes the run, returns its path, and releases the slot in a `finally` (D11). The run is opened unbuffered (D12), and a `SpillBufferSize` staging array batches the writes. The run's size is known in advance, so `SetLength` is called first and `Position` checked at the end, because a run of the wrong length would read back as a truncated line rather than fail.

---

## 6. The run-generation seam

```csharp
internal delegate Task<IReadOnlyList<string>> RunGenerationStrategy(
    ChunkReader reader, ChunkSpill spill, int parallelism, CancellationToken ct);
```

**Contract.**

- A strategy schedules work and owns none of its caller's resources.
- It surfaces the first failure as the original, unwrapped exception (D13).
- It completes only after every `ReadNextAsync` and spill it started has finished, on every path (D16).
- Run order is undefined (D8).

**The two strategies:**

- **`ChannelRunGeneration`** (default): a `Channel<Chunk>` bounded at `parallelism`, one producer draining the reader, and `Parallel.ForEachAsync` consuming it. Its `finally` completes the channel writer, so the consumer cannot wait forever.
- **`AkkaRunGeneration`** (`--pipeline akka`): `Source.UnfoldAsync` → `SelectAsyncUnordered(parallelism, chunk => Task.Run(() => spill(...)))` → `Sink.Seq`.
  - **The `Task.Run` is required.** The mapper runs on the stage's actor thread and `SpillAsync` is synchronous through the sort, so without it every chunk is sorted on one thread (3.8× Channels' time at parallelism 4, against 1.1× with it).
  - `Task.Run` gets `CancellationToken.None`, so a cancelled token cannot skip the `finally` that releases the slot.
  - `SortCommand` creates the `ActorSystem` (`CreateQuietSystem`, logging off) only in the Akka branch.

**The pool bounds in-flight work, under both strategies.** `PoolCapacity` blocks the reader before the channel bound or Akka's buffer is reached, so peak memory is a property of the plan, not the scheduler. The streaming tier asserts `BufferPool.Outstanding ≤ PoolCapacity`. A test against the channel bound would also pass with no flow control at all.

**D13: the same exception from both strategies.** If one strategy wrapped `MalformedLineException`, the same corrupt file would exit 1 under one strategy and crash under the other. `await` already unwraps `Parallel.ForEachAsync`. Akka's materialized task keeps an `AggregateException`, which `AkkaRunGeneration` unwraps with `ExceptionDispatchInfo`.

**D16: a strategy is finished when it returns.** The caller then disposes the reader and pool, and `TemporaryRunSet` deletes the runs. A spill still running would write into a dismantled pool and leave an undeleted run, on the failure path.

- `Parallel.ForEachAsync` already waits for every worker.
- Akka's task completes at the first fault while sibling spills keep running. `AkkaRunGeneration` therefore links a `CancellationTokenSource` to the caller's token. It tracks the single outstanding read and counts spills, incrementing the count before each spill task is created. Its `finally` cancels and waits for both.
- The reader's look-ahead fill is observed by `ChunkReader.DisposeAsync`, and `input` is declared before `reader`, so the stream closes after that fill.
- SL-03 to SL-05 hold one spill open on a gate and assert that the strategy has not completed.

**Why two strategies:**

1. One streaming suite runs against both, so a behaviour holding for only one scheduler is a defect (this caught D13).
2. The byte-identity property test runs under both.
3. The benchmark gets a dependency-free baseline, which makes the Akka.Streams choice a measured one.

Two implementations show that a behaviour is not specific to one scheduler. They do not show correctness under interleavings neither produced.

**Sort-mode flow.** `SortCommand.RunAsync` validates in order: the input, `MemoryBudget.TryCalculate`, the output directory, `--temp`, and `VolumeCapacity.Check`. It then picks the strategy. `RunPhasesAsync` generates runs through `RunGenerationDriver` and acts on the run count:

- 0 runs: `RunPlacement.PlaceEmpty`.
- 1 run: `RunPlacement.Place`.
- More: `GC.Collect()`, then `MergeExecutor` under `ProgressReporter`.

---

## 7. Merging

### 7.1 MergePlanner

`Plan(runCount, fanIn)` is pure arithmetic and guarantees:

- Every run is in exactly one group per pass.
- No group exceeds the fan-in or has size one.
- Every pass reduces the run count.
- The final pass produces one output.

### 7.2 RunCursor

A `RunCursor` holds one run's current line, never a whole run. Its two read-ahead windows and descriptor array are supplied by the caller (`RunCursorBuffers`). Per-fill allocation would bring back the LOH churn D9 removed and fall outside `WorstCasePhaseTwoBytes`. D9's either-resource rule applies here too.

- **Two windows.** As soon as a window is scanned, its carry is copied into the other window and a read starts there, unawaited, before any descriptor is handed out. `Buffer` always names the window being delivered, and the other window takes over only once that one is drained. Only a run's first window, and the resume after end of stream when the previous window ended on descriptor capacity, refill synchronously.
- **`TryMoveNext` / `MoveNextAsync`.** A window holds thousands of descriptors (about 2,700 per worker at the shipped 4 GiB, eight-worker plan). The synchronous `TryMoveNext` serves the common case without an async state machine and never touches a fill.
- **A malformed line carries a run index, not a path.** `MergeExecutor` and `PartitionedMerge` rebuild the exception with the run's path. The partitioned merge also adds the slice start, so the offset is absolute.
- **Slice bounds live in `RunSliceStream`**, where a slice end looks like end of stream. A bound inside the cursor would add a fourth end-of-input condition to the prefetch, the carry and the exhausted flag.

### 7.3 KWayMerge

`MergeAsync(runs, cursorBuffers, output, outputStagingBuffer, maxLineLength, progress = null, ct = default)` takes streams in and writes a stream out, so tests merge `MemoryStream`s. `ct` comes last, so a positional token cannot bind to `progress`.

Output is batched by a `LineStager` into the staging buffer. A line longer than the buffer falls back to a direct two-part async write. Every output write goes through `TimedWriteAsync`, so bytes written and output-wait time are one measurement.

**Selection uses a loser tree private to `KWayMerge` (D3).** It holds a `RunHead[]` (a null buffer marks an exhausted run) and an `int[]` tree using Knuth's construction, both sized to the fan-in. Exact ties go to the lower run index. `RunHead` is `internal` only so `MemoryPlan` can read its size and include the tree in `WorstCasePhaseTwoBytes`.

```
while (tree.WinnerIsAlive)
{
    int run = tree.Winner;
    Write(cursors[run].Current, cursors[run].Buffer);
    if (cursors[run].TryMoveNext() || await cursors[run].MoveNextAsync(ct))   // only now may Buffer change
        tree.SetHead(run, new RunHead(cursors[run]));
    else
        tree.MarkDead(run);
    tree.Replay(run);
}
```

**The invariant.** A head is refreshed immediately after each advance, before `Replay` and before the next comparison. The loop is single-threaded, so no comparison reads a window mid-refill. The line is written before the cursor advances, because advancing can swap `Buffer`. KM-11 and `KWayMergePropertyTests` use tiny windows to force enough refills to catch a violation.

A partitioned merge runs `MergeParallelism` of these single-threaded calls at once, each with its own streams and buffers. They share only `MergeProgress`, whose counters use `Interlocked`.

### 7.4 RunPlacement

With one run, phase two moves it to the output instead of rewriting it, saving a full read and write.

- `Place` moves the run to a `StagingFile` path beside the output, or copies it if the move fails. The staging file is then renamed over the output, so an existing output is replaced atomically and survives any failure. `PlaceEmpty` does the same with an empty file.
- Tests force the copy fallback by passing a lambda as the `tryMove` delegate.
- Only the final rename's failure becomes `DestinationReplaceFailedException` (exit 3). A failed copy stays an I/O failure (exit 5).

### 7.5 MergeExecutor

`MergeExecutor` executes `MergePlanner`'s passes and owns the intermediate runs (D10).

- **Allocation.** `ExecuteAsync` allocates cursor buffers for `min(runCount, MergeFanIn)` runs once and reuses them for every group. The fixed-size output staging buffer is allocated in the constructor. With every stream unbuffered (D12), these arrays are exactly what `WorstCasePhaseTwoBytes` bounds.
- **For each group:**
  1. Open the inputs and the output unbuffered.
  2. `SetLength` the output to the inputs' exact total.
  3. Merge, then check `Position`.
  4. Delete the inputs as soon as the group finishes.

  Deleting per group keeps peak temporary usage near the input size, so the 2× capacity estimate is a true upper bound.
- **The final pass writes straight to the output.** `OutputFile.CreateFresh` deletes an existing output rather than truncating it, because truncating through a hard link or symlink could destroy the input. A failure after the output is opened deletes the partial file. A failure before that leaves an existing output untouched.
- **`TotalBytesToWrite` is exact**, computed once from the plan over run sizes, because runs are `\n`-normalised.
- **Phase-one memory is released first.** `RunGenerationDriver` keeps its pool and reader as locals of a method that returns only paths, and `SortCommand` runs `GC.Collect()`. Pooled arrays live on the LOH, and the budget is a working-set claim.
- `PassesExecuted` lets MP-10 compare predicted and actual passes on a real temporary directory, with no interface.
- **The partitioned branch.** `PartitionedMerge.TryMergeAsync` takes the merge when `MergeParallelism > 1` and the plan is a single group of every run. Multi-pass merges stay sequential. After sampling, a partition with at most one non-empty slice (all keys equal) falls back to the sequential merge. Empty slices alone do not (MP-13).

### 7.6 OutputVerifier

`sorter --verify` checks a result too large to compare against a second sort, using the sort's own comparator.

- **Memory.** Two fixed buffers of `BaseBufferSize` (4 MiB) plus `maxLineLength`, with no memory plan. The input and output are read once, concurrently.
- **Order.** Each adjacent pair of output lines must be non-decreasing. The previous line's descriptor is copied out, so the comparison survives the next fill.
- **Content.** Both scans count lines and bytes and sum per-line FNV-1a 64 hashes. The input scan strips `\r` before `\n`, as `LineCursor` does. The output scan does not, because any `\r` left in the output is content. Both hashes therefore cover the same line contents.
- A malformed line is reported with the path of the file it came from.
- **`OrderViolation` stops at the first violation**, so `VerifyCommand` prints the violating line number instead of a partial count.
- **`OutputNotTerminated`.** The output scan does not finalise an unterminated last line. Otherwise a truncated `"1. a\r"` would hash the same as the input `"1. a\n"`.

Every outcome other than `Verified` exits 4.

### 7.7 RangePartitioner and the partitioned merge

**Why.** The merge spends about 167 ns per output line on per-core miss latency, not bandwidth (about 1.7 GB/s of traffic against roughly 50 GB/s available), so the cost divides across cores. At 20 GiB with a 4 GiB budget, the merge takes 108.2 s with one worker, 51 s with four, 37–39 s with eight, and gets slower at sixteen.

**The partition.** `Locate` runs once, before any worker starts:

1. **Sample.** Read up to `TargetSampleLines` (4,096) lines at evenly spaced byte positions across the runs' concatenated bytes, so the sample depends only on the inputs. Sort them and take the `workerCount − 1` quantiles as splitters. Byte-position sampling weights lines by length, giving the byte split the merge needs, so no refinement round is required. Balance measured within 2–3% against a ±10% target. A count derived from `--max-line` measured 8–13% off (32% off at `--duplicate-ratio 0.4`), so the count is fixed.
2. **Locate.** For each run and splitter, binary-search for the first line start at or above the splitter under the full order. Runs and splitters are sorted, so the search is valid and offsets are monotone by construction. Byte-identical lines all go to the later worker, which is unobservable in the output.

**Sample memory** is outside `MemoryPlan`, capped at `SampleLineByteBudget` (16 MiB) and released before workers start. Draws run in parallel. If any draw is refused, the sample is rebuilt from line lengths in bit-reversed order, so it still depends only on the inputs (RP-09).

**Execution.** The output is preallocated, so no worker extends it. Worker *w* reads its slice of each run through `RunSliceStream`s and writes through an `OutputSliceStream` at `OutputOffsets[w]`. Each worker has its own staging buffer; worker 0 reuses the executor's. Workers share only the `Interlocked` counters in `MergeProgress`.

**Sparse output.**

- **The problem.** On NTFS, a write past the valid data length zero-fills the gap synchronously. After `SetLength`, the highest-offset worker would zero-fill almost the whole file while the others waited.
- **The fix.** `SparseFile.TryMarkSparse` marks the file sparse before `SetLength`, so the gap is a hole. This is Windows-only and opportunistic: if refused, the merge pays for the zero-fill.
- **Afterwards.** Once the length checks pass, `TryClearSparse` clears the flag, a metadata change on a fully written file. Its failure is ignored.
- **The cost** is fragmentation (374 extents instead of 1 at 512 MiB). Without sparse marking, an eight-worker merge at 20 GiB is 59% slower (measurements, section 5).

**Length checks.** A worker that wrote one line too many would overwrite its neighbour and still leave a file of the right size, so there are three checks:

- `OutputSliceStream` throws on any write past its end.
- Each worker's bytes must equal its slice length.
- The total must match.

`RangePartition.From` also validates offsets when the partition is built.

**Failure and ownership.** The first failing worker cancels the others. The reported exception is the first that is not an `OperationCanceledException`, unless the caller cancelled. Runs are opened with `FileShare.Read`, because every worker opens every run. No run is deleted until every worker has finished (8.3).

**Open handle count.** Up to `MergeParallelism × MaxMergeFanIn` handles can be open: 16,384 at the shipped 8 × 2,048, against 2,048 sequentially. That is no problem on Windows, where the 20 GiB run peaked at 1,488. **It was not measured on Linux**, where `RLIMIT_NOFILE` applies and the runtime is expected, but not confirmed, to raise the soft limit. `ulimit -n` should allow about 17,000. Below that, a full-fan-in partitioned merge can fail to open its runs and exit 5.

---

## 8. Planning and infrastructure

### 8.1 MemoryBudget

`Calculate(budgetBytes, parallelism, maxLineLength, assumedMeanLineLength)` solves every allocation from those four numbers and guarantees:

```
WorstCasePhaseOneBytes ≤ budgetBytes
WorstCasePhaseTwoBytes ≤ budgetBytes
ChunkSize              > maxLineLength + 2
MergeFanIn             ≥ 2
```

- **Fan-in and window size are derived.** The fan-in rises to `MaxMergeFanIn` (2,048), and then the window grows, up to 4 MiB. A fixed fan-in of 64 would use 5.6 MiB of a 4 GiB budget and need two passes where the derived plan needs one.
- **`MergeParallelism` is derived, not configured**, so it cannot make a plan infeasible. It is the largest *n* ≤ min(`parallelism`, `MaxMergeParallelism`) whose own solved window clears `maxLineLength + 2`, or 1 if none does:

  ```
  room(n)    = budget − n × OutputBufferSize − (n > 1 ? MaxMergeFanIn × (n + 1) × PartitionOffsetEntrySize : 0)
  window(n)  = floor((room(n) − n × MaxMergeFanIn × LoserTreeBytesPerFanInSlot) × m
                     / (n × MaxMergeFanIn × (2m + DescriptorSize))),        m = assumedMeanLineLength
  MergeFanIn = min(MaxMergeFanIn, floor(room(N) / (N × (bytesPerRunCursor(window(N)) + LoserTreeBytesPerFanInSlot))))
  ```

  The window shrinks as 1/*n*, so workers cost no extra budget by construction. Because the solved window must clear the minimum unaided, extra workers never cost fan-in. `MaxMergeParallelism` is 8 by measurement: at 10, the minimum-size window measured 48.8 s against 38 s at 8.
- **Viability.** `TryCalculate` is the single source of this algebra, and the CLI calls only it. Viability is checked at one worker. `TryFindMinimumViableBudget` bisects over `TryCalculate`, which is valid because viability is monotone in the budget (PB-21, PB-23), so the reported minimum is exact. If 2^50 bytes is not viable, the CLI says no budget can work (MB-11 to MB-13).
- **Two constants are measured.**
  - `AssumedMeanLineLength` is 32, below this data's 37.6-byte mean, so the byte buffer fills first and `ChunkReader` avoids its rebuild path. At 20 GiB, 32 gives no rebuilds, while 40 rebuilds all 178 chunks.
  - `OutputBufferSize` is 1 MiB, which cut total output wait with eight workers by 39.3% at 20 GiB.

  Both raise the minimum viable budget.

### 8.2 TempCapacity and VolumeCapacity

`TempCapacity` is a pure decision over input size and free bytes. `VolumeCapacity.Check` probes the volumes once. It throws `PreflightException` (exit 2) when space is insufficient, and warns and proceeds when free space is unknown.

| Volume | Method | Requirement |
|---|---|---|
| Temp and output on one volume | `EvaluateSameVolume` | 2 × input + 1 MiB |
| Temp, separate | `Evaluate` | 2 × input |
| Output, separate | `EvaluateOutputVolume` | 1 × input + 1 MiB |

Runs hold one copy of the input while a merge pass writes part of another, and per-group deletion keeps real usage well inside 2×. The 1 MiB covers newline normalisation and reading drift. Free space that is not reported counts as `Unknown` (SC-05), never 0, because 0 would block every run on such volumes.

### 8.3 TemporaryRunSet

`TemporaryRunSet` makes "no temporary file survives" a single `using` in `SortCommand`. Both phases use it; neither owns it (D10).

- **Runs live in a private `sorter-<pid>-<8 hex>` directory under `--temp`**, so concurrent sorts, or the output in the same directory, cannot collide with them. The directory is created with the first run, after the capacity check. Run files use `FileMode.CreateNew`.
- **`Delete` is best-effort.** A failed delete is reported and retried later and in `Dispose`.
- **`Dispose` deletes every registered run, then the private directory**, and reports what it could not remove. `--temp` itself is never deleted.
- **Ownership contract: a run is never deleted while a handle on it is open.** Sequential paths open runs with `FileShare.None`. The partitioned merge needs `FileShare.Read`, so it calls `Delete` only after `Task.WhenAll` has seen every worker dispose its streams.

---

## 9. The generator

`LineComposer` composes lines into a caller-supplied span, from a fixed `Vocabulary`. `FileWriter.Write` drives it into a `Stream`, so composition and sizing are tested against a `MemoryStream`. `StagedOutput.Write` writes to a `StagingFile` path and moves it into place only on success.

- **Sizing.** The output is whole, terminated lines up to the first line that does not fit (`TryComposeNext` returns `false`). It never overshoots and never truncates a line, because the sorter would reject a truncated line. It can therefore be up to one line short.
- **No `--max-line` option.** Nothing the generator composes approaches 64 KiB.
- **Reproducibility** holds only on the same .NET version, because `System.Random`'s seeded sequence can change between major versions. An owned random generator would invalidate the recorded baselines.
- **Duplicates are guaranteed by construction.** The ratio is a proportion over the file, and a positive ratio also forces the first two lines to share a string part. Two candidate pairs are always drawn, so later output does not depend on the target, and the first that fits is written. A ratio of 0 disables both.
- **Numbers have no leading zeros**, so generated data never reaches step 4 of the comparison. OC-13 and OC-17 are hand-written for that reason.
- **Progress and cancellation (D15).** The generator exits 0, 3 for bad arguments or a failed write, or 130 when cancelled. The staging file is deleted on any failure.

---

## 10. Command-line contract

The command line is the deliverable's only public API.

```
sorter    <input> <output> [--temp DIR] [--memory 1GiB] [--max-line 64KiB]
                           [--parallelism N] [--pipeline akka|channels]
sorter    --verify <input> <output> [--max-line 64KiB]

generator <output> --size 100GiB [--seed 42] [--duplicate-ratio 0.1]
```

Defaults: `--memory 1GiB`, `--max-line 64KiB`, `--parallelism` = processor count, `--pipeline channels`, `--temp` = the output's directory, `--seed 0`, `--duplicate-ratio 0.1`. Sizes take a byte count or a `B`/`KiB`/`MiB`/`GiB` suffix. `--memory` and `--max-line` let tests force a multi-pass merge or an over-long line on small inputs.

**`--max-line` has a different ceiling in each mode.** Sort mode allows up to `Array.MaxLength - 3`, because a chunk must exceed `maxLine + 2` and fit in one array. Verify mode allows up to `Array.MaxLength - BaseBufferSize`, so a value between the two exits 3 instead of crashing the verifier's allocation.

| Code | Program | Meaning |
|---|---|---|
| 0 | both | Success |
| 1 | sorter | Malformed input, with byte offset, line number, and a truncated preview on stderr |
| 2 | sorter | Insufficient space on the temp or output volume, naming required, available, and the directory examined |
| 3 | both | Invalid arguments; usage printed |
| 4 | sorter | `--verify` found the output out of order, unterminated, or disagreeing with the input's count or hash |
| 5 | sorter | An I/O failure after startup validation passed, such as a full disk; one `I/O error:` line on stderr |
| 130 | both | Cancelled by the operator or by SIGTERM |

**Exit 3 covers what the operator can fix with a different argument:**

- A destination that cannot be replaced.
- A missing or unreadable input, or a missing or unreadable `--verify` output.
- A `--memory` budget that is too small, with the minimum named.

Exit 5 covers failures no argument caused. Every preflight failure is a `PreflightException` carrying its own code, so `ConsoleRun` is the only exception-to-exit-code mapping.

**Cancellation.** `Shared/ConsoleCancellation` gives both programs the same policy:

- The first Ctrl+C or SIGTERM cancels the token.
- A second one ends the process at once.
- SIGHUP is not handled, so a sort under `nohup` survives the session ending.

A cancelled sort leaves no temporary files, which relies on D16. Progress goes to stderr, so stdout stays empty and the tool works in a pipeline.

---

## 11. Traceability

| Test strategy unit | Type |
|---|---|
| 1, line parser | `LineFormat/LineParser` |
| 2, comparator | `LineFormat/LineOrder` |
| 3, descriptor and chunk | `LineFormat/LineDescriptor`, `RunGeneration/Chunk` |
| 4, boundary splitter | `LineFormat/LineCursor` |
| 5, chunk sorter | `RunGeneration/ChunkSorter` |
| 6, k-way merge | `Merging/KWayMerge`, `RunCursor`, `LineFormat/LineStager` |
| 6, single-run shortcut | `Merging/RunPlacement`, `Shared/StagingFile`, branched in `Cli/SortCommand` |
| 6a, range partition | `Merging/RangePartitioner`, `RangePartition`, `RunSliceStream`, `OutputSliceStream`, `SparseFile`, `PartitionedMerge` |
| 7, merge planner; MP-10 | `Merging/MergePlanner`; `MergeExecutor.PassesExecuted` |
| 8, buffer pool | `RunGeneration/BufferPool` |
| 9, memory budget | `Planning/MemoryBudget`, `MemoryPlan` |
| 10, capacity precheck | `Planning/TempCapacity` |
| 11, generator composition | `Generation/LineComposer` |
| Temp cleanup | `Infrastructure/TemporaryRunSet` |
| SL tier | `RunGenerationStrategy`, against both strategies |
| PB, IT, ET tiers | `tests/FileSorter.Tests/Properties/`, `Integration/`, `EndToEnd/`, driving `SortCommand.RunAsync` |
| Byte-identity oracle | `tests/FileSorter.Tests/Support/NaiveReferenceSort` (must not call `LineOrder`) |
| VF | `Verification/OutputVerifier`, `Cli/VerifyCommand` |

---

## 12. Decisions

| # | Decision | Reason |
|---|---|---|
| D1 | Both run-generation strategies ship, chosen with `--pipeline`, default `channels` | Two implementations earn the seam and give the benchmark a baseline. Channels is the default: no actor system, and Akka measured 3.3% slower on phase one (measurements 4.1). Akka remains for its composition and failure semantics |
| D2 | `LineCursor`, not `LineParser`, enforces the maximum line length | The component that bounds the carry-over enforces the bound |
| D3 | A private loser tree, not `PriorityQueue` | At most ⌈log2 k⌉ direct comparisons per line, against a sift-down and sift-up through a boxed comparer |
| D4 | Hand-written argument parsing | One fewer dependency |
| D5 | `LineDescriptor` is 32 bytes and four fields, with `StringOffset` and `Prefix` | No separator rescans, and most comparisons never touch the buffer |
| D6 | `LineFormat` is a feature folder | The grammar is one feature used by every phase |
| D7 | The benchmark reports Akka as a percentage of a Channels baseline | That is how a skeptical reviewer reads it |
| D8 | Run order is undefined | Both schedulers complete out of order |
| D9 | A pool slot owns the bytes and the descriptor array, and a chunk ends when either fills | No descriptor array per chunk on the LOH, so peak memory depends on configuration, not the GC. The assumed mean line length becomes a tuning value |
| D10 | `MergeExecutor` owns intermediate runs; `TemporaryRunSet` is shared by both phases | Temporary files belong to both phases |
| D11 | Whoever holds a slot releases it | Any other rule leaks a slot on a malformed line |
| D12 | Every sorter file stream is unbuffered, and writes go through a budgeted staging array | A `FileStream` buffer under a read-ahead buffer is unbudgeted duplicate work, and the staging array batches writes |
| D13 | Strategies surface the original, unwrapped exception | Otherwise exit codes differ by strategy |
| D14 | `SortCommand` owns the single-run branch | `MergeExecutor` needs no `tryMove` parameter |
| D15 | `FileWriter.Write` takes `onProgress` and a `CancellationToken` | A 100 GB write needs progress, and cancellation must exit 130 |
| D16 | A strategy returns only after all its reads and spills finish | The caller disposes shared resources the moment it returns |

**Rejected:**

- Splitting `ChunkReader`: what remains is one job.
- An interface for `MergeExecutor`: MP-10 uses a real directory.
- Sharing the chunk sort's comparer with the merge.
- Sharing the line grammar between the two programs: GN-09 checks it instead.

---

## 13. Out of scope

- Test case tables, which are in the test strategy.
- The README.
- Benchmark methodology beyond D7.
- A lenient mode for malformed lines, which the README names as a possible extension.
