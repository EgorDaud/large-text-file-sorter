# Design Specification: Contracts, Entities, and Public APIs

Companion to [test-strategy.md](test-strategy.md). That document settles *what the programs do*; this one settles *what the types are*. The behavioural contract table at the top of the test strategy is authoritative and is not restated here. [measurements.md](measurements.md) holds the measured figures; any number stated here is the same number it carries.

Scope: two console programs targeting .NET 10. A generator that writes a `<Number>. <String>` file to a byte-size target with controllable duplicate string parts, and a sorter that orders such a file by string part ascending, then number ascending, at roughly one hundred gigabytes under a bounded memory ceiling.

---

## 1. The organising constraint

The test strategy names five seams: the splitter separable from file I/O, the merge accepting in-memory sequences, placement separable from the volume, the capacity decision separable from the free-space probe, and generator composition separable from writing. The obvious reading is five interfaces — every one of which would have exactly one production implementation and one test double, the single-implementation-interface pattern the assignment brief calls out by name.

Every seam here is instead one of three things:

| Seam mechanism | Used for | Test substitution |
|---|---|---|
| A pure function over `ReadOnlySpan<byte>` | Parsing, comparison, line boundaries, budget arithmetic, capacity arithmetic, merge planning | Call it with a byte array |
| A `Stream` parameter | Merge inputs and output, run spilling, generator output, sorter input | Pass a `MemoryStream` |
| A named `delegate` | Run-generation scheduling, the move-versus-copy decision in placement | Pass a lambda |

**The sorter defines no interfaces of its own.** It defines two delegate types and implements `IComparer<T>` from the BCL once, for the chunk sort (4.3); the merge's loser tree calls the comparison directly. The one abstraction genuinely earned is `RunGenerationStrategy` (section 6), because it has two real production implementations that both ship, are both selectable at runtime, and are both exercised by the same suite.

---

## 2. Solution layout

```
LargeTextFileSorter.slnx
  src/FileSorter/                 exe   the sorter
  src/TestFileGenerator/          exe   the generator
  tests/FileSorter.Tests/
  tests/TestFileGenerator.Tests/
  benchmarks/FileSorter.Benchmarks/
```

No shared class library. The two programs share only the knowledge that the separator byte is `.`; duplicating one constant is better than a `Common` project, which is a layer-named folder wearing a different hat. That duplication is safe only because GN-09 feeds generated output through the sorter's own parser.

```
FileSorter/
  Program.cs                      dispatch (--help, --verify, sort mode), Ctrl+C wiring, the
                                  exception-to-exit-code ladder, phase-two branch, ActorSystem lifetime
  Startup/
    CommandLine.cs                argument parsing for both modes, and the size-suffix parser
    ExitCodes.cs                  the process exit code constants
    CapacityProbe.cs              output-directory and --temp validation, the free-space probe
    ProgressReporter.cs           the periodic stderr lines and the merge-shape summary
    SorterOptions.cs
    MemoryBudget.cs               MemoryPlan Calculate(...)
    MemoryPlan.cs
    TempCapacity.cs               CapacityDecision Evaluate(...) and its two volume-specific forms
    TemporaryRunSet.cs            temp file creation and cleanup, used by both phases
  LineFormat/
    LineDescriptor.cs
    LineCursor.cs                 line boundaries, terminators, carry-over, length limit
    LineParser.cs                 the line grammar
    LineOrder.cs                  the three-level comparator
    MalformedLineException.cs
  RunGeneration/
    BufferPool.cs                 BufferPool, PooledBuffer
    Chunk.cs
    ChunkReader.cs                sequential fill -> Chunk
    ChunkSorter.cs                descriptor sort
    ChunkSpiller.cs               sort, write run, release the pool slot
    RunGenerationStrategy.cs      the delegate, and ChunkSpill
    RunGenerationDriver.cs        phase one's driver: builds the stream, pool, reader and spiller
    AkkaRunGeneration.cs          implementation A
    ChannelRunGeneration.cs       implementation B
  Merging/
    MergePlanner.cs               pass and group arithmetic
    MergePass.cs
    MergeExecutor.cs              drives the passes, owns intermediate runs
    MergeDriver.cs                phase two's driver: the progress line around one ExecuteAsync
    RunCursor.cs
    RunCursorBuffers.cs           the two read-ahead windows and descriptor array per cursor
    KWayMerge.cs                  one group of runs into one output
    RunPlacement.cs
    MergeProgress.cs              bytes written and output-wait time, shared across one merge call
    RangePartitioner.cs           sample, then locate one partition of every run by key range
    RangePartition.cs             the located offsets, slice lengths and output offsets
    RunSliceStream.cs             one worker's read view of [start, end) of a run
    OutputSliceStream.cs          one worker's write view of [start, end) of the output, guarded
    SparseFile.cs                 FSCTL_SET_SPARSE on the partitioned output, Windows only
  Verification/
    VerifyOptions.cs
    VerificationResult.cs         FileScanReport, VerificationOutcome
    OutputVerifier.cs             --verify: adjacent-order check plus count/hash agreement
    VerifyCommand.cs              --verify's driver

TestFileGenerator/
  Program.cs
  Generation/
    GeneratorOptions.cs
    Vocabulary.cs                 the fixed string-part pool
    LineComposer.cs               composition and sizing, no file I/O
    FileWriter.cs                 the loop that drives LineComposer into a Stream
```

`TemporaryRunSet` sits in `Startup/` beside `TempCapacity` because the temporary directory is one concern with one owner: the capacity check decides whether it can hold the run, and the run set creates and deletes everything inside it. Both phases receive it; neither owns it.

`Startup`, `LineFormat`, `RunGeneration`, `Merging` and `Generation` are features. There is no `Services`, `Models`, `Interfaces`, `Helpers`, `Common` or `Utils` folder anywhere, and no folder is named after a test tier. Test projects mirror the production folders name for name, plus a top-level `EndToEnd/` for the property and integration tiers.

Almost every type is `internal`; these are two utilities, not a library with a public API. Tests reach them through `InternalsVisibleTo`.

---

## 3. Entities

### 3.1 LineDescriptor

```csharp
internal readonly struct LineDescriptor
{
    public readonly ulong Prefix;       // first 8 bytes of the string part, big-endian, zero-padded
    public readonly long  Number;       // parsed once at read time, never re-parsed
    public readonly int   StringOffset; // string part start, absolute into the chunk buffer

    private readonly long _offsetAndLength; // Offset (high 32 bits) and Length (low 32 bits) packed

    public LineDescriptor(ulong prefix, long number, int offset, int length, int stringOffset);

    public int Offset => (int)(_offsetAndLength >> 32);   // raw line start, absolute into the buffer
    public int Length => (int)_offsetAndLength;           // raw line length, terminators excluded
    public int StringLength => StringLengthOf(Offset, Length, StringOffset);

    public static int StringLengthOf(int offset, int length, int stringOffset);
    public static ulong BuildPrefix(ReadOnlySpan<byte> stringPart);
}
```

Thirty-two bytes, no reference field, so an array of descriptors is one contiguous block and sorting reorders only descriptors — the buffer is never rearranged and no line is copied or decoded on the hot path. `Offset` and `Length` share one packed field to buy *field count*, not bytes: a five-field version measured out to the same thirty-two bytes but ran 40% slower on the in-memory merge, because RyuJIT's struct promotion stops at four fields. `MemoryPlan` reads the size via `Unsafe.SizeOf` rather than a constant, so the budget stays correct if a field is added.

`StringOffset` is stored rather than recomputed; the alternative is a sixteen-byte descriptor that rescans for the separator on every comparison. `Prefix` caches the first eight bytes of the string part so most comparisons never touch the buffer, and `BuildPrefix` is the one place that layout is produced, called by `ChunkReader.Describe`, `RunCursor.Describe` and the test fixtures alike, so a descriptor's prefix cannot disagree with its bytes. A packed twenty-four-byte layout is not possible: leading zeros are accepted in the number, so the distance from line start to string start is unbounded and does not fit a byte.

`Offset` and `StringOffset` are absolute into the chunk buffer, assigned after the carry-over copy, so a carried fragment's descriptor is written once against its final position.

### 3.2 Chunk

```csharp
internal readonly record struct Chunk(PooledBuffer Buffer, int Count);
```

One pool slot plus a line count. The slot carries both the raw bytes and the descriptor array (5.1), so a chunk holds no independently allocated state and there is exactly one thing to release. `Buffer.Lines` is longer than `Count`; consumers respect `Count`, never `Lines.Length`.

### 3.3 Startup values

```csharp
internal sealed record SorterOptions(
    string InputPath, string OutputPath, string TempDirectory,
    long MemoryBudgetBytes, int MaxLineLength, int Parallelism, Pipeline Pipeline);

internal enum Pipeline { Akka, Channels }

internal readonly record struct MemoryPlan(
    int ChunkSize,
    int DescriptorCapacity,            // descriptors allocated per pool slot
    int Parallelism,
    int MergeFanIn,
    int ReadAheadBufferSize,           // PER MERGE WORKER; see 7.7
    int ReadAheadDescriptorCapacity,   // descriptors allocated per run cursor
    int OutputBufferSize,
    int SpillBufferSize,
    int MergeParallelism)              // merge workers, each with its own cursors and output buffer
{
    public static int DescriptorSize => Unsafe.SizeOf<LineDescriptor>();

    // One slot being parsed, one being filled ahead of it, and Parallelism in flight.
    public int PoolCapacity      => Parallelism + 2;
    public long PendingBytesSize => ChunkSize;   // see 5.2

    public long BytesPerSlot =>
        (long)ChunkSize + (long)DescriptorCapacity * DescriptorSize;

    public long WorstCasePhaseOneBytes =>
        (long)PoolCapacity * BytesPerSlot + PendingBytesSize + (long)Parallelism * SpillBufferSize;

    public long BytesPerRunCursor =>
        2L * ReadAheadBufferSize + (long)ReadAheadDescriptorCapacity * DescriptorSize;

    // KWayMerge.LoserTree's own per-slot cost, read via Unsafe.SizeOf rather than a literal.
    public static int LoserTreeBytesPerFanInSlot => Unsafe.SizeOf<KWayMerge.RunHead>() + sizeof(int);
    public const int PartitionOffsetEntrySize = sizeof(long);

    public long MergeMetadataBytes =>
        (long)MergeParallelism * MergeFanIn * LoserTreeBytesPerFanInSlot
        + (MergeParallelism > 1 ? (long)MergeFanIn * (MergeParallelism + 1) * PartitionOffsetEntrySize : 0);

    public long WorstCasePhaseTwoBytes =>
        (long)MergeParallelism * MergeFanIn * BytesPerRunCursor
        + (long)MergeParallelism * OutputBufferSize
        + MergeMetadataBytes;
}

internal enum CapacityOutcome { Sufficient, Insufficient, Unknown }

internal readonly record struct CapacityDecision(
    CapacityOutcome Outcome, long RequiredBytes, long AvailableBytes);
```

Every derived quantity is a computed member, not a constructor parameter, so no plan can exist whose budget arithmetic and actual allocation disagree.

**`MergeParallelism` multiplies both of phase two's terms.** Every worker opens every run of the group it merges — `MergeFanIn` caps *one* worker's run count, not a total shared between them — and each stages its own output. The extra `(MergeParallelism − 1) × OutputBufferSize` is taken out of the budget *before* `MemoryBudget` solves for the window, so it shaves the window rather than overshooting. `ReadAheadBufferSize` is therefore the window one worker's cursor gets: it is solved against `MergeParallelism × MaxMergeFanIn` cursors, so it falls as workers are added and the product does not move. At `MergeParallelism` 1 every expression reduces to the sequential merge's own cost.

`CapacityDecision` carries no directory: `Evaluate` is a pure decision over two numbers, and the program, which knows the directory it probed, assembles the message.

### 3.4 Merge passes

```csharp
internal sealed record MergePass(
    IReadOnlyList<int[]> Groups,          // run indices, each group no larger than the fan-in
    IReadOnlyList<int>   CarriedForward); // runs not merged this pass
```

Indices, not paths: the planner is pure arithmetic and never learns that runs are files. There is no `MergePlan` wrapper, because its only member would restate `Passes.Count`.

### 3.5 Errors

```csharp
internal sealed class MalformedLineException : Exception
{
    public long   ByteOffset { get; }
    public long   LineNumber { get; }
    public string Preview    { get; }   // offending bytes, truncated to a readable length
}
```

The only custom exception in the solution. Capacity shortfall is a `CapacityDecision`, not an exception; bad arguments produce a usage message; everything else surfaces as the framework exception it already is. An exception hierarchy for a two-program tool is unearned.

---

## 4. LineFormat

### 4.1 LineParser

```csharp
internal static class LineParser
{
    /// line has terminators already stripped. stringStart is relative to the line.
    public static bool TryParse(ReadOnlySpan<byte> line, out long number, out int stringStart);
}
```

The only place that knows the line grammar. It returns `false` rather than throwing, so the hot path has no exception cost and the caller — which knows the byte offset and line number — owns the diagnostic. The boundary is the first `.` in the line: the number is a signed 64-bit integer and cannot contain a period, so no lookahead for a following space is needed. One space immediately after the period is consumed if present. No decoding, no allocation, no encoding validation, and under D2 no length logic.

### 4.2 LineCursor

```csharp
internal ref struct LineCursor
{
    public LineCursor(
        ReadOnlySpan<byte> block, int maxLineLength, long blockBaseOffset = 0, long firstLineNumber = 1,
        bool stripByteOrderMark = true, bool stripCarriageReturn = true);

    public bool TryReadLine(out int offset, out int length);  // complete lines only
    public int  CarryOffset { get; }   // start of the trailing partial line
    public int  CarryLength { get; }   // 0 when the block ends on a terminator
}
```

Never sees a file: it operates on a supplied block and reports the trailing partial line, which is what makes the most defect-prone logic in the sorter exhaustively testable in memory. `blockBaseOffset` and `firstLineNumber` place a block in file coordinates for a diagnostic.

`stripByteOrderMark` and `stripCarriageReturn` both default to `true`, the rules written for the *user's input file*. Every caller reading something the sorter wrote itself — `RunCursor` over a run file, `OutputVerifier` over the sorter's own output — passes `false`, since those files are `\n`-terminated throughout and a `\r` before one there is always content. `RangePartitioner.ReadLineAt` applies the same rule by hand for the same reason.

**Used in both phases**, driven over chunk buffers by phase one and over read-ahead windows by phase two. One implementation of the terminator rules everywhere is what stops the two phases disagreeing about a stray carriage return, which would be invisible until it corrupted a sort key. Nothing else is shared between `ChunkReader` and `RunCursor`: the ten-odd lines of fill-scan-carry that look alike sit either side of a pooled acquisition and a fan-out, and fusing them would be false economy.

Being a `ref struct`, it may be a local in an `async` method under C# 13 provided no instance is live across an `await`; both call sites await the fill first and then run the cursor synchronously to completion.

**Where the maximum line length is enforced, D2.** The limit is enforced here, not in `LineParser`, because the cursor is what must bound its own carry-over — enforcing at the point where the bound is required makes the memory guarantee structural. Observable behaviour is identical. CB-12 covers the over-long case and CB-16 the just-inside boundary; the identifiers LP-23 and LP-24 belong to no case and are not reused.

### 4.3 LineOrder

```csharp
internal static class LineOrder
{
    public static int Compare(
        in LineDescriptor a, ReadOnlySpan<byte> bufferA,
        in LineDescriptor b, ReadOnlySpan<byte> bufferB);
}
```

Four steps, of which three are levels of the ordering. Before any buffer is touched, `Compare` checks the two cached `Prefix` values and returns the sign of that `ulong` comparison if they differ. **Zero-padding a short string part is order-preserving**, because 0 is the minimum byte and ordinal comparison already orders a proper prefix before its extension — so whenever the padded prefixes differ, the first differing position gives the same sign the full comparison would, for arbitrary bytes including `0x00` and invalid UTF-8. When they tie, `Compare` falls through to the three levels: string part ordinally over raw bytes, then number, then raw line bytes. Pure, allocation-free, never decodes, never consults culture.

Two buffers, because the merge compares heads from different runs; the chunk sorter passes the same buffer twice. One comparator, so the two phases cannot diverge.

**The third level is a property of the number grammar, not a patch.** Leading zeros are accepted, so `007. Apple` and `7. Apple` tie; the number is optionally signed, so `+5. Apple` and `5. Apple` tie for an independent reason that survives a parser rejecting leading zeros. Without a third level their order falls to chunk boundaries, which are decided by the memory budget, and the same input under two budgets produces different bytes. OC-13 and OC-17 are the cases.

**`IComparer<T>` is implemented exactly once**, by `ChunkSorter.DescriptorComparer` — a private `readonly struct` constructed inside the one call that owns the buffer it holds. A comparer of the same shape exposed as a shared type would invite a descriptor and a buffer from different chunks to meet, and the sort would silently read the wrong bytes. `KWayMerge`'s loser tree compares through a private static method instead, since the tree is private to that file and there is no boxed interface call left to justify one.

**The reference oracle is a deliberate third implementation and must not call any of this.** `InternalsVisibleTo` gives the `EndToEnd/` tests full access to `LineOrder`, so the prohibition is a convention that has to be stated where a maintainer will see it: a comparator with a case-sensitivity defect would otherwise produce identical wrong output on both sides and the headline property test would pass. The oracle's route is different by construction — decode each line, compare string parts with `SequenceCompareTo`, then the parsed numbers, then the raw lines. Its *line splitting* does delegate to `NaiveLineFormat`, the splitter oracle the boundary sweeps already check `LineCursor` and `RunCursor` against, rather than maintaining a second reading of the end-of-file rule that could drift; `NaiveLineFormat` references nothing in `FileSorter.LineFormat` either, so that costs nothing against the independence requirement.

---

## 5. RunGeneration

### 5.1 BufferPool

```csharp
internal sealed class BufferPool
{
    public BufferPool(int bufferSize, int descriptorCapacity, int capacity);
    public ValueTask<PooledBuffer> AcquireAsync(CancellationToken ct);
    public int BufferSize  { get; }
    public int Outstanding { get; }
}

internal readonly struct PooledBuffer : IDisposable
{
    public byte[]           Bytes { get; }
    public LineDescriptor[] Lines { get; }
    public void Dispose();
}
```

A slot owns **both** arrays, allocated once and never reallocated (D9). That is what makes the memory guarantee true rather than merely budgeted: a descriptor array for a large chunk of short lines is a multi-million-element LOH allocation, and a fresh one per chunk for the life of a hundred-gigabyte run would leave peak working set a function of GC behaviour. It also means a chunk has exactly one thing to release.

`AcquireAsync` waits when the pool is exhausted and never allocates an extra buffer. **This is the program's flow control, in both strategies** — see section 6. Implementation: a bounded `Channel<int>` of slot indices pre-filled at construction, so acquire is a `ReadAsync` and release a `TryWrite`, with no semaphore, lock or condition variable. `PooledBuffer` carries its slot index and a `bool[] _outstanding` makes a double release, or a release of a buffer the pool never issued, an O(1) loud failure. Neither array is cleared on release; every consumer respects the recorded length rather than the capacity.

### 5.2 ChunkReader

```csharp
internal sealed class ChunkReader : IAsyncDisposable
{
    public ChunkReader(Stream input, BufferPool pool, int maxLineLength);
    public ValueTask<Chunk?> ReadNextAsync(CancellationToken ct);  // null when the input is exhausted
    public long BytesConsumed { get; }
    public long LinesRead     { get; }
    public ValueTask DisposeAsync();
}
```

Sequential by construction: one per run, never called concurrently, and every caller `await using`s it. The input is a `Stream`, so unit tests drive the whole read path from a `MemoryStream`.

**The reader overlaps the next chunk's fill with the current chunk's parse.** Each call awaits the fill already in flight for the slot it is about to deliver, acquires the next slot and issues *its* fill, and only then parses the chunk just filled. Issuing that fill *before* the parse rather than after is what makes the overlap real — after compiles, passes every byte-identity test, and overlaps nothing, which only a measurement catches. Every fill's fresh bytes land at a fixed offset `Reserve = maxLineLength + 2`, decided before the carry ahead of it is known, and that fixed destination is what lets the fill be issued early. (A maximum-length `\r\n` line whose fill lands exactly on its CR carries `maxLineLength + 1` bytes, so `Reserve` needs a spare byte on top.)

Folding the carry in has two paths:

- **Carry ≤ Reserve, the common case.** Copy it into `[Reserve − carry, Reserve)` synchronously; the next slot's fill is handed off still running, not awaited.
- **Carry > Reserve** — this chunk ended on descriptor exhaustion (D9) with whole undescribed lines in hand. The next slot is rebuilt: its own fill is awaited here, the one place the overlap is lost; the carry goes to its head, as much fresh read as still fits moves in behind it, and anything displaced waits in a pending buffer for the following fill, since a forward-only stream cannot be asked for those bytes twice. How often this runs is **not a fixed rarity** — it is decided per chunk by whether the descriptor array or the byte buffer empties first, which is decided by `AssumedMeanLineLength` against the data's real mean (8.1).

Whether either is needed is unknown until the parse runs, so the next fill is issued unconditionally and, on the chunk that turns out to be last, released unused — observed first, the same discipline `DisposeAsync` uses. A pool of capacity 1 therefore cannot drive a non-empty stream through this type at all.

**Exhaustion is recomputed locally**, from each fill's own returned count against the one constant every ordinary fill requests, rather than read off a shared flag. With two fills in flight across two chunks, a shared flag lets whichever resolves first answer a question belonging to a different chunk: a chunk's genuine trailing carry gets folded in as a false final line on the strength of a *later* chunk's look-ahead discovering the end of stream. This is recorded because the shared flag is the more natural first design and quietly loses bytes rather than throwing; `ByteIdentityPropertyTests` and three `ChunkReader` unit tests cover it.

**Finalising the trailing fragment strips a bare trailing carriage return first.** A lone unterminated `\r` counts toward `CarryLength` but not toward the content bound `LineCursor` enforces, so the caller finalising the fragment applies the same rule the terminated branch applies: it is the first half of a terminator whose `\n` never arrived. Finalising `"...Apple\r"` as content would make it something this reader's own output, read back through this branch, could never produce — and a correct sort of such a file would then fail `--verify`'s hash check against its own input.

**Release discipline, D11.** Every call holds at most two slots inside a nested `try`/`catch` and disposes whichever it still owns on any throw, most often a malformed line. `DisposeAsync` observes and swallows any fill still in flight before releasing the slot — the same "never dispose a stream under an overlapped read" rule 7.2 states.

### 5.3 ChunkSorter

```csharp
internal static class ChunkSorter
{
    public static void Sort(Span<LineDescriptor> lines, byte[] buffer);
}
```

**A full MSD radix in front of the BCL introsort**, descending one byte at a time, with the introsort finishing only the ranges the radix hands it.

- **Depths 0–7** read byte `d` of `Prefix` — 256 buckets, no buffer dereference. `LineOrder.Compare` decides on `Prefix` whenever two prefixes differ and `Prefix` is big-endian, so ordering the buckets by byte `d` is exactly the decision the comparator makes.
- **Depths 8 and beyond** read byte `d` of the string part from the chunk buffer, into **257** buckets: bucket 0 is "the string part ended before this byte", buckets 1–256 are byte values 0–255. Descriptors reaching depth 8 tie on the whole padded prefix, so the comparator has already fallen through to `SequenceCompareTo`, and byte 8 is the first byte the prefix did not cover. The seam holds because a descriptor with a real byte at depth 8 has at least nine string bytes, so its first eight *are* the prefix the range agreed on — padding cannot be involved on that side.
- **Bucket 0 is never recursed into.** It must sort below every real byte, because ordinal order puts a proper prefix strictly before its extension, including an extension whose next byte is a real `0x00`. At depth 8 it can hold string parts of different lengths the padded prefix could not tell apart (`"ab"` and `"ab\0"`), so it is finished with the full comparator; past depth 8 it holds string parts that are equal outright. An empty string part is in bucket 0 at every depth and so orders first.
- **A level that does not split is not a level of recursion.** The counting pass reports the single bucket everything landed in; the permutation is skipped and the same range retried one byte deeper in the same frame. At a string-byte level, everything landing in bucket 0 ends the descent instead.
- **The descent stops at depth 16 or 32 descriptors**, whichever comes first, and the remainder goes to `lines.Sort(new DescriptorComparer(buffer))`. Both constants are performance bounds, never ordering ones.

The permutation is in place — no second descriptor array — because the budget has no term for one and a chunk's descriptor array can be hundreds of MiB. Nothing needs stability: whatever the radix hands the introsort is still ordered by the full comparator, raw-bytes tie-break included, so the output is the same total order regardless of which permutation the radix produced.

No hand-rolled quicksort. The BCL sort is correct, already tuned for presorted and adversarial cases, and deletes a class of defect; that holds at the base case too, and it is paid for — a hand-rolled insertion sort below the 32-descriptor threshold measures about 5% faster on the whole chunk sort, and the BCL sort is used anyway.

### 5.4 ChunkSpiller and the delegates

```csharp
internal delegate Task<string> ChunkSpill(Chunk chunk, CancellationToken ct);

internal sealed class ChunkSpiller
{
    public ChunkSpiller(TemporaryRunSet runs, int spillBufferSize);

    /// Matches ChunkSpill. Sorts the chunk, writes it as a run file, returns the run path.
    /// Releases the chunk's pool slot in a finally, on the success and throw paths alike.
    public Task<string> SpillAsync(Chunk chunk, CancellationToken ct);
}
```

Every slot handed to the spiller is released by the spiller, on both paths; together with `ChunkReader`'s `catch`, that is the whole release discipline.

The run file is opened with `bufferSize: 1` like every read stream (D12): the `FileStream` buffers nothing, and a staging array sized at `SpillBufferSize` takes its place, each line and its terminator copied in and one `WriteAsync` issued per full buffer. Nothing else buffers a *write* the way a read-ahead buffer already buffers a read, which is why the staging array gets a budgeted allocation.

The run's final size is known before the first byte is written — the sum of each sorted line's length plus its terminator — so `SetLength` is called immediately after opening, turning a file grown 64 KiB at a time into one allocation. That is safe only because the total is exact by construction and checked: `SpillAsync` compares the stream's `Position` against it once every line is staged and throws on a mismatch, because a wrong-length run reads as a truncated last line rather than failing loudly.

---

## 6. The run-generation seam

```csharp
/// A strategy schedules work; it owns none of its caller's resources. It must surface the
/// first branch failure to its caller as the original exception, unwrapped, and it must
/// not complete until every read and every spill it started has finished, on the success,
/// failure and cancellation paths alike -- the caller disposes the reader, the pool and
/// the run registry the moment it does. A read is a ReadNextAsync call: the look-ahead
/// fill ChunkReader issues from inside one is the reader's own, and DisposeAsync observes
/// it before the stream under it closes. Run order is undefined.
internal delegate Task<IReadOnlyList<string>> RunGenerationStrategy(
    ChunkReader reader,
    ChunkSpill  spill,
    int         parallelism,
    CancellationToken ct);

internal static class AkkaRunGeneration
{
    public static Task<IReadOnlyList<string>> RunAsync(
        ChunkReader reader, ChunkSpill spill, int parallelism,
        IMaterializer materializer, CancellationToken ct);
}

internal static class ChannelRunGeneration
{
    public static Task<IReadOnlyList<string>> RunAsync(
        ChunkReader reader, ChunkSpill spill, int parallelism, CancellationToken ct);
}
```

**`AkkaRunGeneration`**: `Source.UnfoldAsync` feeding `SelectAsyncUnordered(parallelism, chunk => Task.Run(() => spill(chunk, linked)))` into `Sink.Seq<string>`. The `Task.Run` is load-bearing: `SelectAsyncUnordered` invokes its mapper on the fused stage's own actor thread, and `SpillAsync` runs synchronously up to its first incomplete await, which is *after* the sort — so without the offload every chunk is sorted on the stream's single thread, one at a time, and the source is not pulled until that sort returns. Measured, that put Akka at 3.8x Channels on the sort alone at parallelism 4, and 1.1x once offloaded. `Task.Run` is given `CancellationToken.None` rather than a token that can already be cancelled: handed one that is, it would skip the body, and with it the `finally` that releases the chunk's pool slot. There is a small adapter between `ChunkReader` and `UnfoldAsync`, since `ReadNextAsync` returns `ValueTask<Chunk?>` while `UnfoldAsync` wants `Task<Option<(TState, TElement)>>`.

**`ChannelRunGeneration`**: a `Channel<Chunk>` bounded at `parallelism`, one producer loop draining the reader into it, and `Parallel.ForEachAsync` over `ReadAllAsync(ct)`. Run paths land in a `ConcurrentBag<string>`, which is the right structure precisely because run order is undefined.

Both are short and own nothing the caller gave them. Three constructs survive that reading and all three are named here: `ChannelRunGeneration`'s `finally` completing the channel writer, since a channel nobody closes leaves the consumer waiting forever on both paths; `AkkaRunGeneration`'s D13 `catch`; and its linked `CancellationTokenSource` and join, described under *Completion*.

### What actually bounds in-flight work

**The pool does, in both strategies.** `PoolCapacity` is `Parallelism + 2`, so `BufferPool.AcquireAsync` blocks the reader before either the channel's bound or the Akka graph's internal buffer is ever the binding constraint. Neither scheduler supplies the backpressure; both inherit it.

This is worth stating precisely rather than crediting the library, for two reasons. It is the stronger claim — peak memory is a property of the plan and cannot be changed by swapping schedulers, which is what the cross-strategy byte-identity and flatness tests demonstrate. And the weaker claim is untestable in a useful way: an assertion written against the channel's bound would pass against a strategy with no flow control at all. The streaming tier therefore asserts `BufferPool.Outstanding` never exceeds `PoolCapacity`.

### Substitutability, D13

The two strategies must be interchangeable in every respect a caller can observe, and the one that reaches the user is the exception arriving at `Program`. If a `MalformedLineException` arrives bare under one strategy and wrapped under the other, `catch (MalformedLineException)` fires under one and not the other, and the same corrupt file exits 1 under Akka and unhandled under Channels.

**Which strategy needs work to honour that is the opposite of the obvious guess.** A bare `await` on a faulted `Task` already throws the first inner exception, so `Parallel.ForEachAsync`'s `AggregateException` never reaches the caller and `ChannelRunGeneration` needs no unwrapping at all. It is the materialized task behind Akka's `RunWith` that keeps its stage failure inside an `AggregateException` the await leaves intact, so `AkkaRunGeneration` peels it with `ExceptionDispatchInfo.Capture(...).Throw()`. The streaming tier asserts that the exception *type* reaching the caller is identical across both, which is the assertion that caught this.

### Completion, D16

A strategy's task completing has to mean the run is over, because `RunGenerationDriver` treats it that way: the reader and pool go out of scope and `TemporaryRunSet.Dispose` deletes every run path it knows about. A spill still running past that point sorts into a pool being torn down and calls `CreateRunPath` on a registry already walked, leaving a run file nothing will delete — on the one path, a failed run, where leaving no debris matters most.

`ChannelRunGeneration` gets this for free: `Parallel.ForEachAsync` completes only once every worker has finished. Akka's is the opposite shape — `SelectAsyncUnordered` fails the graph as soon as one mapper faults and the materialized task completes at that instant, while the sibling `Task.Run` spills carry on writing; terminating the `ActorSystem` does not touch them either, since thread-pool work was never part of that ownership contract.

`AkkaRunGeneration` therefore closes the gap itself: one `CancellationTokenSource` linked to the caller's token, handed to the reader and every spill, with every read and spill tracked and a `finally` that cancels the source and then waits for both. The cancellation is what makes the wait finite. **Neither half of that tracking is a per-chunk term.** `Source.UnfoldAsync` pulls strictly sequentially, so at most one read is outstanding and one `Task?` field holds it. Spills genuinely run concurrently, so they are *counted* rather than collected: a counter incremented **before** the spill's task is created — a spill already running while uncounted is one the drain could report as finished — and a `TaskCompletionSource` the `finally` arms once, completing when the count next reaches zero. Zero only means the end of the run once the graph has stopped, which is why the latch is armed by the drain rather than kept armed throughout.

What is joined is `ReadNextAsync` calls, not every read in flight: `ChunkReader` hands its look-ahead fill forward as `_prefetchFillTask` (5.2), and that fill is the reader's own to observe. `RunGenerationDriver` declares `input` before `reader`, `await using` unwinds in reverse, and `DisposeAsync` awaits the outstanding fill before `input` closes.

The streaming tier asserts this the only way it can be asserted — a gate, not a delay. One spill is held open on an uncompleted `TaskCompletionSource` while the run fails around it, and the strategy's own task must still be incomplete: SL-03 for a spill that throws, SL-04 for a malformed line, SL-05 for cancellation, each against both strategies.

### Why two

The case, at its real strength rather than inflated. Two of these three would be partly reachable with one implementation; the third would not.

1. **The streaming tier runs as one parameterised suite against both.** Much of the same confidence is available from one implementation at two parallelism settings. What the second adds is that a behaviour holding for one scheduler and not the other is necessarily a defect rather than a property of the library, which is what D13 pins.
2. **The byte-identity property test runs under both**, so determinism becomes identical output bytes across two schedulers, two budgets and two parallelisms.
3. **The benchmark has a baseline.** This is the benefit not otherwise obtainable: throughput against a dependency-free implementation on the same input, budget and disk turns the choice of Akka.Streams from a preference into a documented decision with a number attached.

`Program` builds the strategy inside the branch that owns Akka's resources, so `--pipeline channels` never constructs an `ActorSystem` and no nullable materializer is ever in scope:

```csharp
if (options.Pipeline is Pipeline.Akka)
{
    using var system = ActorSystem.Create("sorter");
    using var materializer = system.Materializer();
    return await SortAsync(options, (r, s, p, ct) =>
        AkkaRunGeneration.RunAsync(r, s, p, materializer, ct), ct);
}
return await SortAsync(options, ChannelRunGeneration.RunAsync, ct);
```

Run order from a strategy is undefined — both schedulers complete out of order and `MergeExecutor` treats the list as a set. The honest limit: two implementations demonstrate that a behaviour is not scheduler-specific; they do not demonstrate that either is correct under interleavings neither happened to produce.

---

## 7. Merging

**The merged output's bytes are unique, and that is a consequence of the third comparison level rather than something any merge happens to achieve.** `LineOrder.Compare` falls through to the raw line bytes, so two lines that compare equal are byte-identical, and the output is the input multiset in a total order whose only ties are between indistinguishable values.

### 7.1 MergePlanner

```csharp
internal static class MergePlanner
{
    public static IReadOnlyList<MergePass> Plan(int runCount, int fanIn);
}
```

Pure arithmetic over two integers. Every run appears in exactly one group per pass, no group exceeds the fan-in, no group has size one, each pass strictly reduces the run count, and the final pass produces exactly one output.

### 7.2 RunCursor

```csharp
internal sealed class RunCursor : IAsyncDisposable
{
    public RunCursor(Stream run, RunCursorBuffers buffers, int maxLineLength, int? runIndex = null);

    public bool             TryMoveNext();
    public ValueTask<bool>  MoveNextAsync(CancellationToken ct);
    public LineDescriptor   Current { get; }
    public byte[]           Buffer  { get; }
}
```

Holds one head per run and never buffers a whole run. Both read-ahead buffers **and the descriptor array** are caller-supplied, grouped into one `RunCursorBuffers` record struct (`FirstWindow`, `SecondWindow`, `Descriptors`) so `MergeExecutor` has one array to build rather than three of possibly different lengths. The descriptor array is supplied for the same reason the bytes are: a cursor scans a whole window and hands the lines out one at a time, so allocating that per fill would be an LOH allocation for every window of every run of every pass — the churn D9 removed from phase one — and it appears in no term of `WorstCasePhaseTwoBytes`, which would make that figure an estimate again. D9's either-resource rule applies here exactly as in `ChunkReader`.

**Two equal-size windows rather than one**, so the fill for the window not being delivered runs while the other drains. The instant a window has been scanned — carry offset and length known, descriptors produced — the carry is copied into the *front of the other buffer* and a read is started there without being awaited, before a single descriptor of the just-scanned window is handed out. `Buffer` always names the window currently being delivered, which is exactly what `KWayMerge`'s invariant (7.3) needs to stay stable; the other is free to be mid-fill and never becomes `Buffer` until every descriptor of the current one is gone. Shifting and re-filling the *same* buffer, awaited rather than prefetched, happens in exactly two places, both with nothing to overlap: a run's first window, and resuming after the stream is exhausted when the previous window ended on descriptor capacity. `BytesPerRunCursor` charges for both windows; the descriptor array is not doubled, because descriptors are produced only for the window being delivered.

**Delivery is split into a synchronous `TryMoveNext` and the async `MoveNextAsync`.** A window holds thousands of pending descriptors between fills — about 2,700 per worker at the shipped 4 GiB eight-worker plan, roughly 21,800 at one worker — so a call that only hands out the next pending descriptor need not be `async` at all. `TryMoveNext` touches nothing but the pending index and `Current`, so it can never start or observe a fill and changes nothing about the invariant. `MoveNextAsync` tries the same pending descriptor first, so `TryMoveNext() || await MoveNextAsync(ct)` is indistinguishable from a bare `await` except for the state machine it does not pay.

**A malformed line carries a run index, not a path.** A cursor reports `ByteOffset` relative to whatever stream it was handed — the whole run file sequentially, only a worker's slice under a partitioned merge — because that is all it knows. `runIndex` is a label the caller can map back; `MergeExecutor.MergeSliceAsync` is what reads it and builds a corrected exception naming the run's own path and an absolute offset.

**A partitioned merge's slice bound lives outside this type, deliberately.** The bound lives in `RunSliceStream`, a read-only view that starts at `start` and reports end of stream at `end`. This cursor already carries three interacting conditions around the end of its input — a prefetch possibly in flight, a carry surviving a window that ended on descriptor capacity, and the exhausted flag deciding which the next window takes — and a bound inside it would be a fourth, the one that interacts worst, since "the stream had nothing more" and "this slice ends here" would become two reasons for one state. Expressed outside, a slice end *is* the end of stream this cursor already handles.

### 7.3 KWayMerge

```csharp
internal static class KWayMerge
{
    public static Task MergeAsync(
        IReadOnlyList<Stream> runs,
        IReadOnlyList<RunCursorBuffers> cursorBuffers,   // one per run, supplied by the caller; see 7.2
        Stream output,
        byte[] outputStagingBuffer,                      // MemoryPlan.OutputBufferSize, from MergeExecutor
        int maxLineLength,
        MergeProgress? progress = null,
        CancellationToken ct = default);                 // token last, both trailing parameters optional
}
```

`Stream` in, `Stream` out. This is the whole of "the merge accepts in-memory sequences": unit tests hand it `MemoryStream`s and their own arrays and never touch a disk, and there is no `IRunReader` to defend.

`progress` is optional so the dozens of unit, property and benchmark call sites that have no progress line need not supply one. Every write against the merge output — the staging flush, the two-write over-length fallback, and the trailing partial flush — goes through one `TimedWriteAsync` helper that records elapsed ticks and byte count into `progress`, so the progress line and the output-wait instrument are one measurement rather than two that could disagree. `ct` sits last and defaults to `default` so both trailing parameters can be optional at once; a positional caller passing a token sixth does not compile, which is what keeps a token from binding silently to `progress`.

`MergeExecutor` allocates the cursor buffers and the staging buffer once and reuses them across every group of every pass — allocating the fan-in arrays inside `MergeAsync` would put megabytes of LOH allocation on every group, the same churn D9 removed from phase one.

**The merge selects through a private loser tree rather than a BCL `PriorityQueue`** (D3). A 4-ary heap pays a sift-down and a sift-up per output line, each comparison an interface call on a boxed struct; a loser tree pays at most `⌈log2 k⌉` direct comparisons and no boxing. Internals: `RunHead[] heads` (one per run: `byte[]? buffer` plus `LineDescriptor`, with `Buffer == null` standing for an exhausted run) and `int[] tree` (Knuth's construction, leaf `i`'s parent at `(i + k) / 2`), both sized to the fan-in. `RunHead` is `internal` rather than `private` for one reason: `MemoryPlan.LoserTreeBytesPerFanInSlot` reads its real size via `Unsafe.SizeOf` rather than trusting a comment, so the tree is **priced into `WorstCasePhaseTwoBytes`, not disclosed outside it**. Comparisons go through a private static `Compare(in RunHead, in RunHead)`, so there is no comparer type to hand anything to; exact ties break by lower run index for determinism.

```
for (int i = 0; i < count; i++)
{
    cursors[i] = new RunCursor(runs[i], cursorBuffers[i], maxLineLength, runIndex: i);
    if (await cursors[i].MoveNextAsync(ct))
        tree.SetHead(i, new RunHead(cursors[i]));
}
tree.Build();                                    // first tournament, once every head is set

while (tree.WinnerIsAlive)
{
    int run = tree.Winner;                      // read, not removed — nothing is ever dequeued
    Write(cursors[run].Current, cursors[run].Buffer);
    if (cursors[run].TryMoveNext() || await cursors[run].MoveNextAsync(ct))  // only now may its buffer change
        tree.SetHead(run, new RunHead(cursors[run]));
    else
        tree.MarkDead(run);
    tree.Replay(run);                            // the one leaf that changed, back to the root
}
```

**The invariant**, stated at `RunHead`'s declaration: a run's head is refreshed immediately after every advance — `SetHead` or `MarkDead` runs before `Replay` and before the next comparison — and the loop is single-threaded, so no comparison can run while a head's window is mid-refill. The cursor's *other* window is free to be filling; it is not `Buffer` and is never read by a comparison. The hazard is advancing a cursor before its line has been copied out, which can swap `Buffer` out from under a copy in flight, which is why `Write` precedes the advance and `Replay` runs last. KM-11 (2,000 lines per run through 64-byte windows) and `KWayMergePropertyTests` (33-byte windows, 3 descriptors) are the cases that force enough refills to catch a violation; the curated KM cases run too few lines to refill at all.

The `TryMoveNext` disjunct and `Write`'s own synchronous-then-async split exist for the same reason: the common case on both sides — thousands of pending descriptors between fills, hundreds of lines between flushes — almost never needs the async machinery an unconditional call would pay per line. Neither changes what ends up in the output.

One `MergeAsync` call is single-threaded by design; the only asynchrony inside it is per-run read-ahead. **A partitioned merge qualifies that rather than contradicting it:** it runs `MergeParallelism` of these calls at once, each over its own streams, buffers, staging array and output stream. The single object they share is the `MergeProgress`, whose two counters are `Interlocked` adds precisely so they can be.

### 7.4 RunPlacement

```csharp
internal static class RunPlacement
{
    /// tryMove returns false when the move cannot be performed, for example across volumes.
    public static void Place(string runPath, string outputPath, Func<string, string, bool> tryMove);
}
```

The single-run shortcut: when phase one produced one run, phase two moves it to the output path rather than reading and rewriting it, removing a full read and a full write at a hundred gigabytes. The `tryMove` delegate is the named-delegate seam of section 1 — the cross-volume fallback is forced by a two-line lambda in the tests rather than by a second volume. The copy fallback does not write onto the output path directly: it stages beside it and moves into place once the copy has finished without error. `Program` owns the branch (D14):

```csharp
if (runPaths.Count == 1)
    RunPlacement.Place(runPaths[0], options.OutputPath, TryMove);
else
    await executor.ExecuteAsync(runPaths, options.OutputPath, ct);
```

### 7.5 MergeExecutor

```csharp
internal sealed class MergeExecutor
{
    public MergeExecutor(TemporaryRunSet runs, MemoryPlan plan, int maxLineLength);

    public Task ExecuteAsync(IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct);

    public int PassesExecuted { get; }

    public long   BytesWritten       { get; }   // MergeProgress.BytesWritten
    public long   TotalBytesToWrite  { get; }   // exact, computed once from MergePlanner's own plan
    public double OutputWaitSeconds  { get; }   // SUMMED across workers under a partitioned merge

    public int    MergeParallelismUsed  { get; }   // 1 unless the partitioned path actually ran
    public double SplitterSeconds       { get; }   // 0 on the sequential path
    public double PartitionImbalance    { get; }   // largest worker slice over smallest
    public double SlowestWorkerSeconds  { get; }
    public double QuickestWorkerSeconds { get; }
}
```

This block lists the members the notes below are about, not every member: `PlannedPasses` and `OutputOpened` (which gates deleting a partial output on failure) are omitted.

This is D10 — `MergePlanner` produces arithmetic, `KWayMerge` merges one group, and this is what turns one into the other.

**Allocation.** `ExecuteAsync` allocates `min(runPaths.Count, plan.MergeFanIn)` cursors' worth of state once and reuses it for every group of every pass: two read-ahead buffers plus one descriptor array per cursor (not two — descriptors are produced only for the window being delivered). That belongs in `ExecuteAsync` rather than the constructor because `MergeFanIn` runs into the thousands and sizing for the worst case on a three-run sort would be absurd. The output staging buffer is the exception, allocated in the constructor because its size is a fixed constant rather than a function of the fan-in. With every input stream unbuffered and the output staged into that array (D12), those arrays are what `WorstCasePhaseTwoBytes` bounds:

```
MergeParallelism × MergeFanIn × (2 × ReadAheadBufferSize + ReadAheadDescriptorCapacity × DescriptorSize)
  + MergeParallelism × OutputBufferSize                                                        ≤ budgetBytes
```

**A third addend, `MergeMetadataBytes`, completes it.** The loser tree `KWayMerge` builds per call is about 90 KB at the 2,048 fan-in ceiling; under a partitioned merge `MergeParallelism` of them are live at once — about 720 KB at the shipped 8/2,048 pair — plus `RangePartition.RunOffsets` at its worst-case width, about 147 KB. Both go through the same budget-derived arithmetic as everything else, rather than being disclosed as an excess over it. **What is transient** is `RangePartitioner`'s own sampling and probe buffers (7.7): they are allocated and released entirely inside `Locate`, which returns before the output is preallocated or a single worker starts, so they never coexist with the figures above and neither is charged against the other.

One `MergeProgress` has the executor's own lifetime, shared by every `MergeAsync` call it makes, because the progress line needs a running total across the whole call. `TotalBytesToWrite` is computed once, immediately after `Plan` runs, by walking the same plan over run *sizes* rather than paths — a run merely carried forward contributes nothing until the pass that merges it, exactly mirroring what the loop then does. It is exact rather than an estimate, because run files are `\n`-normalised and the plan is pure arithmetic fixed before any byte is read.

**Behaviour.** For each group: open the group's runs unbuffered, open the group's output unbuffered, merge into a new run from `TemporaryRunSet`, and **delete the group's inputs as soon as that group's merge completes** — per group rather than per pass, which is what keeps peak temporary usage near the input size rather than near twice it, and so makes the twice-the-input startup estimate a genuine upper bound. Runs in `CarriedForward` pass through untouched. The final pass writes directly to `outputPath`, so the last full copy of the data is written exactly once. Before each merge, `MergeGroupAsync` sums `FileInfo.Length` over the group's inputs and `SetLength`s the output to that sum — exact, since nothing in a merge adds, removes or re-terminates a byte — and checks `Position` against it afterwards, the same rule `ChunkSpiller` follows.

Because phase two may use the whole budget, phase one's pool has to be unreachable before the executor is constructed. `RunGenerationDriver` keeps the stream, pool, reader and spiller as locals of a method that returns run paths, so they are not hoisted into the state machine of the method that runs both phases, and `Program` forces one collection first: pooled arrays live on the LOH, which only a gen-2 collection reclaims, and "both phases fit inside the same budget" is a claim about the working set, not about reachability.

`PassesExecuted` exists so MP-10 can assert that the passes actually run match the planner's prediction. It is the only member here that exists for a test, and it is what makes the multi-pass claim measurable rather than asserted. MP-10 uses a real temporary directory with kilobyte-sized runs and is the one Part 2 case that touches disk; an interface introduced to avoid that would buy one test double at the cost of the exact pattern the brief punishes.

**`ExecuteAsync` takes one branch before any of the above.** When `MergeParallelism` is above one *and* the planner returned one pass holding one group with nothing carried forward — which by its own construction means that group is every run there is — the call goes to the partitioned merge and returns, having executed one pass. A multi-pass merge would have to partition every group of every pass against intermediate runs that do not exist yet, for a phase whose cost at that point is dominated by the extra passes, so the planner is left untouched and the branch declines the case.

```csharp
internal sealed class MergeProgress
{
    public long   BytesWritten      { get; }   // Volatile.Read
    public double OutputWaitSeconds { get; }

    public void AddBytesWritten(int count);
    public void AddOutputWaitTicks(long ticks);
}
```

### 7.6 OutputVerifier

```csharp
internal static class OutputVerifier
{
    public static Task<VerificationResult> RunAsync(VerifyOptions options, CancellationToken ct);
}

internal sealed record VerifyOptions(string InputPath, string OutputPath, int MaxLineLength);

internal readonly record struct FileScanReport(long LineCount, long ByteCount, ulong Hash);

internal enum VerificationOutcome { Verified, OrderViolation, CountMismatch, HashMismatch, OutputNotTerminated }

internal sealed record VerificationResult(
    VerificationOutcome Outcome, FileScanReport Input, FileScanReport Output, string? FailureDetail,
    long? OrderViolationLineNumber = null);          // non-null exactly on OrderViolation
```

Exists so a result too large for a second full sort to compare against can be checked from the shipped binary, with the same comparator the sort uses rather than a second reading of the format that could drift.

Two fixed buffers of `4 MiB + maxLineLength`, stated directly rather than derived from a `MemoryBudget`-style plan, since verification has nothing to sort and needs only head room to carry one partial line across a fill. Both files are read once, forward, as two tasks awaited together. The output scan requires every adjacent pair non-decreasing: each line is parsed into a `LineDescriptor` exactly as `ChunkReader.Describe` does and compared against the previous line's descriptor, which is kept in **a small array of its own** — that copy is what lets the comparison survive the previous line's window being overwritten by the next fill, the same reason `KWayMerge`'s invariant never lets a comparison read a window mid-refill.

Both scans compute the wrapping sum of FNV-1a 64 over every line's content bytes. The input scan strips a `\r\n` line's `\r` exactly as `LineCursor` strips it for the sort; the output scan does not, because this sorter's output is `\n`-terminated throughout and a `\r` before one there is always content. The two hashes are comparable *because* of that asymmetry rather than in spite of it: a byte the input scan struck as a terminator's own half is exactly the byte the sort carried into the output as content.

**A malformed line names which file it came from.** `MalformedLineException`'s own message says nothing about which of the two files was being read, so `ScanAsync`'s caller wraps it with the file path before `Task.WhenAll` lets either surface.

**`OrderViolationLineNumber` is non-null exactly on `OrderViolation`.** The scan stops at the first violation rather than reading the rest, so `Output.LineCount` and `Output.Hash` cover only the lines checked before that point while `Output.ByteCount` stays genuine (`FileStream.Length`). Printing a partial count the way the ordinary summary line does would present it as final; `VerifyCommand` prints "scan stopped at line N" instead.

**`OutputNotTerminated` is the output scan's own check, distinct from the input scan's bare-CR finalisation.** An input file may arrive without a final terminator, so the input scan finalises the fragment; the output scan finalises nothing, because the sorter always terminates its own last line. Sharing the input's rule would let a truncated output verify anyway: output `"1. a\r"` with no `\n` hashes the same content as input `"1. a\n"` on both sides.

### 7.7 RangePartitioner and the partitioned merge

```csharp
internal static class RangePartitioner
{
    /// Null when there is nothing to partition; the caller then merges sequentially.
    public static RangePartition? Locate(
        IReadOnlyList<string> runPaths, int workerCount, int maxLineLength, int parallelism, CancellationToken ct);
}

internal sealed record RangePartition(
    long[][] RunOffsets,     // [run][worker], with index WorkerCount holding the run's length
    long[]   SliceBytes,     // [worker]
    long[]   OutputOffsets,  // [worker]
    long     TotalBytes)
{
    public int    WorkerCount { get; }
    public double Imbalance   { get; }   // largest slice over smallest

    public static RangePartition From(long[][] runOffsets, long[] runLengths, int workerCount, long totalBytes);
}

internal sealed class RunSliceStream    : Stream { /* read-only view of [start, end) of a run   */ }
internal sealed class OutputSliceStream : Stream { /* write-only view of [start, end) of output */
    public long BytesWritten { get; }
}
```

**Why.** The merge's ~167 ns per output line is per-core miss latency and memory-level parallelism, not DRAM bandwidth — about 1.7 GB/s of traffic against roughly 50 GB/s available — so the cost divides across cores. On the real merge at 20 GiB and a 4 GiB budget: 108.2 s at one worker, 51 s at four, 37–39 s at eight, and a regression at sixteen.

**The partition.** `Locate` runs two steps, both once, before any worker starts.

1. **Sample.** Up to `TargetSampleLines` (4,096) lines are read at byte positions spread evenly across the runs' *concatenated* bytes — the midpoints of equal segments, stratified rather than random, so the result is a function of the inputs alone — parsed, sorted with `LineOrder`, and the `workerCount − 1` quantiles taken as splitter keys. Sampling by *byte position* rather than line index is what makes those quantiles estimate the byte-fraction split the merge wants: each position stands for an equal number of bytes, so a line twice as long is twice as likely to be drawn. **That is why there is no refinement round.** At 4,096 samples the balance measures within about 2–3% on this data, inside a ±10% target; a `--max-line`-derived count (255 at the default) measures 8–13% off, and 32% at `--duplicate-ratio 0.4`, which is why the count is fixed rather than derived.
2. **Locate.** For every run and every splitter, a binary search over byte offsets finds **the first line start whose key is at or above that splitter, under the full three-level order**. That is the one rule, applied once, and the merge never re-derives it. Runs are sorted, so the predicate is monotone in the offset, which is what makes the search valid; splitters are sorted, so each search starts where the previous ended and a run's offsets are monotone by construction rather than by a clamp afterwards.

**Ties.** A run of byte-identical lines lands wholly in the *later* worker, since the first already satisfies "at or above". Which side is unobservable: two lines that compare equal are byte-identical, so the concatenated output is the same either way.

**Allocation.** Nothing here comes out of `MemoryPlan`. What stays bounded is the bytes *retained* from the draws, against `SampleLineByteBudget` (16 MiB), checked as each line is read — a draw that would push the retained total past it is not retained, so the bound is exact.

**Execution.** The output is preallocated to `TotalBytes` once, so no worker ever extends the file and no two race to. Worker *w* opens every run whose slice for *w* is non-empty, wraps each in a `RunSliceStream`, gives each cursor its own buffers, and writes through an `OutputSliceStream` positioned at `OutputOffsets[w]`. Worker 0 stages through the executor's own output buffer and the others allocate theirs, so exactly `MergeParallelism` exist.

**Sparse output.** NTFS keeps a valid data length per file separately from the length `SetLength` sets, and a write starting past VDL is not placed in isolation: the filesystem first zero-fills every byte between VDL and the write offset, synchronously. `SetLength` moves the length at once and leaves VDL at zero, so the highest-offset worker's first write would zero-fill almost the entire file with the others serialising behind it. `SparseFile.TryMarkSparse` calls `FSCTL_SET_SPARSE` before `SetLength`, so NTFS treats the unwritten span as a hole. The sequential path needs none of this, writing from offset 0 forward. Marking is opportunistic and Windows-only; a filesystem or OS that refuses it leaves the merge unchanged and pays the zero-fill. Once every worker has finished and the length checks pass there is no hole left, so `TryClearSparse` runs then — on a fully written file it is a metadata change, where on a file with a hole it would write the hole out. The re-open that needs is the one step on the success path another process can defeat by holding the file open, so it is caught and ignored: the sort has succeeded and the bytes are correct either way. What sparse preallocation costs is layout — 374 extents against 1 for the same writes at 512 MiB, about 44,000 at 60 GiB — which on an NVMe volume is an allocation-table cost, not a seek cost.

**The checks that replace `Position == totalBytes`.** One check cannot see what several writers can get wrong: a worker emitting one line too many would overwrite its neighbour's first line, leaving a file of exactly the right total length that is wrong in two places. So there are three: `OutputSliceStream` throws on any write crossing its own end, at the write that does it; each worker's bytes written must equal its predicted slice length; and their sum must equal the total. `RangePartition.From` checks the partition on the way in — monotone within each run, starting at zero, ending at the run's length, summing to the bytes the runs hold — so a defect there fails where it happened.

**Failure and ownership.** The first worker to fail cancels its siblings, so the rest stop reading gigabytes into an output about to be deleted; the exception handed back is picked out of the aggregate as the first that is not an `OperationCanceledException`, unless the caller's own token was cancelled (D13's rule). Run files are opened `FileShare.Read` here rather than `None`, because every worker opens every run at once. The deletion contract is unchanged and is what that share still protects: `MergeAsync` disposes every stream on both paths, each worker disposes its own output, and no run is deleted until `Task.WhenAll` has observed every worker finish.

**Open handle count.** Every worker opens every run of its group, so the ceiling is `MergeParallelism × MaxMergeFanIn` — up to 16,384 at the shipped 8/2,048 pair — against the sequential path's 2,048. `SafeFileHandle` wraps a kernel handle table entry directly rather than going through the C runtime's descriptor table, so neither is close to a real ceiling on Windows; the 20 GiB run peaked at 1,488.

---

## 8. Startup

### 8.1 MemoryBudget

```csharp
internal static class MemoryBudget
{
    public static MemoryPlan Calculate(
        long budgetBytes, int parallelism, int maxLineLength, int assumedMeanLineLength);
}
```

Everything the program allocates is solved from these four numbers. `Calculate` guarantees:

```
plan.WorstCasePhaseOneBytes <= budgetBytes
plan.WorstCasePhaseTwoBytes <= budgetBytes
plan.ChunkSize              >  maxLineLength + 2
plan.MergeFanIn             >= 2
```

`MemoryBudget` derives both phase-two figures from the budget rather than fixing them: the fan-in climbs to a ceiling of 2,048, and once the budget affords more than that many cursors at the floor window, the window grows instead, up to 4 MiB. A fixed fan-in of 64 and a floor window would leave phase two allocating 5.6 MiB of a 4 GiB budget and needing two passes; deriving both merges the same input in one. Growing the window alone buys fewer, not faster, round trips, because every fill is awaited before the next is issued — which is why each cursor has two windows and `BytesPerRunCursor` charges for both.

**`MergeParallelism` is derived, not configured**, because a free knob could produce an infeasible plan:

```
MergeParallelism = the largest n ≤ min(parallelism, MaxMergeParallelism) whose own solved window
                   still clears maxLineLength + 2, and 1 if none does

room(n)      = budgetBytes − n × OutputBufferSize
               − (n > 1 ? MaxMergeFanIn × (n + 1) × PartitionOffsetEntrySize : 0)
window(n)    = floor((room(n) − n × MaxMergeFanIn × LoserTreeBytesPerFanInSlot) × m
                     / (n × MaxMergeFanIn × (2m + descriptorSize))),   m = assumedMeanLineLength
MergeFanIn   = min(MaxMergeFanIn,
                   floor(room(N) / (N × (bytesPerRunCursor(window(N)) + LoserTreeBytesPerFanInSlot))))
```

Three things follow. The window falls as `1/n`, so `N × MergeFanIn × bytesPerRunCursor` is unchanged by the worker count — **the parallelism is budget-neutral by construction, not by a second inequality something has to keep in step.** The extra `(N − 1) × OutputBufferSize` is subtracted *before* the window is solved, so it shaves the window rather than overshooting. And requiring the *solved* window to clear the floor on its own — rather than clamping it up as the one-worker case may — is exactly the condition "this budget affords `MaxMergeFanIn` cursors for each of these `n` workers": below it the fan-in, not the window, would pay for the extra worker, trading merge passes for parallelism on a run count the plan cannot know. So a budget too small to grow the window past its floor gets one worker and the sequential merge. `MaxMergeParallelism` is 8, from measurement rather than from the arithmetic: the floor clamp alone would admit 10 at a 4 GiB budget, and at 10 the per-worker window sits at the floor that measures 48.8 s against 38 s at 8.

`phaseTwoViable` and `MinimumViableBudget` are both stated at **one** worker — not because no budget near the minimum chooses more (parallelism 48 at `--max-line` 64 gives two workers at its own minimum, which MB-12 walks through longhand), but because `phaseTwoViable` is computed once against the floor window's one-cursor cost before any worker-count decision is made, so neither it nor the chunk-size condition is a function of `MergeParallelism` at all. `MinimumViableBudget`'s reference plan builds the same one-worker figure, so the two stay exact inverses of each other. MB-11 through MB-13 cover the three clamps, the fall back to one worker, and a sweep across the budgets where the worker count climbs.

**One resource is deliberately outside `MemoryPlan` and still a function of configuration alone: the chunk sort's stack.** `ChunkSorter` allocates its counting and cursor arrays with `stackalloc` — 258 and 257 `int`s, about 2 KiB, once per stack frame rather than once per level — precisely so a frame's stack use is not a function of how many depths the split guard walked, and recursion is bounded by the depth cap.

**Two constants here are set by measurement.** `AssumedMeanLineLength` is 32, *below* this data's 37.6-byte mean, so the byte buffer empties before the descriptor array and `ChunkReader`'s ordinary fold runs rather than its rebuild: on the same 20 GiB file, 32 gives 185 ordinary folds and no rebuilds where 40 rebuilds every one of its 178 chunks. `OutputBufferSize` is 1 MiB, set against the merge's own output-wait instrument: against one sequential writer the curve is shallow below 1 MiB, but eight workers writing concurrently into disjoint ranges are a different pattern, and there 1 MiB cuts summed output-wait 39.3% at 20 GiB and 27.2% at 60 GiB. Both constants are read through the four conditions above rather than asserted separately, and both *raise* `MinimumViableBudget` — far enough that `OutputBufferSize` alone exceeds a 1 MiB budget, which is why every MB case near a small-budget boundary writes its figures longhand rather than asserting a round number.

### 8.2 TempCapacity

```csharp
internal static class TempCapacity
{
    /// freeBytes is null when the volume does not report free space.
    public static CapacityDecision Evaluate(long inputSizeBytes, long? freeBytes);
    public static CapacityDecision EvaluateSameVolume(long inputSizeBytes, long? freeBytes);
    public static CapacityDecision EvaluateOutputVolume(long inputSizeBytes, long? freeBytes);
}
```

A pure decision, separate from the free-space probe `CapacityProbe` performs once. The requirement is twice the input size: run files hold a full copy and a merge pass in progress holds part of another before its inputs are deleted. The multiplier is a deliberately conservative upper bound, and `MergeExecutor`'s per-group deletion is what keeps actual usage well inside it.

The nullable `freeBytes` is what makes SC-05 a unit test rather than a branch buried in `CapacityProbe`: a volume that does not report free space yields `Unknown`, and the rule is to proceed with a stated warning. Treating an unreported figure as zero would block every run on such volumes, which is a self-inflicted outage.

### 8.3 TemporaryRunSet

```csharp
internal sealed class TemporaryRunSet : IDisposable
{
    public TemporaryRunSet(string tempDirectory);

    public string CreateRunPath();
    public void   Delete(string runPath);
    public void   Dispose();   // deletes every run file that still exists
}
```

A class rather than a helper method because it is how "temporary files survive neither path" becomes a single `using` in `Program` instead of a comment nobody enforces.

Run files do not live directly in `--temp`. `CreateRunPath` creates a private `sorter-<pid>-<8 hex>` directory beneath it on its first call, and every run path lives inside — so two sorts sharing a `--temp` parent, or one left at its default of the output's own directory, get disjoint namespaces and neither can collide with a file the operator happens to have there, including the output itself. Creation is lazy, on the first run file rather than in the constructor, so nothing of this invocation's namespace touches disk until the capacity precheck has passed; the private directory is always on the same volume as its parent, so that precheck needs no separate probe. Every run file is opened `FileMode.CreateNew`, so a name that should be exclusive to this invocation is never silently overwritten.

`Delete` exists for `MergeExecutor`'s per-group cleanup and deregisters the path. `Dispose` deletes every run file still registered, then the private directory (tolerating either step failing, because the failure path it exists to serve is exactly the one where the directory's state is unknown). `--temp` itself is never removed: two sorts can share one parent, so neither can safely decide it owns it.

**The ownership contract `Delete` depends on:** a run file is never deleted while a handle on it is open. On the sequential path `FileShare.None` makes that true by refusing a second opener. A partitioned merge cannot use `None`, since every worker opens every run at once, so those handles are `FileShare.Read` and the contract is enforced by the code instead: `MergeAsync` disposes every stream on both paths, each worker disposes its own output, and `Delete` is called only after `Task.WhenAll` has observed every worker finish.

---

## 9. The generator

```csharp
internal sealed record GeneratorOptions(
    string OutputPath, long TargetBytes, int Seed, double DuplicateRatio);

internal sealed class LineComposer
{
    public LineComposer(GeneratorOptions options);

    public int MaxComposedLineLength { get; }   // longest vocabulary entry + widest number + terminator

    /// Returns false when the next line would exceed remainingBytes.
    public bool TryComposeNext(Span<byte> destination, long remainingBytes, out int written);
}

internal static class FileWriter
{
    public static long Write(
        Stream output, LineComposer composer, long targetBytes, Action<long>? onProgress = null);
}
```

There is no maximum-line-length option. The generator composes from a fixed `Vocabulary` with canonical decimal numbers, so nothing it can produce approaches 64 KiB and no path in `TryComposeNext` could be driven to violate a limit — a knob that is parsed, stored and never consumed advertises behaviour that does not exist. `onProgress` is how section 10's progress requirement reaches a synchronous write loop (D15).

`TryComposeNext` returning `false` **is** the sizing rule: the output is the largest whole number of complete lines, each terminated, that does not exceed the target. It never overshoots and never truncates a line to hit the target exactly, because a truncated final line would be malformed and the sorter would reject the generator's own output on its last line.

String parts are drawn from `Vocabulary` using a seeded `Random`. Duplicates are guaranteed at two scales, both by construction rather than left to the chance that a random draw repeats. Across a whole file the configured ratio is a measured proportion. A proportion needs enough lines to mean anything, so a positive ratio *additionally and unconditionally* composes the file's first two lines as a forced pair sharing one pool entry, ahead of every probabilistic draw. Two candidate pairs are drawn every time regardless of the target — the pair an ordinary two lines would produce, and a minimal pair from the shortest pool entry and two single-digit numbers — and whichever fits is written, the ordinary pair preferred. The guarantee therefore holds precisely when the target can hold the two shortest lines that can share an entry; below that the file is simply too small to carry it, which is not an error. **Both candidates are drawn from the same seeded `Random` every time regardless of which is used**, so the sequence that follows never depends on the target size. A ratio of exactly zero disables both and stays a distinct-data mode.

Numbers are written in canonical decimal with no leading zeros, so generated data never exercises the third comparison level. That level is reachable only from hand-written fixtures, which is why OC-13 and OC-17 are unit cases and cannot be delegated to the property test.

Composition is separable from writing: `LineComposer` writes into a caller-supplied span and `FileWriter` takes a `Stream`, so sizing and composition are testable against a `MemoryStream`.

**The generator's own output follows the sorter's staging rule.** `Program.WriteToOutput`, not `FileWriter.Write`, opens a uniquely named staging file beside the requested output, writes to it, and moves it into place only once the write returns without error. `CreateStagingPath` and its cleanup are a deliberate duplicate of `Merging/StagingFile`'s — same retry count on a name collision, same report-rather-than-swallow rule — rather than a shared reference, because the two programs share no project. A failure to replace the requested output this way is reported by name at exit 3 rather than left to crash unhandled.

---

## 10. Command-line contract

The only genuinely public API of the deliverable.

```
sorter    <input> <output> [--temp DIR] [--memory 1GiB] [--max-line 64KiB]
                           [--parallelism N] [--pipeline akka|channels]
sorter    --verify <input> <output> [--max-line 64KiB]

generator <output> --size 100GiB [--seed 42] [--duplicate-ratio 0.1]
```

Defaults: `--memory 1GiB`, `--max-line 64KiB`, `--parallelism` = processor count, `--pipeline akka`, `--temp` = the output file's directory, `--seed` = 0, `--duplicate-ratio` = 0.1. Sizes accept `B`, `KiB`, `MiB`, `GiB` suffixes and a bare byte count. Argument parsing is hand written, roughly forty lines, with no `System.CommandLine` dependency.

`--verify` shares the `--max-line` flag but not sort mode's ceiling: sort mode's feeds `MemoryBudget.Calculate` and is capped at `int.MaxValue - 16`, while `--verify`'s feeds `OutputVerifier`'s fixed `BaseBufferSize + maxLineLength` buffer and is capped lower, at `Array.MaxLength - BaseBufferSize`, so a value between the two ceilings is rejected with exit 3 rather than reaching that allocation and crashing with an unhandled `ArgumentOutOfRangeException`.

`--memory` and `--max-line` exist because the test strategy makes them test-facing knobs: lowering the budget forces a multi-pass merge over a few kilobytes, lowering the line limit forces the oversized-line path with a line that fits on one screen. `--pipeline` exists because both strategies ship and the benchmark compares them.

| Code | Program | Meaning |
|---|---|---|
| 0 | both | Success |
| 1 | sorter | Malformed input, with byte offset, line number, and a truncated preview on stderr |
| 2 | sorter | Insufficient temporary space, naming required, available, and the directory examined |
| 3 | both | Invalid arguments; usage printed |
| 4 | sorter | `--verify` found the output out of order, unterminated, or disagreeing with the input's count or hash |
| 130 | sorter | Cancelled by the operator |

The generator has no cancellation code because `FileWriter.Write` is synchronous and takes no token; documenting 130 for a program that cannot return it would be a claim the code does not honour.

Cancellation in the sorter: `Console.CancelKeyPress` cancels the token, every asynchronous method observes it, and D13 guarantees an `OperationCanceledException` reaches `Program` in the same shape under either strategy. `TemporaryRunSet.Dispose` runs on the way out, so cancellation leaves no temporary files — which holds only because a strategy has finished every spill it started by the time it returns (D16). Progress goes to stderr so stdout stays empty on success and the tool composes in a pipeline.

---

## 11. Traceability

| Test strategy unit | Type |
|---|---|
| Unit 1, the line parser | `LineFormat/LineParser.TryParse` |
| Unit 2, the ordering comparator, including OC-13 and OC-17 | `LineFormat/LineOrder.Compare` |
| Unit 3, the line descriptor and chunk model | `LineFormat/LineDescriptor`, `RunGeneration/Chunk` |
| Unit 4, the chunk boundary splitter, including CB-12 and CB-16 | `LineFormat/LineCursor` |
| Unit 5, the in-memory chunk sorter | `RunGeneration/ChunkSorter.Sort` |
| Unit 6, the k-way merge | `Merging/KWayMerge.MergeAsync`, `Merging/RunCursor` |
| Unit 6, the single-run shortcut | `Merging/RunPlacement.Place`, branched in `Program` |
| Unit 6a, the range partition and the merge slices | `Merging/RangePartitioner.Locate`, `RangePartition`, `RunSliceStream`, `OutputSliceStream`, `SparseFile` |
| Unit 7, the multi-pass merge planner | `Merging/MergePlanner.Plan` |
| Unit 7, MP-10, predicted versus actual pass count | `Merging/MergeExecutor.ExecuteAsync`, `PassesExecuted` |
| Unit 8, the buffer pool | `RunGeneration/BufferPool`, `PooledBuffer` |
| Unit 9, the memory budget calculator | `Startup/MemoryBudget.Calculate`, validated through `MemoryPlan` |
| Unit 10, the startup capacity precheck | `Startup/TempCapacity.Evaluate` and its two volume-specific forms |
| Unit 11, the generator's composition and sizing | `Generation/LineComposer.TryComposeNext` |
| Temp file cleanup on both paths | `Startup/TemporaryRunSet` |
| Streaming-layer tier (SL) | `RunGeneration/RunGenerationStrategy`, run twice |
| Property-based tier (PB) | `tests/FileSorter.Tests/Properties/`, over the types above and `Program.RunAsync` |
| Integration and end-to-end tiers (IT, ET) | `tests/FileSorter.Tests/Integration/` and `EndToEnd/` |
| Byte-identity oracle | `tests/FileSorter.Tests/Properties/NaiveReferenceSort`, independently written, must not call `LineOrder` |
| Verify tests (VF) | `Verification/OutputVerifier.RunAsync`, `VerifyCommand.RunAsync` |

---

## 12. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Both run-generation strategies ship, selectable by `--pipeline`, default `akka`; the `ActorSystem` is created only inside the Akka branch | Two real implementations earn the seam, and the benchmark baseline is the benefit not otherwise obtainable. Deleting the flag would leave `ChannelRunGeneration` referenced only from tests, which reads as dead code |
| D2 | Maximum line length is enforced in `LineCursor`, not `LineParser` | The component that must bound its carry-over enforces the bound. CB-12 and CB-16 carry both boundary cases at the splitter |
| D3 | The merge selects through a loser tree private to `KWayMerge`, not a BCL `PriorityQueue` | A 4-ary heap pays a sift-down and a sift-up per output line, each comparison an interface call on a boxed struct; a loser tree pays at most `⌈log2 k⌉` direct comparisons and needs no comparer type at all |
| D4 | Hand-written argument parsing | One fewer dependency for roughly forty lines |
| D5 | `LineDescriptor` stores `StringOffset` and an eight-byte `Prefix` (thirty-two bytes, `Offset` and `Length` packed to keep four fields rather than five); size obtained via `Unsafe.SizeOf` | Keeps the comparison path free of rescanning and lets most comparisons resolve without touching the line buffer; the budget stays correct if the struct changes again |
| D6 | `LineFormat` is a slice | The line grammar is one feature used by every phase, not a technical layer |
| D7 | The benchmark reports Channels as the baseline and Akka.Streams as a percentage against it | A skeptical reviewer reads it in that direction |
| D8 | Run order from a strategy is undefined | Both schedulers complete out of order; `MergeExecutor` treats the list as a set |
| D9 | A pool slot owns both the byte buffer and the descriptor array; a chunk ends when either fills | Otherwise the descriptor array is a per-chunk LOH allocation and peak working set is a function of GC behaviour rather than configuration. Turns `assumedMeanLineLength` from a correctness assumption into a tuning parameter — one whose value decides, per chunk, which of `ChunkReader`'s two carry paths runs |
| D10 | `MergeExecutor` drives the passes and owns intermediate runs; `TemporaryRunSet` lives in `Startup/` and is shared by both phases | Something has to execute a multi-pass plan, and temporary files belong to both phases rather than to phase one alone |
| D11 | The holder releases: `ChunkReader` disposes a slot it never hands on, `ChunkSpiller` disposes every slot it receives | Any other rule leaks a slot when the reader throws on a malformed line, which is a guaranteed path on bad input rather than an edge case |
| D12 | Every READ stream is opened `bufferSize: 1`; WRITE streams too, with a budgeted staging array (`SpillBufferSize`, `OutputBufferSize`) coalescing each line and its terminator instead. Phase two's buffers are allocated once in `MergeExecutor` and passed in | A `FileStream` buffer under an existing read-ahead buffer is unbudgeted work done twice. That argument is read-only: nothing buffers a write the way a read-ahead buffer buffers a read, so an unbuffered write stream alone would cost two syscalls per line. The staging buffer does that coalescing instead, which is what lets both sides stay unbuffered |
| D13 | A strategy surfaces the first branch failure as the original exception, unwrapped; the streaming tier asserts exception-type identity across both | Otherwise `Parallel.ForEachAsync`'s `AggregateException` and Akka's fault route give the same corrupt file different exit codes. `await` unwraps the Channels path for free; it is Akka's materialized task that needs the explicit peel |
| D14 | `Program` owns the single-run branch; `MergeExecutor` takes no `tryMove` | A parameter threaded two levels for one branch's benefit, and MP-05 already describes the single run as bypassing the merge entirely |
| D15 | `FileWriter.Write` takes an optional `Action<long>? onProgress` | Progress on stderr is required for both programs, and with the cancellation token correctly absent this is the only sanctioned seam left. A hundred gigabytes is several silent minutes otherwise |
| D16 | A strategy returns only once every read and every spill it started has finished, on every path; `AkkaRunGeneration` joins them behind a linked `CancellationTokenSource`, holding one `Task` field for the read and a count for the spills | `Parallel.ForEachAsync` gives `ChannelRunGeneration` this free, while Akka's materialized task completes at the fault with sibling spills still running — and the caller disposes the reader, the pool and the run registry the moment a strategy returns |

Deliberately not done: `ChunkReader` is not split, because what remains after it delegates grammar, terminators and allocation is one job. No interface is introduced for `MergeExecutor`; MP-10 uses a real temporary directory instead. The chunk sort's `IComparer<T>` adapter is not shared with the merge, whose loser tree has nothing to hand an adapter to. The line grammar stays duplicated across the two programs rather than sharing a project, with GN-09 as the test that keeps them honest.

---

## 13. What this specification does not cover

Test case tables, which live in the test strategy. The README's content and structure. Benchmark methodology beyond the baseline choice in D7. The lenient malformed-line side channel, which is named in the README as a natural extension and is deliberately not built.
