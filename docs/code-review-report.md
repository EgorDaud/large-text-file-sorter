# Architecture and Code Review Report: large-text-file-sorter

Scope: external merge sort (`src/FileSorter`), test file generator (`src/TestFileGenerator`), tests, benchmarks, CI.
Method: five parallel review agents (architecture, LineFormat/Startup, RunGeneration, Merging, tests/benchmarks/generator), followed by an independent verification pass over every candidate finding.
Lens: KISS, DRY, SOLID (S/O/L/I/D), YAGNI.

## 1. Executive summary

Overall grade: **A- (4.4 / 5)**.

The codebase is in very good shape. After verification, no finding is above **low** severity. Of 52 candidate findings, 39 were dropped as false, overstated or not worth acting on. 13 survive, and every survivor was downgraded or confirmed as low. None affects correctness, data safety or runtime behaviour. They are about folder and namespace hygiene, a few long methods, and small duplications.

The main themes:

1. The `Startup` namespace is a grab-bag that other layers depend on, creating a namespace-level cycle (reported independently by two reviewers).
2. A few orchestration methods are long: `Program.RunAsync`, `MergeExecutor.ExecuteAsync`, `PartitionedMerge.TryMergeAsync`, `OutputVerifier.ScanCoreAsync`.
3. Small leftover duplication in benchmarks, tests, the merge paths and the two slice streams.

The run-generation module produced no surviving findings.

## 2. Architecture overview

Phases map to top-level modules in one `FileSorter` assembly:

| Module | Role |
|---|---|
| `LineFormat` | Leaf dependency: `LineCursor` (framing), `LineParser` (grammar), `LineDescriptor` (key), `LineOrder` (comparison) |
| `RunGeneration` | Read chunks, sort (in-place American flag sort with 8-byte prefix), spill runs. Channels strategy with a bounded buffer pool |
| `Merging` | `MergePlanner` (pure), `KWayMerge` (loser tree), sequential multi-pass `MergeExecutor`, `PartitionedMerge` (parallel), `RunPlacement` |
| `Startup` | CLI, `ConsoleRun`, `MemoryBudget`/`MemoryPlan`, `TemporaryRunSet`, `CapacityProbe`, `ProgressReporter` |
| `Verification` | Independent checker: order, count and hash |
| `TestFileGenerator` | Separate project: `LineComposer`, `Vocabulary`, `FileWriter`, atomic staged write |

`Directory.Build.props` centralises the target framework, nullable, warnings-as-errors and analyzer levels. CI runs a Linux and Windows matrix plus a Debug leg so `Debug.Assert` invariants execute.

## 3. Scorecard

| Principle | Rating | Rationale |
|---|---|---|
| KISS | 4 / 5 | Hot paths are deliberately designed and explained. Some long methods and the 353-line generator `Program` add cognitive load. The Channels path (88 lines) is simple. |
| DRY | 4 / 5 | Memory budget, parser, comparator and temp-directory helper are single sources. Residual duplication: `RunCursorBuffers` construction, slice-stream boilerplate, EOF-tail rule in three places, benchmark scratch dirs. |
| SOLID: S | 3.5 / 5 | Most classes are cohesive. `Startup` mixes concerns, and `Program`, `MergeExecutor` and `CapacityProbe` carry more than one reason to change. |
| SOLID: O | 4 / 5 | Run generation is behind a strategy contract. Merge path selection is one decision point. No findings. |
| SOLID: L | 5 / 5 | No inheritance hierarchies at risk. The stream wrappers deliberately support only what is needed. No findings. |
| SOLID: I | 4.5 / 5 | Small, focused types and contracts. No findings. |
| SOLID: D | 4 / 5 | Core modules reference `Startup` types (`MemoryBudget.UnbufferedStream`, `ProgressReporter.Describe`) and `Startup` references `KWayMerge`. This is a direction-of-dependency smell within one assembly. |
| YAGNI | 4.5 / 5 | No speculative abstractions were reported. The Akka-versus-Channels cost was measured and documented in `docs/measurements.md`. |

## 4. Strengths

- `LineFormat` is a small, cohesive leaf used by every phase. `LineOrder` has a tie-break that makes output deterministic across chunk sizes.
- Hot-path design is deliberate and measured: packed descriptors, an 8-byte big-endian prefix with a full-compare fallback, an in-place American flag permutation, stackalloc'd bucket tables, and a class-based `LineStager` that keeps `await` out of the per-line loop.
- `MemoryBudget`/`MemoryPlan` computes worst-case formulas from real struct sizes (`Unsafe.SizeOf`), so it tracks layout changes.
- `BufferPool` bounds memory by construction, rejects double release, and exposes `Outstanding` for leak tests.
- `ChunkSpiller` preallocates the run size and verifies `file.Position`. It uses `CreateNew` so collisions surface.
- `MergePlanner` is a small pure function. `KWayMerge` uses a loser tree with a strict run-index tie-break, which keeps the sort stable.
- Integrity checks: predicted-versus-actual byte counts in both merge paths and the `OutputSliceStream` bounds guard.
- `PartitionedMerge` handles worker failure well: linked cancellation, wait for all workers before deleting runs, and root-cause selection over sibling cancellations.
- Crash and cancel safety: atomic write-then-replace via staging files, a private per-invocation temp directory with collision retry, a concurrency-safe `TemporaryRunSet`, and best-effort cleanup.
- `ConsoleRun` gives sort and verify one cancellation and failure policy mapped to documented exit codes.
- `TempCapacity` is pure and overflow-safe, with Unknown, Sufficient and Insufficient outcomes.
- Verification is independent of the sorter. It reads both files concurrently, observes second faults via `Task.WhenAll`, and requires an LF-terminated tail. The shared parser is honestly documented as not an independent oracle.
- Tests use an independent oracle (`NaiveReferenceSort`, `NaiveLineFormat`), property tests and boundary sweeps. About 305 tests carry traceable case IDs.
- Benchmarks reuse the real generator through `SyntheticInput.cs`.
- CI is minimal and purposeful. Design intent is documented in `design-spec.md`, `measurements.md` and `test-strategy.md`.

## 5. Findings

All 13 verified findings are **low** severity. There are no critical, high or medium findings. Several were downgraded from medium or high during verification, noted below where relevant.

### 5.1 KISS

**K1. Generator `Program.cs` mixes parsing, signals, staged write and progress**
- File: `src/TestFileGenerator/Program.cs:10`
- Evidence: 353-line static class holding `Usage`, `TryParseOptions` (129), `TryTakeValue`, `WriteToOutput` (246, with staging logic), `TryParseSize`, `Describe`, and a progress closure (117-126) mutating a captured variable. Tests reach in via internals. About 100 lines are usage text and comments. The `Generation/` folder already holds `FileWriter` and `GeneratorOptions`, so the original claim that it only covers composition was partly wrong.
- Recommendation: Move parsing into `GeneratorOptions` or a command-line class and the staged write into `FileWriter` or a run class. Leave `Main` as parse, run, map exit code. This mirrors the sorter's `CommandLine`/`ConsoleRun` split.

**K2. `CapacityProbe` name and responsibility drift**
- File: `src/FileSorter/Startup/CapacityProbe.cs:6`
- Evidence: Holds `TryCreateTemporaryRunSet` (45-62), a factory for `TemporaryRunSet`; `TryValidateOutputDirectory` (16) and `DirectoryOf` (9) are path utilities. `CheckCapacity` (131) returns `int?` and prints through `ReportCapacityIssue`. `Program.cs:50-54` carries a CA2000 suppression caused by the Try-pattern ownership. The class header already calls it "Preflight".
- Recommendation: Split into a `Preflight` class (output directory validation, temp directory creation) and `VolumeCapacity` (probe plus pure decision). Throw typed errors instead of Try-with-outs plus `int?`, which also removes the suppression.

**K3. Very large test files**
- File: `tests/FileSorter.Tests/EndToEnd/SortRoundTripTests.cs:1`
- Evidence: 1058 lines. Others: `MemoryBudgetTests` 900, `ChunkReaderTests` 669, `RunCursorTests` 647, `MergeExecutorTests` 635. `FileSorter.Tests.Properties.NaiveReferenceSort.Sort` is fully qualified at lines 327, 379, 424. The oracle is shared by `Integration/LineFormatIntegrationTests.cs` and `Merging/RangePartitionerTests.cs`.
- Recommendation: Split the round-trip file by concern (happy path, multi-pass, cancellation, failure). Move the shared oracle to `Support/`. This is a minor organisation nit.

### 5.2 DRY

**D1. `RunCursorBuffers` construction duplicated across merge paths**
- File: `src/FileSorter/Merging/PartitionedMerge.cs:222`
- Evidence: Identical construction at `MergeExecutor.cs:90-93` and `PartitionedMerge.cs:222-225`, so buffer sizing could drift. The claim that run and output `FileStream` open options are duplicated was overstated: `FileShare` and `FileMode` differ between the paths.
- Recommendation: Add `RunCursorBuffers.Create(MemoryPlan)`. Do not share the `FileStream` open helpers.

**D2. Two stream wrappers copy dispose, validation and NotSupported boilerplate**
- File: `src/FileSorter/Merging/OutputSliceStream.cs:62`
- Evidence: `OutputSliceStream` (9-102) and `RunSliceStream` (9-97) repeat the `end < start` check, the `_disposed` flag, the `DisposeAsync`/`Dispose` pair, the CA2215 suppression text and the NotSupported stubs. About 30 lines. The claim that the suppression shows a "fragile" design was rejected: the justification is sound.
- Recommendation: Optional small `SliceStreamBase`.

**D3. Benchmark temp-root boilerplate in five classes plus `FlatnessRunner`**
- File: `benchmarks/FileSorter.Benchmarks/MergeBenchmarks.cs:50`
- Evidence: A GUID temp root with create and recursive delete appears at `MergeBenchmarks.cs:50,93`, `PartitionedMergeBenchmarks.cs:47,111`, `RunGenerationBenchmarks.cs:45,63`, `SpillWorkBenchmarks.cs:47,63`, `FlatnessRunner.cs:60,85`. The tests project already has `Support/TempDirectory.cs`. About 5 lines each in non-production code.
- Recommendation: One benchmark-side scratch helper, or link `TempDirectory.cs`.

**D4. Tests hand-roll the temp directory instead of using the shared helper**
- File: `tests/FileSorter.Tests/Startup/TemporaryRunSetTests.cs:15`
- Evidence: `TemporaryRunSetTests.cs:15-24` and `tests/TestFileGenerator.Tests/OutputPlacementTests.cs:12-26` use a non-tolerant `Directory.Delete`. `Support/TempDirectory.cs:18-28` swallows `IOException` and `UnauthorizedAccessException` on purpose. Risk is small (cleanup runs after assertions). `TemporaryRunSetTests` passes a path that does not yet exist, so switching needs a quick check.
- Recommendation: Switch `TemporaryRunSetTests`. The generator tests are optional (separate project; would need a linked file).

### 5.3 SOLID-S

**S1. `Program.RunAsync` orchestrates too much**
- File: `src/FileSorter/Program.cs:55`
- Evidence: `RunAsync` (55-145) does preflight validation, Akka versus channel wiring and exit-code mapping. `RunPhasesAsync` (148-219) does placement, `GC.Collect` (192), merge-wait output (202-205) and partial-output cleanup. `DestinationReplaceFailedException` sits in `Program` (17), and `Program.cs:137` is a second exit-code mapper next to `ConsoleRun.Run`. Downgraded from high: the file is 225 lines, already delegates to many helpers and documents its ordering choices. The `Program.cs:137` catch deliberately maps to exit 3, which the generic IO mapping does not.
- Recommendation: Optional `SortCommand` mirroring `VerifyCommand`. Moving the merge-wait line into `MergeReporter` is a cheap, clear win. Tests drive `Program.RunAsync` directly, so any extraction changes test targets.

**S2. `OutputVerifier.ScanCoreAsync` is long and boolean-flag driven**
- File: `src/FileSorter/Verification/OutputVerifier.cs:113`
- Evidence: Four booleans (lines 99-115, called at 40-41) that are really two fixed profiles. A 113-229 body mixes refill, carry, order check, hashing and tail handling in a mutating closure. The EOF tail rule at 212-218 is copied from `ChunkReader.cs:149-158`, and `RunCursor.cs` also handles carry, so the rule exists in three places. Downgraded from medium: the verifier is deliberately standalone, and strategy classes would add more machinery than they remove.
- Recommendation: Extract only the shared EOF-tail helper (clearly worth doing). The `ScanProfile` record is optional.

**S3. `MergeExecutor` mixes strategy selection, buffer allocation, pass looping and file opening**
- File: `src/FileSorter/Merging/MergeExecutor.cs:59`
- Evidence: `ExecuteAsync` (59-134) validates, plans, chooses path, allocates buffers and runs the pass loop. `MergeGroupAsync` (177-239) opens files and verifies length. Six progress properties (37-57) are backed by several fields. About 240 lines, tightly coupled by shared buffers and progress state. Downgraded from medium.
- Recommendation: Optional split into a thin orchestrator and a `SequentialMultiPassMerge`, following the `PartitionedMerge` precedent. Moving `ComputeTotalBytesToWrite` beside `MergePlanner` is a reasonable minor step.

**S4. `PartitionedMerge.TryMergeAsync` is about 130 lines**
- File: `src/FileSorter/Merging/PartitionedMerge.cs:56`
- Evidence: Lines 56-187 cover sampling, decline logic, preallocation, worker fan-out, root-cause selection, verification, sparse clearing and run deletion. `PartitionStats` is rebuilt at 67, 77, 81 and 141. The flow is linear and well commented. Downgraded from medium.
- Recommendation: Extract `PreallocateOutput`, `VerifyWrites` and `FinishOutput`. Build stats once with a `with` expression.

### 5.4 Architecture / SOLID-D

**A1. `Startup` is a grab-bag namespace and creates a dependency cycle**
- Files: `src/FileSorter/Startup/MemoryPlan.cs:3`, `src/FileSorter/Startup/MemoryBudget.cs:1`
- Evidence: `Startup` (11 files) holds CLI parsing, `ConsoleRun`, `MemoryBudget`/`MemoryPlan`, `TemporaryRunSet`, `CapacityProbe`, `TempCapacity` and `ProgressReporter`. `Merging` (`MergeExecutor.cs:2`, `PartitionedMerge.cs:6`, `MergeReporter.cs:3`, `RunPlacement.cs:1`, `StagingFile.cs:1`), `RunGeneration` (`ChunkSpiller.cs:2`, `RunGenerationDriver.cs:2`) and `Verification` (`OutputVerifier.cs:3,31,34`, `VerifyCommand.cs:14,39`, `VerifyOptions.cs:25-47`) depend on it. `MemoryPlan.cs:3` depends back on `Merging` via `KWayMerge.RunHead` (line 42). `StagingFile.cs:15` reuses `TemporaryRunSet.RandomSuffix`. Everything is in one assembly, so there is no project-level cycle and no runtime impact. Reported independently by two reviewers and rated low by both verifiers.
- Recommendation: Split into `Cli/` (`CommandLine`, `SorterOptions`, `ExitCodes`, `ConsoleRun`), `Planning/` (`MemoryBudget`, `MemoryPlan`) and `Infrastructure/IO` (`TemporaryRunSet`, `StagingFile`, `RunPlacement`, `ProgressReporter`/`Describe`, the `UnbufferedStream` constant, a shared temp-naming helper). Keep dependencies one-way. Note that sizing the loser-tree slot from the real `RunHead` struct avoids a duplicated constant. Preserve that, for example by moving `RunHead` or its size to a shared layout type.

## 6. Prioritized action plan (top 10)

| # | Action | Findings | Effort | Value |
|---|---|---|---|---|
| 1 | Split `Startup` by responsibility and make the dependency direction one-way. Move `UnbufferedStream`, `ProgressReporter.Describe` and temp naming to a neutral IO location. | A1 | M | Highest: removes the only structural smell |
| 2 | Extract the shared EOF-tail rule into `LineCursor` or a helper used by `ChunkReader`, `RunCursor` and `OutputVerifier`. | S2 | S | Prevents three-way divergence |
| 3 | Add `RunCursorBuffers.Create(MemoryPlan)` and use it in both merge paths. | D1 | S | Single source for buffer pricing |
| 4 | Move the merge-wait console lines into `MergeReporter`, and the `DestinationReplaceFailedException` mapping into the `ConsoleRun` policy. | S1 | S | Trims `Program` |
| 5 | Split `CapacityProbe` into `Preflight` and `VolumeCapacity`, throwing typed errors. This removes the CA2000 suppression. | K2 | M | Cohesion and naming |
| 6 | Break up `PartitionedMerge.TryMergeAsync` (`PreallocateOutput`, `VerifyWrites`, `FinishOutput`) and build stats once. | S4 | S | Readability |
| 7 | Switch `TemporaryRunSetTests` to `TempDirectory`. Add a shared scratch helper for benchmarks. | D4, D3 | S | Removes leftover TST-1 duplication |
| 8 | Move `NaiveReferenceSort` to `Support/`, add the missing `using`, and split `SortRoundTripTests` by concern. | K3 | S-M | Test navigability |
| 9 | Move generator option parsing and the staged write out of `Program` into `Generation/`. | K1 | M | Mirrors the sorter's split |
| 10 | Optional: `SliceStreamBase`, a `SequentialMultiPassMerge` split, and a `SortCommand` extraction. Defer unless the code is touched anyway. | D2, S3, S1 | M | Marginal: adds indirection |

## 7. Appendix: counts

| Area | Surviving findings | Dropped in verification |
|---|---|---|
| Architecture | 4 | 6 |
| LineFormat and Startup | 2 | 9 |
| RunGeneration | 0 | 10 |
| Merging | 4 | 8 |
| Tests, benchmarks, generator | 3 | 6 |
| **Total** | **13** | **39** |

Total candidates: 52. Verification rate: 25% survived.

Surviving findings by severity: critical 0, high 0, medium 0, low 13. In the report, the architecture and linefmt-startup `Startup` findings are merged into A1, so 12 distinct entries appear in section 5. Several were reported at medium or high and downgraded to low by the verifier (S1, S2, S3, S4, D3, D4, D1, D2, A1).

By principle: KISS 3 (K1-K3, including K2), DRY 4, SOLID-S 4, Architecture / SOLID-D 1 (reported twice, merged).
