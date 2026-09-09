# Test Strategy and Test-Driven Specification

Scope: two console programs. A generator that writes a text file of a caller-specified size in bytes, with a controllable number of lines sharing the same string part, driven by a seeded random source. A sorter that orders such a file, primarily by the string part ascending, secondarily by the number ascending, and that must remain correct and bounded at roughly one hundred gigabytes.

Line format: a number, then a period, then a space, then a free-form string. Valid examples include "415. Apple", "30432. Something something something", "1. Apple", "2. Banana is yellow".

Target design, which this specification tests and does not relitigate: classic external merge sort; run generation into spilled sorted temp files; k-way merge through a loser tree, with multi-pass grouping when the run count exceeds the fan-in; pooled raw byte buffers with line-descriptor value types instead of materialised strings; ordinal byte-wise comparison over UTF-8; a fixed buffer pool guarded by a counting limit; a reactive streaming orchestration shell over phase one only; a single-pass phase two range-partitioned across independent merge workers, with asynchronous read-ahead per run; vertical slice organisation.

### Settled behavioural contract

These decisions are locked. Every test case below assumes them, and the readme must state them in the same words.

| Area | Rule |
|---|---|
| Separator | The first period in the line, not the first period-then-space pair. One space immediately following that period is consumed if present; if there is no space, the string part begins at the next byte. |
| Number | A signed 64-bit integer, full range, no carve-outs. Negative values are accepted and order naturally ascending. A value outside the representable range is malformed. Leading zeros are accepted. |
| Ordering | Three levels. String part ascending, ordinal over raw bytes. Then number ascending. Then the raw line bytes ascending, as a final deterministic tie-break. |
| Case | Ordinal and case-sensitive, so uppercase orders before lowercase. |
| Malformed input | Fail fast on the first malformed line, reporting byte offset, line number, and the offending bytes truncated to a readable length of 128 bytes. |
| Maximum line length | A documented, configurable limit, defaulting to 64 KiB. A longer line is malformed. |
| Encoding | Never validated. Input is assumed UTF-8; the string part is compared as opaque bytes. Invalid sequences pass through and order by their byte values. |
| Input terminators | Both the single-character and the two-character convention are accepted. Exactly one carriage return immediately preceding the line feed is stripped. |
| Output terminators | Always the single-character terminator, so output bytes are deterministic regardless of input convention. Every emitted line is terminated, including the last. |
| Trailing region | The empty region after the file's final terminator is not a line. |
| Empty string part | Valid, and orders before every non-empty string part. |
| Empty input | Valid. Produces empty output and a success exit status, not an error. |
| Startup capacity check | Free temporary space of roughly twice the input size is verified before any work begins; a shortfall fails immediately with a clear message. |
| Test-facing knobs | Maximum line length and total memory budget are configurable, specifically so tests can force paths that would otherwise appear only at scale. |
| Sort stability | Not required and provably moot, because the third comparison level makes equality mean byte-identical. No test asserts it. |
| Single run | When run generation produces exactly one run, that run is moved to the output path rather than read and rewritten. A move that fails because the temporary directory and the output path are on different volumes falls back to a copy. The temporary file survives neither path. |
| Buffer hygiene | Buffers are not cleared on release. Consumers respect the recorded length, never the capacity, so residual bytes beyond the recorded length are never read. |
| Generator sizing | Output is the largest whole number of lines not exceeding the requested byte size. Never overshoot, never truncate mid-line. |

A lenient mode diverting malformed lines to a side channel with a reported count is named in the readme as a natural extension and is deliberately not built.

Part 2 covers the unit tests. The property-based, integration and streaming-layer tiers follow it in "Cross-slice tiers"; the benchmark tier is specified last.

---

## Part 1: Test strategy

### The pyramid for this project

| Tier | Proves | Cases | Cost |
|---|---|---|---|
| Unit tests against the algorithmic core (LP, OC, CM, CB, CS, KM, RP, RC, OS, SF, MP, BP, MB, SC, GN, GW) | All correctness of parsing, ordering, chunking, sorting, merging, placement, planning, pooling, budgeting, capacity checking, and generation | 200, most of them table rows over a handful of parameterised methods | Milliseconds; runs on every save |
| Property-based tests (PB) | That the composed pipeline agrees with a naive reference over randomly generated inputs, and that structural invariants survive input shapes nobody thought to enumerate | 19 | Seconds |
| Integration tests over the real file system (IT) | The line-format shapes the unit tier can only reach in memory, plus the two cases that need a real environment: the capacity precheck against an actual volume, and single-run placement across two actually different volumes | 8 | Seconds |
| End-to-end and verify tests (ET, VF) | Whole runs through `Program.RunAsync` and `VerifyCommand.RunAsync`: budgets and merge shapes, destination failures, temp cleanup, and `--verify`'s own outcomes | 15 and 12 | Seconds to a minute |
| Startup ownership tests (TR) | That a run's temporary files live in a private per-invocation directory which does not outlive the run | 4 | Milliseconds |
| Streaming-layer tests (SL) | Flow control, backpressure, deterministic resource release, error propagation across the fan-out | 5 | Seconds |
| Benchmarks and manual verification | Throughput, allocation profile, real large-file behaviour | A handful, run on demand, never in the gate | Minutes to hours |

The shape is deliberate: the tier that proves the program is right is the cheapest tier, because the design put every correctness-bearing decision behind a plain, dependency-free seam.

**These counts are exact rather than estimates, and they are meant to be checked rather than trusted.** Every case in this document carries a `[Trait("Case", "...")]` in its test, so the identifiers here and the traits in the suite are two sets a reviewer can diff mechanically instead of comparing by eye — which is the only thing that keeps a document this size from drifting quietly away from the code it describes. The two sets agree exactly today, at 263 specified cases, with two deliberate exceptions recorded in place: PB-07 and GN-13, the only identifiers in this document with no test behind them, each carrying its reason in its own row. A case count is not a test-method count; several cases are parameterised methods covering many rows at once, so the suite runs substantially more methods than there are cases here.

### The central insight

Sorting correctness is a pure function question — given a sequence of bytes, which line comes first, and does the merge preserve that order globally. None of that involves the streaming library, the file system, threads, or asynchrony, and every one of parsing, comparison, chunk sorting, run spilling, k-way merging, buffer pooling and budget arithmetic is reachable from a plain unit test with no test doubles beyond in-memory byte sequences.

That leaves the streaming layer a smaller and more honest job. It does not need to prove the output is sorted. It needs to prove three things: that chunks flow through the fan-out without unbounded accumulation, that every acquired buffer is released exactly once on every path including the failure path, and that a fault in one branch surfaces as a fault of the whole operation rather than a silent truncation. A reviewer reading the test project should be able to tell within thirty seconds which tests guard correctness and which guard plumbing.

### The single highest-value test

Generate a random input file, run the complete pipeline over it, and assert the output is byte-identical to a naive reference sort that reads every line into memory, orders them with the same key, and writes them back. It outweighs the rest of the suite for four reasons:

- **It tests the composition, not the parts.** Per-method tests prove each unit behaves as its author imagined; they cannot prove the units agree with each other about, say, whether a stray carriage return belongs to the line before it. Defects in external merge sort concentrate exactly at those seams — the last line of a chunk, the last line of a run, the boundary between passes.
- **It generates cases nobody would write by hand.** Over enough runs, random inputs with a controlled duplicate ratio produce equal-head ties across three runs at once, a run that empties on its first pop, a chunk boundary landing precisely on a terminator, and a final line without a terminator, all in combination.
- **It has a total oracle.** A partial oracle such as "the output is non-decreasing" accepts a sorter that drops lines. Byte identity rejects everything: wrong order, dropped lines, duplicated lines, mangled bytes, wrong terminators, a missing or extra trailing newline.
- **Its failures shrink to something readable**, reducing a failing thousand-line input to the two or three lines that actually disagree.

**What makes the oracle trustworthy** is that it must not share code with the implementation. Sharing the production comparator would make the test tautological: a comparator with a case-sensitivity bug would produce identical wrong output on both sides. The reference therefore derives its key by a separately written, deliberately naive route, and three settled rules must be mirrored exactly in it or the test is meaningless rather than merely weak — ordinal case-sensitive comparison, the third-level tie-break on raw line bytes, and the single-character output terminator with every line terminated.

### Determinism, and why there is a third comparison level

Two determinism properties are asserted rather than assumed. **Generator determinism:** the same seed and size target produce a byte-identical file, which is what makes every downstream test reproducible and every bug report actionable. It is easy to break accidentally — seeding from a clock, iterating a hash-ordered collection, drawing random values from several threads without partitioning the stream. **Sorter determinism:** the same input produces a byte-identical output across repeated runs, degrees of parallelism, and memory budgets.

The third comparison level exists to make that last clause true. Leading zeros are accepted, so two lines can have identical string parts and numerically equal but textually different numbers; they tie on both stated keys, and their relative order would then be decided by which chunk each landed in — and chunk boundaries are a function of the memory budget. The same input under two budgets would produce different bytes, and the byte-identity oracle would fail intermittently for a reason that has nothing to do with sorting. The assignment specifies no order for such lines, so any deterministic rule satisfies it, and a deterministic one is strictly more useful.

Asserting output equality across two parallelism settings and two budgets is the cheapest race detector and the cheapest ordering-ambiguity detector in the suite.

### Demonstrating the hundred-gigabyte claim without a hundred gigabytes

The claim under test is not "we ran a hundred gigabytes" but the stronger and cheaper one: peak working set is a function of configuration alone and independent of input size. Once that holds, capacity follows from arithmetic plus available disk, and running the real thing is a confirmation rather than a proof.

**The maximum line length is load-bearing for this claim, not a convenience.** A bounded line length guarantees every line fits inside a chunk, which bounds the carry a line can leave for the next read — the last piece of state in the read path whose size would otherwise be a function of the input. Without it, one hostile line converts a memory guarantee into a hope.

Two knobs are configurable specifically to make expensive paths cheap to reach: lowering the budget forces a multi-pass merge over a few kilobytes, and lowering the line limit forces the oversized-line path with a line that fits on one screen. Three tests then establish the claim:

1. **Flat working set across growing inputs at a fixed budget** — inputs spanning at least two orders of magnitude, asserting the largest peak does not exceed the smallest by more than a stated tolerance. The assertion is on *flatness*, not an absolute number: a design that quietly accumulates per-line state shows up as a slope. Inherently noisy, so it uses a generous tolerance and is tagged out of the default gate.
2. **A forced multi-pass merge under a deliberately tiny budget**, asserting both that the output is correct and that the pass count matches the planner's prediction — the code path that only exists for very large inputs, at a scale that runs in seconds.
3. **Bounded buffer occupancy**, asserting directly that outstanding buffers never exceed the ceiling and that the budget arithmetic never yields a plan whose worst case exceeds the budget. This is the unit test that makes the other two interpretable rather than anecdotal.

### Why fail fast is the right policy, and what it costs

A correct sorted output of a file that could not be fully read is not possible, and a wrong answer that looks well formed is worse than a loud failure, because nothing downstream will question it. The cost is real and stated rather than hidden: one bad byte late in a very large file discards the work done so far.

Fail fast raises the stakes on two boundary rules, which is why both get prominent tests. If the trailing empty region after the final terminator were treated as a line, every well-formed file would appear to contain one malformed line and would fail under this policy. And if a stray carriage return were not stripped, no error would be raised at all — the byte would silently join the sort key and the output would be wrong in a way that looks exactly like a sorting bug. Case sensitivity deserves the same treatment for the opposite reason: the assignment's examples do not disambiguate it, so the settled answer must be stated and the oracle must mirror it.

### What is not unit-testable

Naming this is a quality signal, not an admission. Pretending these are covered is how a suite reaches high coverage numbers while retiring no risk.

- **Actual throughput on real hardware.** A timing assertion in a unit test is a flaky test wearing a performance costume. Covered by benchmarks and a recorded manual run.
- **True out-of-memory and disk-full behaviour.** The startup capacity check is pure arithmetic and is unit tested; the internal handling of a write failure is exercised with an injected failing sink. Real exhaustion partway through a run, caused by another process, is a manual check.
- **Operating system and file system interactions.** Temp placement, cleanup after abrupt termination, and a locked or read-only output path are integration or manual concerns. The one cross-volume behaviour that matters — the move-to-copy fallback — is pulled behind a seam so the decision and the cleanup guarantee are unit tested.
- **Thread interleaving in the fan-out.** Concurrency tests demonstrate the absence of a defect only for the schedules that happened to occur. The mitigation is structural: phase one's shared mutable state is confined to the pool, which is tested directly for its invariants, and phase two's merge workers share nothing but a progress counter of two interlocked adds.
- **Genuine hundred-gigabyte behaviour.** Long-tail effects at real scale are only observable at real scale. The strategy above reduces this to a confirmation step, and the residual risk is listed in Part 3 rather than hidden.

### Naming and organisation under vertical slice architecture

The test project mirrors the production structure feature for feature: no folder named after a technical layer, none called "unit tests", none called "services" or "infrastructure", because those words do not appear on the production side either. One test class per unit under test, named in the same words the production code uses. Cross-slice tests live in their own top-level area named for what they exercise, not for their tier.

Test method names are sentences describing behaviour in three parts: the unit's action, the condition, and the expected result. The condition clause is mandatory even when there is only one case, because it is what makes a failing test name self-explaining in a build log.

Every case in Part 2 carries a stable identifier that appears in the test as a trait, so a reviewer can trace a table row to a running test and back. Identifiers are never reused after a case is deleted.

---

## Part 2: Unit test specification

Conventions used in every table below. The identifier is stable and appears in the test. Input describes the precondition, not the mechanics of arranging it. Expected outcome is an observable result, never an internal state assertion. A case that can catch no defect does not belong in the table; where the defect a case exists to catch is not obvious from its name, the note under the table says so.

---

### Unit 1: The line parser

**Responsibility.** Given the raw bytes of a single line, with terminators already removed by the splitter, identify the boundary between the number part and the string part, produce the parsed number, and produce the extent of the string part as an offset and length within the original bytes. It does no decoding, no allocation, and no encoding validation. It is the only place in the system that knows the line grammar.

The boundary is the first period in the line. The number is a signed 64-bit integer and therefore cannot contain a period, so the first period is an unambiguous boundary and no lookahead for a following space is needed to find it. One space immediately following that period is consumed if present; if there is no space, the string part begins at the next byte. This rule is simpler than searching for a period-then-space pair, it cannot be confused by a period inside the string part, and it correctly parses a line whose space after the period is missing.

**Behaviours that must hold.** The string part is everything after the separator, taken verbatim including interior periods, spaces, and non-ASCII or invalid bytes. The parser never copies the string part and never decodes it. The number part must be a valid signed 64-bit integer, optionally signed, with leading zeros permitted; anything else is malformed. A malformed line stops the run immediately with a diagnostic naming the byte offset, the line number, and the offending bytes truncated to a readable length. The maximum line length, defaulting to 64 KiB, is enforced by the chunk boundary splitter in Unit 4 rather than here, because that unit is the one whose carry-over the limit bounds; the parser therefore never sees an over-long line and owns no length logic.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| LP-01 | Parses a well formed line into number and string extent | "415. Apple" | Number is 415; string extent covers exactly "Apple" |
| LP-02 | Splits on the first period when the string part contains a period and a space | "1. Apple. Banana is yellow" | Number is 1; string extent covers "Apple. Banana is yellow" |
| LP-03 | Parses a line with no space after the period | "7.Apple" | Number is 7; string extent covers "Apple" |
| LP-04 | Parses a line whose string part begins with a period | "7. . leading dot" | Number is 7; string extent covers ". leading dot" |
| LP-05 | Consumes exactly one space after the period | "3.  two spaces before the text" with two spaces following the period | String extent begins with the second space and preserves it |
| LP-06 | Rejects a line with no period | "415 Apple" | Malformed; run stops with offset, line number, and truncated bytes |
| LP-07 | Rejects a line whose prefix is not numeric | "abc. Apple" | Malformed |
| LP-08 | Rejects a line whose prefix is partly numeric | "12x. Apple" | Malformed |
| LP-09 | Accepts an empty string part | "42. " with nothing after the space | Number is 42; string extent is empty |
| LP-10 | Accepts a line ending immediately after the period | "88." with no space and nothing after | Number is 88; string extent is empty |
| LP-11 | Accepts leading zeros and reports the numeric value | "007. Apple" | Number is 7; string extent covers "Apple"; the raw bytes remain available for the third-level tie-break |
| LP-12 | Parses the extreme representable values at both ends | The largest and the smallest representable signed 64-bit values, each as the number part | Both parse to the exact value |
| LP-13 | Rejects values one step beyond each end | The largest value plus one and the smallest value minus one, written out in full | Both malformed; never a wrapped or truncated value |
| LP-14 | Accepts a negative number | "-5. Apple" | Number is -5; string extent covers "Apple" |
| LP-15 | Rejects a lone sign with no digits | "-. Apple" | Malformed |
| LP-16 | Accepts zero as the number | "0. Apple" | Number is 0 |
| LP-17 | Preserves whitespace inside and at the end of the string part | "3. spaced out   " with trailing spaces | String extent includes every trailing space verbatim |
| LP-18 | Rejects a line that is only a period | "." and nothing else | Malformed, because the number part is empty |
| LP-19 | Rejects a completely empty line | Zero bytes | Malformed |
| LP-20 | Rejects a line of digits with no period | "12345" | Malformed |
| LP-21 | Preserves multi-byte content undecoded | A line whose string part contains non-ASCII characters | String extent covers the exact bytes, unchanged |
| LP-22 | Passes invalid byte sequences through without validating | A line whose string part contains bytes that are not valid UTF-8 | Parses successfully; string extent covers the exact bytes; no failure and no substitution |
| LP-25 | Produces an actionable diagnostic for a malformed line | A malformed line at a known byte offset well into a multi-line input, with an offending line far longer than the preview limit | The report names the byte offset, the line number, and the offending bytes truncated to the documented 128 bytes |

The two maximum-line-length boundary cases belong to the splitter rather than to the parser, under D2: they are CB-16 and CB-12 in Unit 4. The identifiers LP-23 and LP-24 belong to no case and are not reused.

LP-25 lives in `ChunkReaderTests` rather than `LineParserTests`, for the same kind of reason and worth stating rather than leaving as an oddity. `LineParser` reports only *that* a line is malformed; it is handed one line's bytes and knows neither where that line sat in the file nor which line it was, so the diagnostic named in the contract table is assembled by whichever reader was holding the offset -- `ChunkReader` on the input, `RunCursor` and `RangePartitioner` on run files, `OutputVerifier` on the output. That spread is itself what the case is for: each of those five carries its own `PreviewMaxBytes` constant, so the 128-byte truncation is a rule stated in five places and, until LP-25, asserted in none. An untruncated preview is not a cosmetic defect under fail-fast -- one hostile line turns the error message into 64 KiB of stderr.

---

### Unit 2: The ordering comparator

**Responsibility.** Given two line descriptors and the buffers they point into, return their relative order under three levels: string part ascending compared ordinally over raw bytes, then number ascending, then the raw line bytes ascending. It never decodes and never consults culture. It is a pure function with no allocation.

**Behaviours that must hold.** Byte-wise ordinal comparison over well-formed UTF-8 is equivalent to comparing code point sequences, which is why the string part is never decoded on the hot path. That equivalence is a load-bearing claim of the design and gets a direct test. Comparison is case-sensitive, so uppercase orders before lowercase. Shorter strings that are prefixes of longer ones order first. An empty string part orders before every non-empty one. The number is consulted only when the string parts are byte-identical, and the raw line bytes only when the numbers are also equal. Two lines compare equal only when their raw bytes are identical. The comparator is antisymmetric, transitive, and total.

**Why the third level exists.** Leading zeros are accepted, so two lines can have identical string parts and numerically equal numbers while differing in their bytes. The number is also optionally signed, which produces the same tie independently: "+5" and "5" are numerically equal and textually different, so the third level would be required even by a parser that rejected leading zeros. It is a property of the number grammar, not a patch for one accepted spelling. Without a third level they compare equal, and their output order is then decided by chunk boundaries, which are decided by the memory budget. The same input under two budgets would produce different output bytes and the byte-identity oracle would fail for a reason unrelated to sorting. The raw-bytes tie-break costs nothing on the hot path because it is reached only when both stated keys are exhausted, and the assignment specifies no order for such lines, so any deterministic rule satisfies it.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| OC-01 | Orders by string part ascending | "9. Apple" against "1. Banana is yellow" | "Apple" line orders first, despite its larger number |
| OC-02 | Breaks ties by number ascending when string parts are identical | "30. Apple" against "4. Apple" | The line numbered 4 orders first |
| OC-03 | Reports equality only for byte-identical lines | Two lines with the same number and the same string bytes | Comparison result is equal in both directions |
| OC-04 | Orders a strict prefix before its extension | "1. Ban" against "1. Banana" | "Ban" orders first |
| OC-05 | Orders an empty string part before any non-empty one | "5. " against "5. A" | The empty string part orders first |
| OC-06 | Orders uppercase before lowercase | "1. apple" against "1. Apple" | The uppercase form orders first |
| OC-07 | Orders multi-byte content by code point | String parts containing non-ASCII characters whose code point order is known and differs from any naive per-character ordering | Order matches code point order |
| OC-08 | Orders a multi-byte character after every ASCII character | An ASCII string part against one starting with a non-ASCII character | The ASCII line orders first |
| OC-09 | Is antisymmetric over a representative sample | Every ordered pair from a curated set covering all cases above | If the first orders before the second, the second orders after the first, and equality is symmetric |
| OC-10 | Is transitive over a representative sample | Every ordered triple from the same curated set | If the first orders before the second and the second before the third, the first orders before the third |
| OC-11 | Is total over a representative sample | Every ordered pair from the same set | Exactly one of before, after, or equal holds for every pair, with no undefined result |
| OC-12 | Produces the same order regardless of buffer placement | The same two logical lines at different offsets within different buffers | Identical result |
| OC-13 | Breaks a leading-zero tie on raw line bytes | "007. Apple" against "7. Apple" | A strict, non-equal order, decided by the raw bytes |
| OC-14 | Produces the same order for a tied pair regardless of presentation order | The same leading-zero pair compared in both argument orders, and again after being placed in different buffers | The same line wins every time |
| OC-15 | Orders negative numbers ascending below positives | "-5. Apple", "0. Apple", and "5. Apple" | Ascending order is minus five, zero, five |
| OC-17 | Breaks an explicit-sign tie on raw line bytes | "+5. Apple" against "5. Apple" | A strict, non-equal order, decided by the raw bytes |
| OC-16 | Orders invalid byte sequences by byte value without failing | Two string parts containing invalid UTF-8 sequences that differ in a known byte | Ordered by byte value; no failure, no substitution |
| OC-18 | Orders a strict prefix before an extension that shares its whole cached prefix | "1. Ban" against "1. Bananarama", where the extension is long enough that its cached prefix holds eight real bytes | "Ban" orders first |
| OC-19 | Orders by a 0x00 byte that falls inside the cached prefix | Two string parts identical up to a byte within the first eight bytes, where one has 0x00 there and the other 0x01 | Ordered by that byte's value |
| OC-20 | Falls through past an identical eight-byte cached prefix to the difference beyond it | "1. AAAAAAAAX" against "1. AAAAAAAAY", sharing the eight-byte prefix "AAAAAAAA" exactly | Ordered by the ninth byte |
| OC-21 | Orders an empty string part before one that begins with a real 0x00 byte | "1. " against a string part whose first byte is 0x00 | The empty one orders first |
| OC-22 | Falls through a padding/real-zero tie to order the shorter string first, including at the eight/nine-byte boundary | "1. ab" against "1. ab\0", and against "1. ab\0c"; separately, a string part of exactly eight bytes against its own nine-byte extension | The shorter (or the strict prefix) orders first in every pair |

The three ordering-law cases, OC-09 through OC-11, are the tests most often skipped and the ones most likely to reveal a real defect. They are cheap: one curated set of roughly a dozen lines covering ASCII, non-ASCII, invalid bytes, empty, prefix, case, negative, leading-zero, and tie-break variants, driven through three nested loops. Run them as a single parameterised set, and reuse the same set for the chunk sorter.

---

### Unit 3: The line descriptor and chunk model

**Responsibility.** Represent a chunk as one large raw byte buffer plus a parallel array of small line-descriptor values, each recording a line's offset into the buffer, its length, and its parsed number. Sorting reorders the descriptor array only. The buffer is never rearranged, never copied per line, and never converted to text on the hot path.

**Behaviours that must hold.** Every descriptor's offset and length must address exactly the bytes of its line, exclusive of any terminator and exclusive of a stripped carriage return, so that reconstructing the chunk in descriptor order and appending single-character terminators reproduces a permutation of the original lines. The extent is also what the third comparison level reads, so it must be exact for ordering and not merely for output. The descriptor is a value type small enough that an array of them is a contiguous block, and it holds no reference to the buffer, which lives once per chunk.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| CM-01 | Records exact offset and length for every line | A chunk of several lines of differing lengths | Each descriptor's extent reproduces its line exactly, with no terminator bytes included and none omitted |
| CM-02 | Leaves the underlying bytes untouched after sorting | A chunk in reverse order, sorted | The buffer's bytes are bit-identical to before the sort; only the descriptor array has changed |
| CM-03 | Handles the first line of a chunk | A chunk whose first line is the sort's minimum and again where it is the maximum | Correct extent and correct final position in both cases |
| CM-04 | Handles the last line of a chunk | A chunk whose final line has no trailing terminator, and again where it has one | Correct extent in both cases |
| CM-05 | Handles a chunk of exactly one line | One line | One descriptor, correct extent, sorting is a no-op that does not throw |
| CM-06 | Handles an empty chunk | Zero bytes, zero lines | Zero descriptors, sorting and spilling both succeed as no-ops |
| CM-07 | Carries the parsed number alongside the extent | Lines with numbers spanning the full signed 64-bit range, including negatives | Each descriptor's number matches the parser's result exactly |
| CM-08 | Reconstructs the chunk in descriptor order | A sorted descriptor array written back out | The written bytes are a permutation of the input lines with no loss and no duplication |
| CM-09 | Emits single-character terminators regardless of input convention | A chunk built from input using the two-character convention, and a mixed chunk | Every written line, including the last, is followed by exactly one single-character terminator; no carriage returns appear in the output |

---

### Unit 4: The chunk boundary splitter

**Responsibility.** Given a block of bytes just read from the input and any bytes carried over from the previous read, determine where each complete line ends, strip terminators, and determine which trailing bytes form a partial line that must be carried into the next read. It guarantees no line is ever split across chunks. It decides nothing about sorting and touches no file.

This unit's logic must be separable from actual file input and output. It operates on a supplied block of bytes and a supplied carry-over, and returns line boundaries plus a new carry-over. That separation is the reason it can be tested exhaustively in memory, and it is the single most valuable seam in the sorter, because boundary handling is where external sorts most often break and where a file-coupled implementation is most painful to test.

**Behaviours that must hold.** A line's bytes plus its terminator are consumed exactly once. Both terminator conventions are accepted, including mixed within one file, and exactly one carriage return immediately preceding the line feed is stripped. A two-character terminator split across a read boundary is not mistaken for a line ending followed by an empty line. The final line of a file is emitted even without a trailing terminator. The empty region after a final terminator is not a line. Any leading byte order mark is consumed before the first line is emitted. The carry-over never exceeds the configured maximum line length plus one byte -- the one byte being a carriage return that may be the first half of a `\r\n` whose line feed has not arrived yet, which the limit does not count -- and that is what bounds it. This unit, and not the parser, is where the maximum line length is enforced: the limit exists to bound this unit's carry-over, so enforcing it here makes the memory guarantee structural and leaves the parser with no length logic at all.

**Two cases carry more weight than the rest of this table.**

CB-05, the trailing region after the final terminator. If that region is treated as a line, it is an empty line, an empty line is malformed, and under the fail-fast policy every well-formed file that ends with a terminator fails on its last byte. This is the single most direct interaction between the terminator rule and the malformed-input policy, and getting it wrong makes the program reject essentially all valid input.

CB-07 and CB-14, the stray carriage return. This is the sneakiest defect in the assignment. If the carriage return is not stripped, nothing raises an error. The byte silently becomes the last byte of the string part, it changes the sort key for every line in the file, and the output is wrong in a way that looks exactly like a sorting bug rather than a parsing one. It will not be caught by any test that only checks that output is non-decreasing, because the output is non-decreasing under the corrupted key.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| CB-01 | Splits cleanly when the read ends exactly on a terminator | A block ending immediately after a terminator | All lines emitted; carry-over is empty |
| CB-02 | Carries a partial trailing line into the next read | A block ending mid-line | Complete lines emitted; the partial tail returned as carry-over and emitted as the first line of the next block, joined correctly |
| CB-03 | Carries a partial line across more than two reads | A line longer than one read block, spanning three or more blocks, but within the maximum line length | The line is emitted once, intact, after the final block containing it; the carry-over never exceeds the maximum line length |
| CB-04 | Emits the final line when the file has no trailing terminator | A final block whose last line ends at end of input | The final line is emitted, with no phantom empty line after it |
| CB-05 | Treats the region after the final terminator as nothing, not as a line | A file ending with a terminator | No line is emitted for the trailing region; no malformed line is reported; the run succeeds |
| CB-06 | Recognises the single-character terminator | A block using only the single-character terminator | Correct line boundaries; no terminator bytes in any extent |
| CB-07 | Strips exactly one carriage return before the line feed | A block using the two-character convention throughout | No carriage return appears in any line extent; no empty lines are produced |
| CB-08 | Handles a two-character terminator split across a read boundary | A block ending on the carriage return, next block beginning with the line feed | One line boundary, not two; no empty line emitted; no stray carriage return in either extent |
| CB-09 | Handles mixed terminators within one file | A block mixing both conventions | Every line boundary correct regardless of style; no carriage returns survive into any extent |
| CB-10 | Consumes a leading byte order mark | A file beginning with a byte order mark | The mark is not part of the first line's number or string part; the first line parses and compares as if it were absent |
| CB-11 | Handles a byte order mark as the only content before a terminator | A file with a mark followed immediately by a terminator | No malformed line is produced from the mark alone |
| CB-12 | Rejects a line beyond the maximum length | A line one byte longer than the configured limit, spanning several read blocks | Malformed, reported at the limit rather than after unbounded accumulation; the carry buffer never grows past the limit |
| CB-13 | Handles empty input | Zero bytes | No lines emitted; empty carry-over; the run succeeds with empty output |
| CB-14 | Strips only one carriage return, not a run of them | A line ending with two carriage returns followed by a line feed | Exactly one carriage return is stripped; the other remains as the final byte of the string part |
| CB-15 | Treats a lone carriage return as ordinary content | A carriage return in the middle of a line with no following line feed | Not a line boundary; the byte remains in the extent |
| CB-16 | Accepts a line one byte inside the maximum length | A line one byte shorter than the configured limit, spanning several read blocks | Emitted normally, with its extent covering the whole line |
| CB-17 | Carries, rather than rejects, a fill boundary landing on the CR of a maximum-length `\r\n` line | A block ending exactly on the CR of a line whose content is exactly the configured limit, with the LF not yet read | Not malformed: the block is carried whole (content plus the trailing CR) into the next read |
| CB-18 | Rejects a `\r\n` line whose content already exceeds the limit, once terminated | A line one byte longer than the configured limit, ending `\r\n`, presented whole (LF included) | Malformed, at the line's start offset |
| CB-19 | Disables carriage-return stripping and keeps it as content | A block using the two-character convention throughout, read with `stripCarriageReturn: false` | Every line's extent includes its own trailing carriage return; no bytes are stripped |

---

### Unit 5: The in-memory chunk sorter

**Responsibility.** Reorder a chunk's descriptor array so that reading descriptors in index order yields lines in sorted order under the comparator. It allocates nothing beyond whatever the chosen sort requires, moves no line bytes, and returns nothing beyond the permuted array.

**Behaviours that must hold.** The output is a permutation of the input: same count, same multiset of descriptors, none invented, none lost. The result is non-decreasing under the comparator. Sorting an already-sorted chunk changes nothing observable.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| CS-01 | Leaves an already sorted chunk in order | A chunk already in sorted order | Identical descriptor order; result is a permutation with the same count |
| CS-02 | Sorts a reverse-ordered chunk | A chunk in strictly descending order | Fully ascending order |
| CS-03 | Handles a chunk where every line is identical | Many byte-identical lines | Sorted result is a permutation with unchanged content; no failure |
| CS-04 | Handles a chunk where every string part is identical but numbers differ | Same string part throughout, numbers shuffled, including negatives and leading-zero forms | Ascending by number, with leading-zero ties resolved by raw bytes |
| CS-05 | Handles a single-line chunk | One descriptor | Unchanged; no failure |
| CS-06 | Handles an empty chunk | Zero descriptors | Unchanged; no failure |
| CS-07 | Preserves the multiset of lines | A chunk with duplicates and mixed lengths | Output contains exactly the same lines with the same multiplicities |
| CS-08 | Produces the same order across repeated runs and chunk placements | The same lines sorted twice, and again after being split differently across two chunks | Byte-identical resulting line order in every case |
| CS-09 | Handles a chunk where every string part shares a top byte | Several string parts all starting with the same letter | Ascending order, matching the un-bucketed comparator |
| CS-10 | Puts empty string parts first | A mix of empty and non-empty string parts | Empty string parts sort ahead of every non-empty one |
| CS-11 | Orders string parts that share a top byte but differ later by the later bytes | String parts starting with the same letter, differing at the second byte | Ascending order by the differing byte |
| CS-12 | Orders string parts whose full eight-byte prefix ties by the bytes after it | Two string parts identical for their first eight bytes, differing at the ninth | Ascending order by the ninth byte |
| CS-13 | Orders string parts shorter than eight bytes by the zero-padded prefix | A one-byte string part, a two-byte string part that extends it, and an unrelated one-byte string part in a different bucket | The one-byte part before its own extension, both before the unrelated part |
| CS-14 | Orders string parts that agree for eight bytes by the ninth and tenth | Three string parts sharing an eight-byte head, differing at the ninth byte and (for two of them) at the tenth | Ascending by the first differing byte |
| CS-15 | Puts a string part of exactly eight bytes before its own nine-byte extension | An eight-byte string part and the same eight bytes plus one more | The shorter one first |
| CS-16 | Puts a shorter string part before one whose ninth byte is a real 0x00 | The same eight-byte head alone, with a trailing 0x00, and with a trailing 'A' | The eight-byte one, then the 0x00 one, then the 'A' one |
| CS-17 | Orders string parts differing only in trailing zero bytes shortest first | `"ab"`, `"ab\0"` and `"ab\0\0"` in one chunk | Ascending by length |
| CS-18 | Orders string parts that agree past the depth cap by the bytes after it | Three string parts sharing twenty identical bytes, differing after them | Ascending by the first differing byte |
| CS-19 | Orders string parts sharing twelve bytes by the thirteenth | Three string parts sharing a twelve-byte head, differing at the thirteenth byte | Ascending by the thirteenth byte |
| CS-20 | Puts empty string parts first among long ones | Two empty string parts and enough long ones to take the chunk past the radix's introsort base case | The empty ones first, ordered between themselves by number |

**Bucketing changes nothing about which test in this section applies.** CS-09 through CS-20 target the radix specifically; CS-01 through CS-08 already exercise the empty-chunk and single-line boundaries it also has to pass through cleanly, and are not repeated here.

**CS-14 through CS-20 carry filler lines, and that is load-bearing.** `ChunkSorter` finishes any range of 32 or fewer descriptors with the introsort alone, so a curated case of three or four lines never reaches the radix at all -- it would assert the comparator, which OC-09 through OC-16 already own, and would have passed against a radix that was never entered. Each of these cases therefore repeats one filler line, sharing the leading bytes of the case so it stays in the same range instead of splitting off at the first level, until the chunk is past that threshold. The filler always sorts last, so it never comes between the lines under test.

**Stability is not required, and the reason is worth stating.** The third comparison level breaks every remaining tie on the raw line bytes, so two descriptors compare equal only when their lines are byte-identical. The relative order of byte-identical lines is unobservable in the output, because either arrangement produces the same bytes. Stability is therefore not a property this sorter needs, an unstable sort is free to be used, and no test in this document asserts it. The dependency is exact: this holds only while the comparator is total. If the raw-bytes tie-break is ever removed, equality stops meaning byte-identity, stability becomes observable, and this question reopens before any other.

---

### Unit 6: The k-way merge

**Responsibility.** Given several sorted sequences of lines, produce a single sorted sequence containing every line from every input exactly once, using a loser tree keyed by each sequence's current head. It reads each input sequentially, holds one head per input, and never buffers an entire input.

**Behaviours that must hold.** The output is sorted and is a permutation of the concatenated inputs. An input that empties keeps its leaf in the merge structure and simply loses every match from then on, without disturbing the others. The merge terminates when every input is exhausted, and terminates on empty input. Memory use is proportional to the number of inputs, not to their sizes.

For testing, the merge must accept in-memory sorted sequences rather than requiring files. That is the whole reason it is a unit and not an integration concern.

**The single-run shortcut.** When run generation produces exactly one run, the merge phase does not read and rewrite it. It moves the run to the output path, which avoids a full read and a full write of the entire dataset, and on a hundred-gigabyte file that is not a micro-optimisation but a halving of the work. Because the temporary directory and the output path may sit on different volumes, a move that fails for that reason falls back to a copy -- not directly onto the output path, but to a uniquely named staging file beside it, moved into place only once the copy has finished without error, so a destination that already exists is either left exactly as it was or fully replaced and never partially written. The run file survives neither path, and the staging file survives neither outcome of the fallback. The placement step is small and sits behind its own seam, so the move-versus-copy decision and the cleanup guarantee are testable without arranging two real volumes; the genuine cross-volume behaviour is an integration concern.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| KM-01 | Merges a single run | One sorted sequence | Output identical to the input |
| KM-02 | Merges two runs | Two sorted sequences that interleave | A single correctly ordered sequence containing every line once |
| KM-03 | Merges many runs | Ten or more sorted sequences with interleaved content | Correct total order; every line present exactly once |
| KM-04 | Merges runs of wildly unequal length | One run with thousands of lines, several with one line each | Correct order; the long run drains without stalling or being dropped early |
| KM-05 | Handles one empty run among non-empty ones | A mix including at least one empty sequence, empty at both the first and last position | Correct output as if the empty run were absent |
| KM-06 | Handles all runs empty | Several empty sequences | Empty output; no failure |
| KM-07 | Handles zero runs | No sequences at all | Empty output; no failure |
| KM-08 | Handles duplicate keys spanning multiple runs | The same line present in three different runs | All three copies appear, adjacent, in the output |
| KM-09 | Selects deterministically when several runs present equal heads | Multiple runs whose current heads tie on the string part and number but differ in raw bytes, and a second case where heads are fully identical | All tied heads are emitted before any greater line; the raw-bytes level decides their order; the count is exact |
| KM-10 | Advances every input exactly once per emission | A crafted set where a double-advance would drop a line | Output count equals the sum of input counts |
| KM-11 | Produces output incrementally without materialising inputs | A large synthetic set of runs consumed lazily | Output is produced progressively; no input is fully buffered |
| KM-12 | Places a single run by moving it when the move succeeds | Exactly one run, with the placement seam reporting a successful move | The output path holds the run's exact bytes; no read-and-rewrite of the contents occurs; no copy is attempted |
| KM-13 | Falls back to a copy when the move fails across volumes | Exactly one run, with the placement seam reporting a cross-volume move failure | The output path holds the run's exact bytes, produced by copy; the failure is not surfaced to the caller as an error |
| KM-14 | Leaves no temporary file behind on either placement path | Exactly one run, placed by move and again placed by copy | No temporary file remains in either case |
| KM-15 | Writes lines longer than the output staging buffer directly, mid-run and at the end | A staging buffer too small to hold a legal maximum-length line; one over-length line mid-run, another as the run's very last line | Both lines land in the output correctly; the staging buffer's async fallback path (design-spec 7.3) is exercised without corrupting the lines around it |
| KM-16 | A middle run's cursor failing to construct still disposes every run's stream | Three runs, the middle one given `RunCursorBuffers` that violate `RunCursor`'s own guard | The guard's exception propagates; every one of the three streams -- the one already wrapped in a cursor, the failing one, and the one never reached -- ends up disposed |
| KM-17 | A run-file cleanup failure after a successful place leaves the replaced destination intact | The run file held open for read only (denying delete, not read), driven through the copy fallback | `File.Delete` throws `IOException`; the destination already holds the run's exact bytes |
| KM-18 | Issues one stream read per window plus one that discovers end of stream | Fixed-width lines and a window sized to hold an exact number of lines with nothing carried over | Read count equals the number of windows plus one final read that returns empty |
| KM-19 | Leaves no staging file behind after a successful copy fallback | Exactly one run, placed by the copy fallback | No `*.partial` file remains beside the output |
| KM-20 | The copy fallback replaces an existing destination | Exactly one run, an output path that already holds different content | The output holds the run's exact bytes afterward |
| KM-21 | A failed final move in the copy fallback leaves an existing destination untouched | An output path locked open for read with delete sharing, denying the write access the final move needs | The exception propagates; the destination's previous bytes are unchanged; only the staging file is removed |

---

### Unit 6a: The range partition and the merge slices

**Responsibility.** Split one single-pass merge into a fixed number of disjoint key ranges, expressed as byte offsets into each run, so that several independent merges can write into disjoint ranges of one output file (design-spec 7.7). The partition is located once and never re-derived; the merge reads nothing but the offsets.

**Behaviours that must hold.** Every located offset is a line start. The offsets of one run are monotone, begin at zero, and end at that run's length, so the slices cover it exactly once. Every line placed in an earlier slice is strictly below every line placed in a later one, across all runs at once -- which is the "first line at or above the splitter" rule and the tie rule (a group of byte-identical lines lands wholly in the later worker) stated together, and is checkable without knowing the splitter keys. A worker reads exactly the lines of its own slice and no neighbour's. A worker's writer refuses any write crossing into its neighbour's range.

**Why the ordering claim is stated as a comparison between slices rather than against the splitters.** One splitter is used for every run, so a line before a run's boundary is below it and a line at or after any run's boundary is at or above it: the two sets are ordered by the splitter that separates them, and checking that directly needs no access to the splitter keys the partitioner does not expose. It is also the stronger statement, because it fails if any copy of a tied line is left on the earlier side -- the earlier side's maximum would then equal the later side's minimum, and the comparison would not be strict.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| RP-01 | Every located offset is a line start and the slices cover each run exactly | Five sorted runs of two hundred lines over four hundred distinct keys, four workers | Each run's offsets are line starts, monotone, start at zero and end at its length; the slice lengths sum to the runs' total; the output offsets are that total's running sum |
| RP-02 | Every line of an earlier slice is strictly below every line of a later one | Six sorted runs of three hundred lines over only ninety distinct keys, five workers | The comparison holds for every pair of slices, under the independent oracle's reading of the order |
| RP-03 | A run of byte-identical lines lands wholly in one worker | Three runs of four hundred copies of one line, four workers | Three slices are empty, the last holds everything; the partition is still complete; `Imbalance` is infinite |
| RP-04 | More workers than lines still yields a partition covering every byte | Three runs of one line each, eight workers | A complete, key-ordered partition with at least five empty slices |
| RP-05 | Duplicate splitters produce empty slices rather than overlapping ones | Two runs over two distinct keys, five workers, so at least three of the four splitters are byte-identical to another | A complete, key-ordered partition with at least one empty slice |
| RP-06 | A malformed run line surfaces as a malformed-line exception, not an aggregate | A run whose second line has no separator | `MalformedLineException` reaches the caller directly |
| RP-07 | Empty runs contribute nothing, and runs holding no bytes at all yield no partition | A mix with empty runs at the first, middle and last positions; then a set of runs that are all empty | The mixed set partitions correctly; the all-empty set returns no partition at all |
| RP-08 | Random run sets at random worker counts are always well formed and key ordered | A sweep over run count, lines per run, distinct-key count and worker count together | RP-01's and RP-02's claims hold at every point |
| RC-01 | A window ending on descriptor capacity exactly at the slice boundary delivers every line and stops | A slice of exactly four eight-byte lines, a window holding exactly four, a descriptor array of exactly four | Exactly those four lines |
| RC-02 | Three capacity-ended windows in a row land on the slice boundary | The same coincidence, three windows deep | Exactly the slice's twelve lines |
| RC-03 | A slice whose last line ends exactly at the bound without filling the descriptor array | Four lines, a window holding four, a descriptor array of seven | Exactly those four lines |
| RC-04 | An empty slice delivers no lines | `start == end` | No lines, no failure |
| RC-05 | A slice starting mid-file delivers its own lines and no neighbours | Eleven lines starting at line seven, a window that does not divide the offset | Exactly those eleven |
| RC-06 | A slice reaching the end of the file delivers the final line | The last four lines of a thirteen-line run | Exactly those four |
| RC-07 | Every slice of a random file at a random window delivers exactly its own lines | A sweep over line count, first line, slice length, window size and descriptor capacity | The slice's own lines, in order, and nothing else |
| RC-08 | Disposing the slice disposes the inner stream exactly once | A counting stream wrapped in a `RunSliceStream`, disposed with `DisposeAsync` | The counting stream's combined dispose count is one |
| OS-01 | Writes land at the slice start rather than at the start of the file | A slice `[4, 8)` of a sixteen-byte file | The bytes land at offset four; `BytesWritten` is four |
| OS-02 | A write that would cross the end of the slice throws and writes nothing | A slice `[4, 8)`, three bytes written, then two more | `InvalidOperationException`; nothing further written; `BytesWritten` unchanged |
| OS-03 | A write that fills the slice exactly is allowed | A slice `[2, 6)` filled by two writes of two bytes | Both succeed |
| OS-04 | An empty slice accepts nothing at all, and a reversed slice is rejected | A slice `[2, 2)` written to; a slice constructed with `end < start` | The write throws and `BytesWritten` stays zero; the construction throws |
| OS-05 | Disposing the slice disposes the inner stream exactly once | A counting stream wrapped in an `OutputSliceStream`, disposed with `DisposeAsync` | The counting stream's combined dispose count is one |
| SF-01 | Marking a fresh file sparse succeeds and the attribute appears | An empty file on the test volume, marked through `SparseFile.TryMarkSparse` | `FileAttributes.SparseFile` is set afterwards; skipped off Windows, and on a volume that refuses the FSCTL |
| SF-02 | A write far past the valid data length lands and the hole reads as zero | A sparse 64 MiB file, one 1 KiB block written at 60 MiB | The block reads back intact, the untouched span reads as zeros, the length is unchanged |
| SF-03 | Clearing the attribute on a fully written sparse file succeeds | A sparse 4 MiB file written end to end, then `SparseFile.TryClearSparse` | The call returns true and the attribute is gone |
| SF-04 | A partitioned merge is byte-identical to the sequential one and its output ends up an ordinary file | Six sorted runs of six thousand lines spread round-robin over the key space, merged at four workers and at one | Identical bytes; on Windows, the output's sparse attribute matches what clearing achieves on that volume, probed independently |

**Where the executor's own half is tested.** `MergeExecutorTests` carries the cases that need a real merge rather than a partition or a stream: a multi-pass merge is never partitioned however many workers the plan allows; a partitioned merge whose workers write fewer bytes than predicted throws naming the worker that did (driven by `\r\n` run files, exactly as the sequential path's own length-check case is); and a failing worker surfaces its own exception rather than a cancellation of the call's own making or the `AggregateException` around it, with every one of the `N × R` run handles closed by the time it arrives -- asserted by deleting each run file afterwards, which on Windows fails outright if a handle is still open.

---

### Unit 7: The multi-pass merge planner

**Responsibility.** Given a run count and a merge fan-in, decide how runs are grouped into merge passes so that the final pass produces exactly one output. It is pure arithmetic over integers and touches nothing else. It is separate from the merge itself precisely so this arithmetic can be tested without any input or output at all.

**Behaviours that must hold.** Every run appears in exactly one group per pass. No group ever exceeds the fan-in. The plan terminates, meaning each pass strictly reduces the run count until one remains. The plan never produces a group of size one, because merging one run into one run copies an entire pass worth of data for no benefit; such a run is carried forward to the next pass instead.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| MP-01 | Plans one pass when the run count is below the fan-in | Runs fewer than the fan-in | Exactly one pass, one group containing every run |
| MP-02 | Plans one pass when the run count equals the fan-in exactly | Runs equal to the fan-in | Exactly one pass, one group, no second pass |
| MP-03 | Plans two passes when the run count just exceeds the fan-in | Runs equal to the fan-in plus one | Two passes; no group exceeds the fan-in; the leftover run is carried forward rather than merged alone |
| MP-04 | Plans three or more passes for a large run count | A run count requiring at least three passes | Correct pass count; every group within the fan-in; run count strictly decreases each pass |
| MP-05 | Handles a single run | One run | Zero merge passes; the run is handed to the placement step rather than to any merge, as covered by KM-12 through KM-14 |
| MP-06 | Handles zero runs | No runs | A plan producing empty output; no failure and no division by zero |
| MP-07 | Rejects a fan-in below two | A fan-in of one or zero | A clear failure at plan time, not a non-terminating plan |
| MP-08 | Never emits a group of size one | Run counts chosen so that naive division leaves a remainder of one, tested across several fan-in values | No group of size one in any pass; the odd run is either carried forward or absorbed into an under-full group |
| MP-09 | Balances group sizes within a pass | A run count that does not divide evenly by the fan-in | Groups differ in size by at most one, rather than several full groups plus one nearly empty group |
| MP-10 | Predicts a pass count matching actual execution | A run count and fan-in used both to plan and to drive a real merge over small in-memory runs | The number of passes actually executed equals the number predicted |

**The planner never plans a pass it does not need.** MP-08 keeps it from emitting a group of size one, and MP-05 keeps it from emitting a pass at all when there is a single run. Both rules exist for the same reason: a pass that produces no ordering change still reads and rewrites every byte of the dataset. The single-run case is then handled by placement rather than by merging, which is specified with the merge in Unit 6.

---

### Unit 8: The buffer pool

**Responsibility.** Hand out a fixed number of pre-allocated buffers and take them back, so that peak memory is bounded by the pool's ceiling rather than by anything the rest of the program does. Acquisition when the pool is exhausted waits for a release; it never allocates an extra buffer. Release is deterministic and happens on every path, including failure. A release of a buffer the pool did not issue, or a second release of a buffer already returned, is a defect and fails loudly.

**Behaviours that must hold.** The pool never has more buffers outstanding than its configured ceiling. A released buffer becomes available again. The same buffer is never outstanding to two consumers at once. A consumer that fails still returns its buffer.

Buffers are not cleared on release. Zeroing a large buffer on every return is wasted work proportional to the buffer size, repeated once per chunk for the life of the run. The contract that replaces it is that every consumer respects the recorded length rather than the buffer's capacity, so residual bytes beyond the recorded length are never read. That contract is what the descriptor model already does anyway, since a descriptor's extent is bounded by the bytes actually filled. BP-10 exists to make the trade visible: it asserts the residual data is genuinely there and genuinely harmless, so a reader can see the risk was considered rather than overlooked.

Note on the concurrency limits of these tests. They can prove the pool's invariants under the interleavings the test happens to produce, and they can prove the single-threaded lifetime rules exhaustively. They cannot prove the absence of a race. The design mitigates this by keeping the pool as the only shared mutable state in phase one, which makes it small enough to reason about directly.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| BP-01 | Round-trips an acquire and a release | A pool with capacity, one acquire followed by one release | A usable buffer of the configured size; after release, availability is restored |
| BP-02 | Defers acquisition when the pool is exhausted | All buffers outstanding, one further acquisition attempted | The attempt waits and does not complete until a release occurs; no additional buffer is allocated |
| BP-03 | Completes a deferred acquisition when a buffer is released | An exhausted pool with a pending acquisition, then a release | The pending acquisition completes and receives the released buffer |
| BP-04 | Returns a buffer when the consumer fails | A consumer that fails partway through its work | The buffer is returned to the pool; a subsequent acquisition succeeds |
| BP-05 | Never hands the same buffer to two consumers | Repeated acquire and release cycles, with buffer identity recorded at every step | No buffer identity is outstanding more than once at any moment |
| BP-06 | Respects the ceiling under concurrent pressure | More concurrent consumers than the ceiling, each acquiring, working briefly, and releasing | The observed maximum simultaneous outstanding count never exceeds the ceiling; every consumer eventually completes |
| BP-07 | Fails loudly on a foreign or repeated release | A buffer the pool did not issue, and a second release of an already released buffer | A clear failure in both cases; never a phantom capacity increase |
| BP-08 | Provides buffers of the configured size | A pool configured for a given buffer size | Every issued buffer is at least the configured size, and the size is stable across acquisitions |
| BP-09 | Handles a ceiling of one | A pool with a single buffer, two sequential consumers | Strict serialisation; both consumers complete |
| BP-10 | Reissues a buffer still holding a previous consumer's bytes | A buffer filled with recognisable data, released, then acquired by a second consumer that fills fewer bytes and records that shorter length | The reissued buffer legitimately still contains the earlier bytes beyond the recorded length; the second consumer's results are correct and depend on nothing beyond its own recorded length |

---

### Unit 9: The memory budget calculator

**Responsibility.** Turn a configured total memory budget and a degree of parallelism into a chunk size and a merge fan-in that together respect the budget in both phases. It is pure arithmetic over its explicit inputs, with no dependence on ambient machine state, and it is the single place where the memory guarantee is expressed as numbers.

**Behaviours that must hold.** In phase one, the product of chunk size and parallelism, plus the descriptor array overhead implied by the chunk size, must fit within the budget. In phase two, the product of the fan-in and the per-run read-ahead buffer size, plus the output buffer, must fit within the same budget, not within a fresh one. The chunk size is never smaller than the configured maximum line length, since a line must fit in a chunk. The calculator never returns a fan-in below two and never returns a non-positive chunk size; it fails clearly rather than returning an unusable plan.

The descriptor overhead is easy to forget and is not negligible. A chunk of short lines produces a very large number of descriptors, and at the shortest realistic line length the descriptor array is a meaningful fraction of the chunk buffer. The calculator must account for it using a worst-case assumption about minimum line length, and that assumption must be a named, tested input rather than a constant buried in an expression.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| MB-01 | Produces a viable plan for a normal budget | A realistic budget with a realistic parallelism | A positive chunk size and a fan-in of at least two; total phase one footprint within budget; total phase two footprint within budget |
| MB-02 | Fails clearly when the budget is too small to be viable | A budget below the minimum needed for one chunk plus a fan-in of two | A clear failure naming the minimum viable budget; never a zero or negative chunk size, never a fan-in of one |
| MB-03 | Handles a degree of parallelism of one | Parallelism of one | The whole phase one budget is available to a single chunk; the plan remains valid |
| MB-04 | Handles a very high degree of parallelism | Parallelism far exceeding what the budget can support at a sensible chunk size | Either the chunk size shrinks to a documented floor with parallelism reduced accordingly, or the call fails clearly; the chunk size is never smaller than the configured maximum line length |
| MB-05 | Accounts for merge read-ahead within the same budget | Any valid budget | The fan-in satisfies the constraint that fan-in times the read-ahead buffer size, plus the output buffer, is within the total budget |
| MB-06 | Accounts for the descriptor array overhead | A budget with a worst-case assumption of very short lines | The effective chunk size is reduced so that the buffer plus its descriptor array fits the budget |
| MB-07 | Responds monotonically to budget changes | Two budgets, one larger | The larger budget yields a chunk size and a fan-in that are each greater than or equal to the smaller budget's |
| MB-08 | Produces a plan from its explicit inputs alone | The same inputs supplied repeatedly, on a machine whose free memory and core count differ between calls | Identical outputs every time; no dependence on observed machine state |
| MB-09 | Yields a plan the planner accepts | The calculator's fan-in passed directly to the multi-pass planner | The planner accepts it and produces a terminating plan |
| MB-10 | Grows the read-ahead window once the fan-in ceiling leaves the budget idle | A budget large enough that the fan-in ceiling (`MaxMergeFanIn`) binds before the budget is exhausted | The read-ahead window grows past its floor to spend the otherwise-idle bytes, up to its own documented ceiling; the fan-in stays at the ceiling; `WorstCasePhaseTwoBytes` stays within the budget |
| MB-11 | Clamps the merge worker count by each of the three rules that can bind it | The shipped configuration at 4 GiB (the measured ceiling binds, and raising parallelism past it changes nothing); 1.5 GiB at the same pair (the window floor binds, one worker short of the next step); 4 GiB at parallelism 3 and at 1 (the operator's parallelism binds) | `MergeParallelism` is 8, 3, 3 and 1 respectively; the per-worker window never falls below `maxLineLength + 2`; `WorstCasePhaseTwoBytes` stays within the budget |
| MB-12 | Falls back to one merge worker at a budget too small to grow the window | MB-01's own 2 MiB configuration; parallelism 4 at its own exact minimum viable budget, where the worker-count loop genuinely runs; parallelism 48 at ITS OWN minimum viable budget | `MergeParallelism` is 1, 1 and 2 respectively; each plan's `WorstCasePhaseTwoBytes` stays within its own budget |
| MB-13 | Keeps phase two inside the budget, and the worker count monotone, across the range that adds workers | A sweep from 300 MiB to 4.25 GiB in 23 MiB steps at the shipped `--max-line`/assumed-mean pair, which crosses every worker-count transition from one to eight | At every point: phase two within the budget, fan-in at least two, per-worker window at or above its floor, and a worker count that never falls as the budget rises; the sweep reaches eight |

---

### Unit 10: The startup capacity precheck

**Responsibility.** Before any work begins, decide whether there is enough free temporary space to complete the run, and fail immediately with a clear message if there is not. The requirement is roughly twice the input size, since run files hold a full copy of the data and an intermediate merge pass may hold another. The arithmetic and the decision are separate from the mechanism that probes free space, so the decision is a pure function of an input size and a reported free-space figure and is unit testable without touching a volume. `--temp` and the output file can live on two different volumes, so `CapacityProbe` checks both: `EvaluateSameVolume` (twice the input, plus a small margin) when they resolve to the same volume, and `Evaluate` (temp) plus `EvaluateOutputVolume` (roughly one input size, plus the same margin) separately when they do not.

**Behaviours that must hold.** A comfortable surplus proceeds without comment. A shortfall fails before the first byte is read, naming the required amount, the available amount, and the directory examined. The check never silently proceeds on a shortfall, and never blocks a run that would have fitted. The multiplier is a deliberately conservative upper bound rather than an exact prediction: a run that produces a single run needs closer to one times the input, because that run is moved rather than rewritten, and a run needing several merge passes still never holds more than the input plus one pass in flight.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| SC-01 | Proceeds when free space comfortably exceeds the requirement | An input size and a free-space figure several times larger | Proceeds; no message |
| SC-02 | Fails when free space is below the requirement | A free-space figure below twice the input size | The decision is insufficient and carries the required and available figures; the program assembles the message, adding the directory it examined |
| SC-03 | Handles the boundary at exactly the requirement | Free space exactly equal to the computed requirement | Per the documented rule, and the rule is stated in the message when it fails |
| SC-04 | Handles an empty input | An input size of zero | Proceeds; empty input is valid and needs no temporary space |
| SC-05 | Fails clearly when free space cannot be determined | A free-space figure reported as unavailable | Per the documented rule, either proceed with a stated warning or fail with a clear message; never treat unavailable as zero |
| SC-06 | Computes the requirement from the input size, not the output size | Input sizes across several orders of magnitude | The requirement scales as roughly twice the input in every case |
| SC-07 | Combines temp and output onto one bound when they are the same volume | `EvaluateSameVolume` at, and one byte below, twice the input plus the capacity margin | Sufficient at the boundary; Insufficient one byte short of it |
| SC-08 | Computes the output volume's own, smaller bound when the two volumes differ | `EvaluateOutputVolume` at, and one byte below, the input size plus the capacity margin; also fed a null free-space figure, and an input near the `long` range | Sufficient/Insufficient at the same boundary rule as SC-03; Unknown with the negative sentinel per SC-05; the requirement clamps rather than overflows per SC-06 |

---

### Unit 11: The generator's line composition and sizing logic

**Responsibility.** Produce lines that satisfy the format, hit a byte-size target rather than a line count, guarantee that a controllable proportion of lines share a string part, and do so reproducibly from a seed. The composition and sizing logic must be separable from writing to a file so it can be tested against an in-memory sink.

**Behaviours that must hold.** The output is the largest whole number of lines, each with its terminator, that does not exceed the requested byte size. It never overshoots, and it never truncates a line to reach the target exactly. Duplicate string parts are guaranteed by construction, not left to the chance that a random draw repeats. The same seed reproduces byte-identical output. Every line produced is parseable, and every line is within the maximum line length. Output uses the single-character terminator, matching the sorter's output convention, and numbers are written in canonical decimal with no leading zeros.

Stopping under the target rather than overshooting is what preserves the round-trip property. Truncating the final line to hit the byte count exactly would emit a line that the parser rejects, and under the fail-fast policy the sorter would then refuse the generator's own output on its very last line. Undershooting by less than one line is invisible to every caller; a malformed final line is fatal to all of them.

That last point has a testing consequence worth stating: because generated numbers are canonical, generated data never exercises the third comparison level. The leading-zero tie is reachable only from hand-written fixtures, which is why OC-13 and OC-14 exist as unit cases and cannot be delegated to the property test.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| GN-01 | Honours a byte-size target rather than a line count | A size target of a few hundred kilobytes | Output size is at or below the target and within one maximum line length of it; the caller never specifies a line count anywhere |
| GN-02 | Stops before the line that would exceed the target | A target that falls in the middle of a line | The crossing line is omitted entirely; output is at or just below the target; the final line present is complete and terminated |
| GN-03 | Guarantees the configured proportion of duplicate string parts by construction | A size target large enough for the ratio to settle, at a duplicate ratio above zero | The output's measured proportion of repeated string parts is deliberate reuse from a fixed pool, not coincidental collision, at zero repeats when the ratio is exactly zero |
| GN-04 | Observes a configured duplicate ratio within tolerance | Several ratios across the range, at a size large enough for the statistic to settle | Measured proportion of lines whose string part appears more than once is within a stated tolerance of the configured ratio |
| GN-05 | Reproduces byte-identical output from the same seed | The same seed and size target, generated twice | Byte-identical output |
| GN-06 | Produces different output from different seeds | Two different seeds, same size target | Different output |
| GN-07 | Handles a size target smaller than a single line | A target of a handful of bytes, below the length of any line the generator can compose | An empty file; never a truncated, unparseable line |
| GN-08 | Handles a size target of zero | A target of zero bytes | An empty, valid file; no failure |
| GN-09 | Produces only lines the parser accepts | A generated file of moderate size, every line fed to the line parser | Every line parses successfully with a number and a string extent; no line exceeds the maximum length |
| GN-10 | Produces numbers within the representable range with a wide spread | A large generated file | Every number is a valid signed 64-bit value, spanning a wide spread rather than clustering in a narrow band |
| GN-11 | Produces varied string part lengths and content | A generated file | String parts vary in length and include at least some multi-word and some multi-byte content |
| GN-12 | Emits the single-character terminator on every line | A generated file, inspected as bytes | No carriage returns appear anywhere; every line including the last is terminated |
| GN-13 | *Not implemented.* Deterministic output across parallelism settings | -- | -- |
| GN-14 | Guarantees a repeated string part at the smallest size that fits the ordinary pair | The exact byte count of the composer's own first two lines, measured directly rather than assumed, at several seeds | Exactly two lines, sharing one string part |
| GN-15 | Guarantees at least one repeated string part at several small sizes and seeds | 512, 1,024, and two sizes not aligned to any obvious boundary (733 and 2,001 bytes), crossed with several seeds | At least one line shares its string part with another |
| GN-16 | Falls back to ordinary generation, without error, when the target is one byte short of the minimal pair | One byte less than the composer's own `MinimalForcedPairByteLength`, its true floor | No exception; output never exceeds the target; the same seed reproduces the same bytes |
| GN-17 | Guarantees a repeated string part at a size that fits two ordinary lines but not the drawn pair | 100 bytes, seed 42 | At least two lines, at least one repeated string part |
| GN-18 | Sweeps small sizes and seeds for GN-17's shape | Sizes 40 through 200 in steps of 10, seeds 1 through 20 (340 combinations) | Every file with two or more lines has at least one repeated string part |

**Not included, and why.** GN-13 has no test because the generator has no parallelism to vary: `TestFileGenerator` composes and writes on one thread, so there is no knob a test could move and no schedule that could reorder its draws. The case is kept in the table rather than deleted because it is the one that would have to be written first if generation were ever parallelised -- a seeded source drawn from several threads without partitioning the stream is exactly how GN-05's determinism gets broken by accident. Its identifier is recorded here so a reader looking for it finds why it does not exist, the same way PB-07 is.

---

### Generator output placement tests (GW)

**Scope.** `tests/TestFileGenerator.Tests/OutputPlacementTests.cs`, driving `Program.WriteToOutput` directly -- `Main` is a private entry point and cannot be driven from a test the way the sorter's own `Program.RunAsync` can, so this method carries the whole write-then-replace behaviour `Main` delegates to. The generator's own output follows the same staging-and-move rule as the sorter's: write under a private staging name beside the requested output, move that staging file over the output only once writing finished without error, and on failure delete only the staging file.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| GW-01 | A successful write replaces an existing destination and leaves no staging file | An existing output holding unrelated stale content | The output holds the newly generated content; no `*.partial` file remains beside it |
| GW-02 | A failed replace of a locked destination leaves it untouched and deletes only the staging file | An existing output held open for read with delete sharing, denying the write access the final move needs | The exception propagates; the previous output's bytes are unchanged; no `*.partial` file remains |

---

### Cross-slice tiers

Three tiers are specified here: property-based, integration, and streaming-layer. None of the three restates the unit tier. Each is written against what the existing suites already cover (`SortRoundTripTests` for end-to-end runs, `RunGenerationStrategyTests` for the streaming layer) so that a case here adds evidence rather than count; where an existing case already covers a behaviour, that is stated instead of a duplicate row. Every case below carries a stable ID and a `[Trait("Case", "...")]` in its test, in the same style as Part 2's units, even though these tests live in their own top-level test folders (`Properties/`, `Integration/`) rather than inside any one production slice's folder, per this document's own organisation rule: cross-slice tests belong to no single slice.

---

### Property-based tests (PB)

**Scope.** `tests/FileSorter.Tests/Properties/`. Random inputs, generated and shrunk by CsCheck (see the README's "Testing" section for the dependency decision), driven through the real composed pipeline or the real production types the unit tier already exercises individually. This tier proves the composition, not the parts: that the parser, the splitter, the comparator, and the merge agree with each other about the cases nobody sat down and wrote by hand.

**The independent oracle.** PB-01 and PB-02 are checked against `NaiveReferenceSort` (`tests/FileSorter.Tests/Properties/NaiveReferenceSort.cs`), written directly from the settled contract table at the top of this document and deliberately referencing none of `FileSorter.LineFormat` -- no `LineOrder`, `LineParser`, `LineCursor`, or `LineDescriptor`. A comparator sharing a defect with its own oracle produces identical wrong output on both sides and the property passes while proving nothing; that tautology is easy to reach by accident, which is why the independence is stated here rather than assumed. PB-03 through PB-05 are different in kind: they test `LineOrder` itself, the same way OC-09 through OC-11 do, so calling it directly is correct there, not a lapse in independence.

`NaiveReferenceSort`'s own line splitting is a thin wrapper over `NaiveLineFormat` (PB-10 and PB-11's own splitter oracle), not a second, separately written reading of the same end-of-file rule: two oracles disagreeing about where a file's last line ends would still be independent of production, and would still make PB-01, PB-02 and PB-13 fail for a reason that has nothing to do with the sorter. PB-15 through PB-18 pin `NaiveReferenceSort`'s end-of-file handling directly, and PB-01, PB-02 and PB-13 now draw the generated file's own final-line termination from `LineEntryGen.FinalTermination` (the same four shapes `RandomLineFileGen` builds for the splitter sweeps) rather than always the plain `'\n'` a fixed test builder would default to, so the disagreement the oracle used to have with production on this exact point is now inside what those three properties actually exercise. `NaiveLineFormat` is by this point the test suite's single source of the end-of-file splitting rule: PB-10 and PB-11 pin it against the real `ChunkReader`/`RunCursor`, and PB-15 through PB-18 pin it against the documented contract directly, so every other test-side reading of the rule is one of those two, not a third.

**Generator shape.** `LineEntryGen` (`tests/FileSorter.Tests/Properties/LineEntryGen.cs`) generates up to 24 `(number, string)` entries per file, with the string part restricted to printable ASCII so that ordinal byte comparison and .NET's `StringComparer.Ordinal` cannot disagree over a surrogate pair -- a real concern, but LP-21/LP-22/OC-07/OC-16's concern, not this tier's to reprove. Every fifth entry also gets a twin (same parsed number, same string part, different raw bytes), because `Gen.Long` and `Gen.String` alone essentially never produce a tie on both stated keys by chance, and without one the third comparison level -- the entire reason PB-02 runs at two budgets -- would never actually be reached. The twin alternates between the README's two independent raw-byte-tie mechanisms, a zero-padded number and an explicit `+`, so both are exercised rather than only one.

**What the tier catches, checked by mutation.** Transposing the primary and secondary keys in `LineOrder.Compare`, with no other change, fails PB-01 and PB-02 immediately, each reporting a CsCheck seed and a shrunk counterexample as small as two or three entries rather than a bare assertion failure. That is the check worth re-running against any future change to `LineOrder`.

**Not included, and why.** One property is deliberately absent: the generator round trip, that every line `TestFileGenerator` produces parses successfully. GN-09 (Unit 11) already checks exactly this, driving real generated output through the real production parser. A CsCheck sweep over many `(seed, size)` pairs would add breadth but no new defect-catching power beyond what GN-05, GN-06 and GN-09 establish together, which makes it a duplicate rather than a gap. Its identifier, PB-07, is listed below so a reader looking for it finds why it does not exist.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| PB-01 | Sorting a generated file matches the independent oracle byte for byte | A random file from `LineEntryGen`, its last line's own termination drawn from `LineEntryGen.FinalTermination` (`'\n'`, `"\r\n"`, none, or a bare trailing `'\r'`), sorted through `Program.RunAsync` at a budget comfortably holding it in one chunk | Sorter output is byte-identical to `NaiveReferenceSort`'s output |
| PB-02 | Sorting the same file at two memory budgets both match the oracle | The same generated file, its last line's own termination varied the same way PB-01's is, sorted once at parallelism 20's own viable floor plus a small margin (a 304-byte chunk, small enough that most of this generator's 0-29-line outputs produce several runs) and once at a budget holding it in one chunk | Both outputs are byte-identical to the oracle, and therefore to each other; the small budget's own plan (`ChunkSize` 304, `MergeParallelism` 1) is asserted directly before the property runs |
| PB-03 | Comparing a generated set is antisymmetric | A freshly generated set of descriptors, every iteration | For every pair, the sign of the forward comparison is the negation of the sign of the backward one |
| PB-04 | Comparing a generated set is transitive | A freshly generated set of descriptors, every iteration | For every ordered triple where x ≤ y and y ≤ z, x ≤ z |
| PB-05 | Comparing a generated set is total | A freshly generated set of descriptors, every iteration | Every pair yields exactly one of before, after, or equal, consistently in both directions; every element ties with itself |
| PB-06 | Concatenating every chunk's lines in order reproduces the input | A generated file read through a real `ChunkReader` over a deliberately small `BufferPool` (buffer and descriptor capacity small enough to force several chunks and carry-over splits even for a few-kilobyte file) | Every emitted line, in chunk order and in-chunk order, concatenated with a `'\n'` after each, equals the original input bytes exactly |
| PB-07 | *Not implemented.* The generator round-trip property | -- | -- |
| PB-08 | Comparing arbitrary byte strings agrees in sign with a reference compare that has no prefix fast path | One arbitrary byte string of 0 to 16 bytes, any value including 0x00, and a second generated as a mutation of the first (a shared head of random length copied from it, then an independent tail drawn from an alphabet including 0x00), each embedded in a real line shape with its own number and raw-line region | The sign of `LineOrder.Compare` agrees with a reference implementation that keeps the same three fall-through levels but has no cached-prefix step |
| PB-09 | Bucketing before the introsort produces the same order as sorting without it | Up to 400 `(number, string part)` entries, string parts arbitrary bytes of 0 to 20 bytes -- every top byte, and so every bucket, reachable, not only the ones printable ASCII produces | `ChunkSorter.Sort`'s output is byte-identical, concatenated, to a naive `Array.Sort` over the same descriptors calling `LineOrder.Compare` directly, with no bucketing pass |
| PB-10 | `ChunkReader` over random inputs matches an independent, non-streaming splitter | Random line lengths 0..`--max-line` (biased so some land exactly at the limit), mixed `\n`/`\r\n`, an occasional leading BOM, an occasional bare trailing carriage return at end of file, random `bufferSize` from `ChunkReader`'s own strict floor up to a couple of KiB, random `descriptorCapacity` (1..7) and pool capacity (2..4), and a stream that hands back only a few random bytes per read; roughly a quarter of iterations replace one line with an over-length one | The concatenated described lines, `LinesRead` and `BytesConsumed` match `NaiveLineFormat.Split`'s reading of the same bytes; on the malformed quarter, the thrown `MalformedLineException`'s byte offset and line number match the naive splitter's own |
| PB-11 | `RunCursor` over random inputs matches the same independent splitter | The same shape as PB-10 (no BOM: `RunCursor` never strips one), with random equal-size read-ahead windows in place of `bufferSize`/pool capacity | The concatenated described lines match `NaiveLineFormat.Split`'s reading; on the malformed quarter, the thrown exception's offset and line number match |
| PB-12 | The partitioned merge produces the same bytes as the sequential one | Random run sets built by bucketing a multiset over 2 to 10 runs (so byte-identical lines land in different runs, and some runs come out empty), drawn from a deliberately tiny vocabulary of eight numbers and six string parts so splitters routinely collide and routinely equal many lines at once; a worker count drawn from 1 to 8, regularly exceeding the line count outright | Merging the same run files at one worker and at the drawn worker count gives byte-identical outputs, and both equal `NaiveReferenceSort`'s output over the concatenated runs |
| PB-13 | Sorting a generated file through a partitioned merge matches the oracle | The same `LineEntryGen` files as PB-01, final-line termination varied the same way, sorted through `Program.RunAsync` at the one configuration that reaches more than one merge worker at a chunk size small enough for a few-kilobyte file to produce several runs (`--max-line` 64, parallelism 66, a budget of 4,362,432 bytes, giving three workers and a 270-byte chunk) | Output is byte-identical to `NaiveReferenceSort`'s, and the plan really does have three merge workers |
| PB-14 | The radix produces the same bytes as a naive `LineOrder` sort, over deliberately tied keys | Up to 900 `(number, string part)` entries, string parts 0 to 60 bytes over a three-symbol alphabet and numbers over five values, so long ties are the common case rather than a coincidence; plus three deterministic stress shapes (200,000 lines of up to 60 bytes, 200,000 of up to 6, 50,000 empty) and one of 200,000 byte-identical 60-byte string parts | `ChunkSorter.Sort`'s output is byte-identical, concatenated, to a naive `Array.Sort` over the same descriptors calling `LineOrder.Compare` directly |
| PB-15 | Sorting an unterminated final line keeps it as a whole line | `"2. Banana\n1. Apple"`, no terminator at all after the last line | `"1. Apple\n2. Banana\n"` |
| PB-16 | Sorting a final bare carriage return strips it as the terminator's other half | `"2. Banana\n1. Apple\r"` | `"1. Apple\n2. Banana\n"` |
| PB-17 | A lone carriage return tail after the last terminated line leaves no extra line | `"1. Apple\n2. Banana\n\r"` -- a complete, `\n`-terminated file with one further `\r` and nothing else after it | `"1. Apple\n2. Banana\n"`, two lines, not three |
| PB-18 | Sorting byte-order-mark-only input produces empty output | The three-byte UTF-8 BOM, nothing else | Empty output |
| PB-19 | Sorting a single bare carriage return produces empty output | One `'\r'` byte, nothing else | Empty output |

---

### Integration tests (IT)

**Scope.** `tests/FileSorter.Tests/Integration/`. Real files on a real file system, driven through `Program.RunAsync` exactly as the binary's `Main` drives it -- no in-memory shortcut. `SortRoundTripTests` already covers empty input, a single-run input leaving no temp files, a forced multi-pass merge, both pipelines producing byte-identical output, cancellation leaving no temp files, and the malformed-line diagnostic; none of that is repeated here. What remains are the line-format cases the unit tier can only exercise in memory (CB-04, CB-06 through CB-11, exercised there against `LineCursor` directly rather than against a real file `Program.RunAsync` reads), plus the two genuinely real-environment cases the plan calls out: the capacity precheck against an actual volume, and single-run placement across two actually different volumes.

**Oracle.** The same `NaiveReferenceSort` the property tier uses (above), applied here to hand-written fixtures rather than generated ones, since each IT case targets one specific structural shape (a BOM, a missing trailing terminator, a specific terminator convention) rather than a random one.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| IT-01 | Sorting a single-line file reproduces that line | One line, terminated | Output matches the oracle |
| IT-02 | Sorting a file with no trailing terminator still emits the final line | A well-formed file whose last line has no `'\n'` | Output matches the oracle; the last line is present, not dropped |
| IT-03 | Sorting a file using only the single-character terminator sorts correctly | Several lines, `'\n'` throughout | Output matches the oracle |
| IT-04 | Sorting a file using only the two-character terminator sorts correctly | Several lines, `"\r\n"` throughout | Output matches the oracle |
| IT-05 | Sorting a file mixing both terminator conventions sorts correctly | Some lines `'\n'`, some `"\r\n"`, in one file | Output matches the oracle |
| IT-06 | Sorting a file beginning with a byte order mark sorts correctly | A UTF-8 BOM, then several ordinary lines | Output matches the oracle; the mark is not part of the first line's number or string part |
| IT-07 | The capacity precheck against the real temp volume reports Sufficient | `TempCapacity.Evaluate` fed a real `DriveInfo.AvailableFreeSpace` reading for the machine running the suite, against a trivially small input size | Outcome is `Sufficient` |
| IT-08 | Placing a single run across two genuinely different volumes falls back to a copy | A small single-run input, `--temp` and the output path on two different real drive roots, detected and confirmed writable at run time | Exit 0; output correct; no leftover `run-*.tmp` in the temp directory |

---

### Streaming-layer tests (SL)

**Scope.** `tests/FileSorter.Tests/RunGeneration/BackpressureAndCancellationTests.cs`. `RunGenerationStrategyTests` already covers, parameterised over both strategies: bounded in-flight chunk count under a fast reader, buffer release when a spill throws, release on cancellation, identical exception type across strategies for malformed input and for cancellation, and a mid-stream spill failure surfacing rather than hanging. None of that is repeated here. The genuine gaps are three. Nothing in that file proves the pool is actually load-bearing rather than merely never observed to be exceeded (SL-01), nothing proves cancellation is prompt rather than merely eventual (SL-02), and nothing proves that a strategy is *finished* when its task completes rather than merely no longer interested — its exception assertions are satisfied by a strategy that surfaces the right failure while a spill of its own is still running against the reader, the pool and the temporary-run registry its caller disposes on return (SL-03 to SL-05, design-spec D16). Every case below runs against both production strategies, for the reason `RunGenerationStrategyTests` states for itself: a behaviour holding for one scheduler and not the other is a defect, not a library quirk.

SL-03 to SL-05 share one shape. One fake spill blocks on an uncompleted `TaskCompletionSource` — deliberately not on its own token, since a spill that abandons its work the instant cancellation is requested joins trivially and proves nothing — and reaches a counting stand-in for `TemporaryRunSet` only once released. The run is then failed around it in three different ways. Exactly one spill is ever held in each case — SL-03 and SL-05 gate the first and let every later one finish at once, and SL-04's input yields a single chunk before the reader fails on the next — because holding them all would saturate the pipeline at parallelism 2, leaving nothing to pull the reader and so nothing to raise the failure the case is about. A strategy that joins nothing would then satisfy the case for the wrong reason. Each case asserts that the strategy's task is still incomplete while the gate is held, that releasing it surfaces the original failure, and that the stand-in registry, closed the moment the strategy's task completes, records no run created after that point. No case sleeps for a result: the only wait not driven by a gate is a short window that gives a strategy that walks away from its spills the chance to do so, and a strategy that joins them cannot complete inside it however long it is.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| SL-01 | A held-open spill stalls the reader until a slot is released | Parallelism 1, one spiller held open on an uncompleted gate, a stream with far more chunks than the pool can hold in flight | `ChunkReader.LinesRead` stabilises below the full input while the gate is held (polled for stability, not a fixed delay) and at least one pool buffer is genuinely outstanding; releasing the gate lets `LinesRead` advance again and the run completes normally |
| SL-02 | Cancelling mid-run unwinds well before the input would have drained | A large stream, an artificial per-chunk spill delay sized so uncancelled draining would take roughly 12.5 seconds, cancelled about 30 ms after starting | The run throws `OperationCanceledException` and the elapsed time from cancellation to that throw is under a stated 10-second bound |
| SL-03 | A spill failure does not end the run while a sibling spill is still running | Parallelism 2; the first spill blocks on a gate, the second waits until the first is genuinely in flight and then throws `IOException` | The strategy's task is still incomplete while the gate is held; releasing it surfaces the `IOException` itself, not a cancellation raised in cleaning up after it; the run registry, closed as soon as that task completes, records no run created afterwards |
| SL-04 | A malformed line does not end the run while a spill is still running | Parallelism 2, two lines per chunk, a malformed third line; the spill of the first chunk blocks on a gate while the reader trips over it | As SL-03, with `MalformedLineException` surfacing |
| SL-05 | Cancellation does not end the run while a spill is still running | Parallelism 2; the first spill blocks on a gate and every later one finishes at once; the input hands over a fixed prefix and then parks, so a read is outstanding when the caller's token is cancelled | As SL-03, with `OperationCanceledException` surfacing |

---

### Startup ownership tests (TR)

**Scope.** `tests/FileSorter.Tests/Startup/TemporaryRunSetTests.cs`. `TemporaryRunSet` touches the real file system directly and has no seam to substitute, so its existing, untraited cases already use a real scratch directory: creating and deleting run paths, tolerating a file already gone by the time `Dispose` runs, and leaving a pre-existing or non-empty `--temp` directory in place. The cases below are the ones added for the private per-invocation namespace: nothing a plain `run-########.tmp` name in the requested directory used to guarantee once two invocations, or an unrelated file, could share it.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| TR-01 | Creates a run path inside a private directory beneath the requested one | A single `TemporaryRunSet` over a fresh directory | The run path's directory is not the requested directory itself, but a direct child of it |
| TR-02 | Two instances sharing the same requested parent use different private directories | Two `TemporaryRunSet`s constructed over the same directory | Their first run paths sit in two different directories, and are themselves different paths |
| TR-03 | Constructing the set does not yet create a private directory | A single `TemporaryRunSet`, immediately after construction, before any run path is requested | The requested directory has no subdirectories yet |
| TR-04 | Disposing removes the private directory even when the parent already existed | A pre-existing requested directory, one run file created and written | The requested directory survives; its private subdirectory does not |

---

### End-to-end tests (ET)

**Scope.** `tests/FileSorter.Tests/EndToEnd/SortRoundTripTests.cs`. Real files, driven through `Program.RunAsync` exactly as `Main` drives it, the same discipline the integration tier (above) uses. The file's older cases -- an empty input, a single-run input, a forced multi-pass merge, both pipelines producing byte-identical output, cancellation leaving no temp files, and the malformed-line diagnostic -- carry no case IDs of their own. The ET identifiers below name the cases in the file that do.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| ET-01 | Sorting a CRLF file of maximum-length lines gives identical output at two memory budgets | Twelve `\r\n`-terminated lines whose content is exactly the configured `--max-line`, sorted once at the computed minimum viable budget and once at a nearby larger one | Both runs exit 0 and produce byte-identical, `'\n'`-only output in ascending order |
| ET-02 | Sorting an input whose final unterminated line ends in a bare carriage return strips it | `"2. Banana\n1. Apple\r"` (no final `\n`) | Exit 0; output is `"1. Apple\n2. Banana\n"`; `--verify` against the same input then exits 0 |
| ET-03 | A partitioned merge and a sequential one produce the same file from the same input | Three hundred lines over a hundred distinct keys, sorted twice: once at PB-13's three-worker configuration (tens of runs, one pass), once at an ordinary 1 MiB budget (one worker) | Both exit 0, leave no `run-*.tmp`, and produce byte-identical output, equal to `NaiveReferenceSort`'s |
| ET-04 | Sorting lines whose content ends in a carriage return before the line feed sorts byte-correctly at every merge shape | Sixty lines (one third `\n`-terminated, one third ordinary `\r\n`, one third ending their own content in `\r` before an explicit `\r\n`, `"...\r\r\n"`) sorted at a single-run budget, at parallelism 30's minimum viable budget + a small margin (98-byte chunk, genuine multi-run, single-pass, N = 1), and at parallelism 71 / budget 4,698,304 (307-byte chunk, N = 3, PB-13's own pair); a separate, larger fixture of 30,000 of the same cycling lines at parallelism 4's bare minimum viable budget (60,531-byte chunk, `MergeFanIn` pinned at `MinMergeFanIn` = 2, genuinely multi-pass) | All four exits are 0, all four outputs are byte-identical to `NaiveReferenceSort`'s, `--verify` against each is exit 0, and each shape's own plan (`MemoryBudget.Calculate`) and Program's own "phase one produced N run(s)" stderr line are asserted directly -- run count, `MergePlanner.Plan(runCount, plan.MergeFanIn).Count` (1 for the two single-pass shapes, 4 for the multi-pass one), and `plan.MergeParallelism` (1, 1, 1 and 3) |
| ET-05 | Sorting the sorter's own output again loses a trailing content carriage return | Input `"1. a\r\r\n"` (content `"1. a\r"` under the input rule), sorted once to `"1. a\r\n"`, then that OUTPUT file sorted again as a fresh input | Both exits are 0; the first output is `"1. a\r\n"`; the second is `"1. a\n"` -- one byte shorter, identical otherwise |
| ET-06 | An unrelated sentinel named like a run file survives a successful sort | A file named `run-00000001.tmp` beside the output, in `--temp`'s default location | Exit 0; the sort's own output is correct; the sentinel's bytes are unchanged |
| ET-07 | An output named like a run file is produced correctly | The output path itself is `run-00000001.tmp` | Exit 0; the file exists afterward with the correct sorted content |
| ET-08 | Two concurrent sorts sharing one temp parent do not disturb each other | Two independent small sorts, run concurrently with `Task.WhenAll`, both given the same `--temp` directory | Both exit 0 with their own correct output; no run file or private directory survives in the shared parent afterward |
| ET-09 | Sorting forces a multi-pass merge and leaves no private directory behind on success | A budget at `MemoryBudget.MinimumViableBudget`'s own minimum (`MergeFanIn` pinned at 2), over an input sized to produce several runs | Exit 0; `MergePlanner.Plan(runCount, plan.MergeFanIn).Count` is greater than 1; the temp directory this run created no longer exists afterward |
| ET-10 | A forced failure during phase one still removes the private directory | A malformed line, surfacing `MalformedLineException` after phase one may already have spilled some runs | The exception propagates; the temp directory this run created no longer exists; the output was never created |
| ET-11 | An empty-input sort reports a read-only existing destination by name and exits 3 | An empty input; an existing output marked read-only | Exit 3; stderr names the output path; the previous output's bytes are unchanged; no `*.partial` file remains |
| ET-12 | A single-run sort reports a read-only existing destination by name and exits 3 | A one-run input; the same read-only existing output as ET-11 | Exit 3; stderr names the output path; the previous output's bytes are unchanged; no `*.partial` file remains |
| ET-13 | A successful single-run sort still replaces an existing destination | A one-run input; an existing output holding unrelated stale content, not locked | Exit 0; the output holds the new sorted content; no `*.partial` file remains |
| ET-14 | An empty-input sort reports a destination that is a directory by name and exits 3 | An empty input; an existing directory sitting at the output path | Exit 3; stderr names the output path; the directory is unchanged; no `*.partial` file remains |
| ET-15 | A single-run sort reports a destination that is a directory by name and exits 3 | A one-run input; an existing directory sitting at the output path | Exit 3; stderr names the output path; the directory is unchanged; no `*.partial` file remains |

---

### Verify tests (VF)

**Scope.** `tests/FileSorter.Tests/EndToEnd/VerifyTests.cs`, driving `VerifyCommand.RunAsync` directly -- the entry point `sorter --verify` itself uses, the same discipline `SortRoundTripTests` applies to `Program.RunAsync`. `--verify` exists so a result larger than the 20 GiB oracle's shape allows (a second full sort to compare against) can still be checked: it reads the output once and requires every adjacent pair to be non-decreasing under the sorter's own three-level order (reusing `LineParser`, `LineDescriptor.BuildPrefix` and `LineOrder.Compare`, so the check is against the same comparator the sort itself uses, not a second, independently-drifting one), and reads the input and the output once each, in parallel, to compare line counts and an order-independent hash of every line (the wrapping sum of FNV-1a 64 per line, input lines normalised for hashing exactly as `LineCursor` normalises them for sorting). A malformed line in either file is the same defect design-spec 10's exit-1 row already covers and is handled identically; a genuine ordering or content disagreement gets its own exit code (4) rather than reusing exit 3 ("invalid arguments"), since nothing about the arguments is wrong.

| ID | Test name | Input | Expected outcome |
|---|---|---|---|
| VF-01 | Verifying a genuinely sorted output reports success | A small file and its correctly sorted output | Exit 0 |
| VF-02 | Verifying an out-of-order pair reports the right line number | An output with two adjacent lines swapped | Exit 4; the failure names the output line number where the violation was found; `VerificationResult.OrderViolationLineNumber` matches it and `Output.LineCount` reflects only the lines checked before the violation |
| VF-03 | Verifying a dropped line is caught by the count and hash check | An output missing one line, otherwise still sorted | Exit 4; input and output line counts disagree |
| VF-04 | Verifying a duplicated line is caught by the hash check when the count still matches | An output with one line duplicated in place of another, so the line count is unchanged and the result is still non-decreasing | Exit 4; line counts agree, hashes disagree |
| VF-05 | Verifying a `\r\n` input against a `\n`-only output verifies | A `\r\n`-terminated input, correctly sorted into single-terminator output | Exit 0 |
| VF-06 | Verifying a malformed output line surfaces `MalformedLineException` naming the offending line | An output containing a line with no separator | The existing exit-1 diagnostic, not `--verify`'s own exit 4; the message names the output path |
| VF-07 | Verifying an input whose final unterminated line ends in a bare carriage return verifies | An input ending `"...Apple\r"` (no final `\n`), correctly sorted | Exit 0 |
| VF-08 | Verifying a malformed input line names the input path, not the output path | An input containing a line with no separator, a well-formed output | The exit-1 diagnostic names the input path only |
| VF-09 | Verifying output whose content ends in a carriage return before the line feed verifies | An input line reading `"1. a\r\r\n"` (content `"1. a\r"` under the input rule), correctly sorted into output ending `"1. a\r\n"` (a bare `\n` terminator, the `\r` kept as content) | Exit 0 |
| VF-10 | Verifying an output whose final carriage return is never terminated fails | Input `"1. a\n"`, output `"1. a\r"` (no final `\n` at all) | Exit 4; `VerificationOutcome.OutputNotTerminated` |
| VF-11 | Verifying an output that is a single unterminated carriage return fails against an empty input | Empty input, output `"\r"` | Exit 4; `VerificationOutcome.OutputNotTerminated` |
| VF-12 | Verifying an output missing its final line feed fails even without a trailing carriage return | Input `"1. Apple\n2. Banana\n"`, output `"1. Apple\n2. Banana"` (no final `\n`) | Exit 4; `VerificationOutcome.OutputNotTerminated` |

---

### Benchmark and performance measurement specification

`benchmarks/FileSorter.Benchmarks/` is a separate console project (BenchmarkDotNet), never invoked by `dotnet test`, with `<IsTestProject>false</IsTestProject>` stating that rather than relying on the absence of a test adapter reference.

**Phase one only, for the D7 comparison.** `RunGenerationBenchmarks` measures `AkkaRunGeneration` and `ChannelRunGeneration` in isolation, not a whole sort. Timing a full sort would bury the scheduler difference under merge I/O — phase two is identical between pipelines — and produce a ratio that mostly measures the disk.

**Three levels, because one number cannot defend a claim about where time goes.** `RunGenerationBenchmarks` is realistic: a real `ChunkSpiller` writing real run files, so the figure is what an operator would see. `SchedulerOverheadBenchmarks` runs the identical shape with a spill that does nothing but release the pool slot, isolating scheduling and hand-off from disk I/O. On the measuring machine the realistic figure is tens of milliseconds and the no-op figure a little over ten — five to seven times smaller at parallelism 1 and two to four times smaller at parallelism 4, which is not the separation that would let scheduling be dismissed outright. What the no-op figure bounds is reading, parsing, scheduling and hand-off together, since it still pulls the whole input through `ChunkReader`; what is attributable to the scheduler is the difference between the two pipelines on that shape, under three milliseconds either way where both pipelines are stable. What the no-op spill cannot say is whether the rest is the sort or the write, because it removes both; `SpillWorkBenchmarks` is the third level, with a spill that either sorts without writing or writes without sorting. The chunk sort itself is not benchmarked here at all: the 2 MiB chunks these classes use are too small for a radix pass to show anything, so that measurement is taken on real chunks and reported in [measurements.md](measurements.md).

**Channels is the baseline; Akka is reported as a percentage against it** (D7, `[Benchmark(Baseline = true)]`), with `[MemoryDiagnoser]` reporting allocation alongside the ratio. If a run shows Akka slower, that number is reported as measured; design-spec 6 commits in advance to defending the default on composition and failure semantics rather than on speed.

**The `ActorSystem` is built in `[GlobalSetup]` and disposed in `[GlobalCleanup]`, outside the measured region.** `ActorSystem.Create` is on the order of a hundred milliseconds, and charging that once per iteration would make the ratio mostly a statement about process startup. This deliberately differs from `Program`'s D1 placement, where the same fixed cost is paid once per sort regardless — the placement differs precisely because the reason is the same.

**Per-iteration state is rebuilt in `[IterationSetup]`, not shared from `[GlobalSetup]`.** `ChunkReader` holds a read position, `BufferPool` is stateful, and `TemporaryRunSet` accumulates paths; only the generated bytes and the computed `MemoryPlan` are shared. Without this, the second iteration onward reads an exhausted stream and the benchmark silently measures an empty loop. `[IterationCleanup]` disposes each iteration's `TemporaryRunSet` and stream so runs do not accumulate on disk.

**Input.** `SyntheticInput`, internal to the benchmark project, produces `<Number>. <String>` bytes from a fixed vocabulary with a seeded `Random`. It is deliberately not `TestFileGenerator`'s `LineComposer`: that type is internal to its own program, and duplicating a small line-writer here is the same trade the two shipping programs already make for the line grammar itself.

**The matrix is deliberately small, but not so small it cannot support a claim.** Parallelism `{1, 4}`, one input size (8 MiB), one budget (2 MiB), two launches of five warmup and twenty measured iterations. Three iterations (`ShortRunJob`) will not support a published ratio — the reported error comes out larger than the mean it qualifies, and repeated runs move the figure across a range wide enough to invert its conclusion. Twenty across two launches bring the error to a few per cent and separate per-process variance from the code under test, at well under a minute per class. What is still traded away is breadth: these numbers describe this shape of work on this machine and are not a throughput claim in general.

**Memory flatness.** `--flatness` is a third mode of the same executable — not a BenchmarkDotNet benchmark and not a tagged unit test, so "never in the default gate" holds by construction rather than by a `--filter` someone can forget. It drives the real pipeline at one fixed budget over inputs at roughly 1, 10 and 100 MiB, sampling `GC.GetTotalMemory(false)` on a five-millisecond background timer and keeping the running maximum — not a single read afterwards, which reports what survived rather than the high-water mark.

**Each input size is measured in a freshly launched process, and that is load-bearing rather than an optimisation.** An earlier version ran all three sorts in one process, and every size but the smallest showed a "peak" within a few percent of its own input — indistinguishable from a leak that scales with input. It was not one: generating a hundred megabytes of synthetic lines allocates and frees several hundred thousand short-lived objects, and freeing them does not undo the GC's adaptively-sized generation budgets and segments, which `GC.Collect()` does not shrink back and which `GC.GetTotalMemory` reports alongside genuinely live bytes. A minimal repro confirmed it with none of the sorter's types involved. Running each size in a fresh worker — the parent generates the input and writes it to disk, the worker only reads a plain file and sorts it — removes the confound, because the process being measured never generated the input whose size is under test.

Even with that isolation the reading still climbs a little: 16.0, 17.5 and 22.5 MiB against a fixed 16 MiB budget, a 41.2% spread. Checked directly, the byte count immediately after `BufferPool` construction — the actual configured footprint — is flat at roughly 15 MiB regardless of input size, and the residual is the same GC bookkeeping scaled to the sorter's own allocation activity, since a bigger input needs more chunks at a fixed budget and so triggers more collections. The stated tolerance is 60%, wider than a first guess, specifically to cover that residual honestly rather than tune it away.

**`--flatness` never reaches `MergeParallelism` above 1**, since at this harness's parallelism, `--max-line` and assumed mean the plan needs roughly 50 MiB before a second merge worker is affordable. `--flatness-parallel-merge` is a second CLI mode sharing every mechanism above at 64 MiB — the smallest round budget giving `MergeParallelism` 2, derived in `FlatnessRunner`'s own comment from `MemoryBudget`'s closed form — and adds a fourth size, 1 GiB. A separate mode rather than a second budget folded into one invocation, so neither matrix's published figures can be silently replaced by a merely-similar sample from the other. Each worker prints its own computed `MergeParallelism` and the parent checks every size agrees before reporting the table — a defence against this class's comments drifting from what `MemoryBudget.Calculate` actually does.

**`PartitionedMergeBenchmarks` is the in-process benchmark for the partitioned merge.** `MergeBenchmarks` drives `KWayMerge.MergeAsync` directly and carries no merge-worker dimension, so every `MergeParallelism` figure otherwise comes from manual 20 GiB runs. This class adds `[Params(1, 2, 4, 8)] MergeWorkers` over `MergeExecutor.ExecuteAsync`, `RangePartitioner` included. Reaching 8 needs a plan built at `parallelism >= 8`, so the class builds one at 256 MiB / parallelism 8 and reaches the smaller values by overriding `MergeParallelism` alone. That override is safe in exactly one direction — `WorstCasePhaseTwoBytes` is strictly increasing in `MergeParallelism` with every other field fixed, so a plan that fits at 8 fits a fortiori at 1, 2 and 4 — and `[GlobalSetup]` asserts it for every forced value at run time rather than trusting the algebra silently. `[IterationSetup]` rewrites fresh run files from an in-memory template every iteration, since `MergeExecutor` deletes every run file it merges.

**Machine and disk characteristics are recorded with every result**, not once in this document: BenchmarkDotNet prints CPU, runtime and OS in its summary, and the README states the disk alongside the numbers. A result committed without that context is a claim about the code that the code did not make; the machine made it.

---

## Part 3: Coverage and risk

### Evidence mapping

| Evaluation criterion | Evidence |
|---|---|
| Code quality and readability | Vertical slice test organisation mirroring the production slices, described in Part 1. Every unit in Part 2 has a stated single responsibility, and the fact that each is testable in isolation with no test doubles is itself the structural evidence. The seams called out explicitly, splitter separable from file input and output, merge accepting in-memory sequences, placement separable from the volume it writes to, capacity decision separable from the free-space probe, generator composition separable from writing, are the readability claim in executable form. The settled contract table means a reviewer can check behaviour against a single page rather than inferring it from tests. |
| Performance considerations | KM-12 on the single-run move, which removes a full read and a full write of the entire dataset. MP-05, MP-08, and MP-09 on never planning a pass or a group that does no work. MP-10 on predicted versus actual pass count. MB-04 and MB-07 on sizing behaviour under extreme and changing configuration. CM-02 and CM-07 asserting that the hot path neither moves bytes nor re-parses numbers. LP-22 and OC-16 confirming nothing is decoded or validated on the hot path. BP-10 on the no-clear contract that keeps a per-chunk cost off the release path. Absolute throughput is deferred to the benchmark pass. |
| Memory management | BP-02, BP-04, BP-05, BP-06, BP-07, and BP-09 on the pool's ceiling and lifetime rules, and BP-10 on the recorded-length contract that replaces clearing. MB-04, MB-05, and MB-06 on the line-length floor, read-ahead, and descriptor overhead within one budget. CB-03 and CB-12 on the bounded carry-over, which is what makes the line-length limit load-bearing rather than cosmetic. KM-11 on incremental merge output. SC-01 through SC-06 on disk capacity, the resource the memory bound does not cover, and KM-14 on temporary files not surviving placement, which is the other half of that resource. The flat-working-set and tiny-budget tests from Part 1. |
| Test coverage and reliability | Two hundred unit cases concentrated on the correctness-bearing core, with the property-based byte-identity tests (PB-01, PB-02) above them, each mutation-tested against `LineOrder.Compare` and confirmed to fail before being confirmed to pass again. Determinism assertions in OC-13, OC-14, CS-08, MB-08, GN-05, and PB-02. IT-01 through IT-06 pin the same oracle against real files. Stable identifiers tying every case back to this document. |
| Multithreading and concurrency | BP-06 on ceiling adherence under concurrent pressure. The cross-parallelism output equality check on determinism under varying parallelism. MB-08 ensuring the plan itself does not vary with ambient machine state, which would make output vary too. The structural argument that the pool is the only shared mutable state in phase one and that phase two's merge workers share only an interlocked progress counter (design-spec 7.7). PB-12 and ET-03 on the partitioned merge agreeing byte for byte with the sequential one, at several worker counts. `RunGenerationStrategyTests` on bounded occupancy, release discipline, and exception identity across both strategies; SL-01 and SL-02 on backpressure demonstrated as genuinely blocking and on cancellation responsiveness within a stated bound; SL-03, SL-04 and SL-05 on a strategy having joined every spill it started before it returns, so that a failed or cancelled run has no worker still touching the reader, the pool or the temporary-run registry its caller is disposing. |
| Thoughtful edge case handling | LP-02 through LP-25 on parsing, including both range boundaries and the diagnostic itself. CB-12 and CB-16 on both maximum-line-length boundaries, which live with the splitter rather than the parser under D2. CB-05 on the trailing region and CB-07, CB-08, CB-14, and CB-15 on carriage returns, the two cases that fail fast turns from cosmetic into fatal. OC-13 and OC-14 on the leading-zero tie that would otherwise make output budget-dependent. LP-22 and OC-16 on invalid encoding passing through by design. KM-05 through KM-09 on empty, unequal, and tied runs, and KM-13 on the cross-volume placement fallback that would otherwise fail at the very last step of a successful run. MP-02, MP-05, MP-06, and MP-08 on planner arithmetic. SC-03 and SC-05 on capacity boundaries. GN-02, GN-07, and GN-08 on the size boundary and degenerate targets. |

### Decisions

Every behavioural decision is settled and recorded in the contract table at the top of this document, which is the single place to look for any of them. The tables above depend on that table row for row: a rule cannot change there without changing cases here, so the two move together or not at all. The only conditional row in Part 2 is GN-13, a scope note about whether generation is parallelised at all, not an unmade decision; it is recorded as not implemented, with its reason, beside Unit 11's table.

### Residual risks the unit tests do not retire

Real hundred-gigabyte behaviour. A 100 GiB run is now recorded -- 313.4 s at a 6 GiB budget, one merge pass, `--verify` clean -- on a second volume large enough to hold input, temporary files and output together. What it does not retire is an independent oracle at that size: the run is checked for order, line count and content hash against its own input, not against a separately produced sort, because an oracle costs a second full sort. Nor is its input representative: it is a concatenation repeating one 20 GiB block three times, so it carries more duplicates than the generator's own output. File handle pressure at a high fan-in and temp directory capacity beyond the startup estimate are exercised by the recorded 20 and 100 GiB runs, and the memory-flatness argument covers the working set. The readme says so under "Limits, honestly".

Absolute performance. Nothing in this suite says the sort is fast, only that it is bounded and correct. Throughput is a benchmark concern and is deferred.

Concurrency defects outside the pool. Tests demonstrate correctness for observed interleavings only. The mitigation is structural rather than exhaustive testing: phase one's shared mutable state is the buffer pool, which is small enough to reason about directly and is tested against its invariants, and the run registry, one locked list of created run paths, and phase two's merge workers -- up to eight of them -- share nothing but a progress counter of two interlocked adds, every buffer, handle, cursor and output range being one worker's own, on ranges located before any worker starts.

Balance of the partitioned merge on adversarial data. The slices are key ranges, so a group of byte-identical lines cannot be split: an input whose lines are all one key gives one worker everything and the rest nothing, and the sort is then exactly as fast as the sequential merge, having also paid for the splitters. RP-03 and PB-12 pin that this stays correct, and `RangePartition.Imbalance` is printed on stderr; nothing detects or reroutes the case.

Disk exhaustion after the startup check. The precheck retires the common case, which is starting a run that was never going to fit. It cannot retire a volume filled by another process partway through. Injected failures cover the internal handling; the real condition is a manual check.

Cost of the fail-fast policy. A single malformed byte late in a very large file discards the work done so far. This is a deliberate trade, not an oversight, and the lenient side-channel mode is named in the readme as the extension for anyone who needs the other side of it.

Configuration outside the tested envelope. The budget calculator is tested at normal, minimal, and extreme values, but the space of budget, parallelism, and line-length combinations is not enumerated. A hostile configuration may still produce a poor plan, which is why MB-02 and MB-04 must fail loudly rather than degrade quietly. `--memory` and `--parallelism` have to be chosen together for the same reason: the plan holds parallelism fixed and rejects a budget that cannot cover its write buffers, rather than quietly running at a lower parallelism than was asked for.
