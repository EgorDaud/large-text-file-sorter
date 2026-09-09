using FileSorter.Merging;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Startup;

// Every expected figure below is written out longhand -- computed by hand from the
// stated inputs -- rather than by re-running the closed-form expression Calculate
// itself uses, so that a defect in that expression cannot hide behind an assertion
// that re-derives it.
//
// The constants the derivations are built on: OutputBufferSize 1_048_576 (1 MiB),
// SpillBufferSize 65_536 (64 KiB), LineDescriptor 32 bytes (LineDescriptorTests pins
// that figure independently via Unsafe.SizeOf), PoolCapacity = parallelism + 2, the
// read-ahead window floor maxLineLength + 2, LoserTreeBytesPerFanInSlot 44 and
// PartitionOffsetEntrySize 8.
//
// Wherever a derivation multiplies a read-ahead window by 2, that is because a run
// cursor holds two equal-size windows so a fill for one can be in flight while the
// other is scanned, and both are charged since neither is allocated per fill --
// unlike the descriptor term next to it, which is not doubled because descriptors are
// only ever produced for the window being delivered.
//
// OutputBufferSize is large enough to be the binding term in MinimumViableBudget at
// every (maxLine, assumedMean) pair this file exercises at parallelism 1 and at most
// of the small parallelisms, so several cases here run at budgets in the low
// megabytes purely to stay viable.
public sealed class MemoryBudgetTests
{
    [Fact]
    [Trait("Case", "MB-01")]
    public void Produces_a_viable_plan_for_a_normal_budget()
    {
        // budget=2_097_152 (2 MiB), parallelism=4, maxLine=1024, assumedMean=64.
        // spillRoom(4) = 2_097_152 - 4*65_536 = 1_835_008
        // denom(4) = (4+2)*(64+32)+64 = 6*96+64 = 640 -- the leading factor is
        // PoolCapacity, parallelism + 2, because the reader holds two slots of its own
        // (one filling ahead of the one being parsed).
        // chunkSize = floor(1_835_008*64 / 640) = floor(117_440_512/640) = 183_500
        // descriptorCapacity = floor(183_500/64) = 2867
        // bytesPerSlot = 183_500 + 2867*32 = 183_500+91_744 = 275_244
        // WorstCasePhaseOneBytes = 6*275_244 + 183_500 + 4*65_536 = 1_651_464+183_500+262_144 = 2_097_108 (<= 2_097_152)
        //
        // Phase two: floorR = 1024+2 = 1026 (a legal carry can be maxLineLength + 1
        // bytes when a \r\n line's CR lands at a fill boundary -- see
        // LineCursor.TryReadLine -- so the read-ahead window's floor sits one byte
        // above that); floorDescriptorCapacity = floor(1026/64) = 16;
        // floorBytesPerRunCursor = 2*1026 + 16*32 = 2052+512 = 2564.
        //
        // rawFanIn divides by floorBytesPerFanInSlot, which is floorBytesPerRunCursor
        // plus LoserTreeBytesPerFanInSlot (44): one more fan-in slot costs one more
        // RunHead and one more int in the loser tree KWayMerge.MergeAsync builds.
        // fanInRoom = 2_097_152 - 1_048_576 = 1_048_576; floorBytesPerFanInSlot =
        // 2564 + 44 = 2608; rawFanIn = floor(1_048_576/2608) = 402 (>= 2, viable).
        // rawWindow = floor(1_048_576*64 / (2048*(2*64+32))) = floor(67_108_864/327_680) = 204, below floorR, so
        // the window is clamped up to its floor rather than grown -- this budget cannot even afford
        // MaxMergeFanIn (2048) cursors at the floor window (402 < 2048), so there is no room left over
        // to grow the window into.
        //
        // MergeFanIn itself is solved separately, at MergeParallelism 1 (this budget
        // never reaches a second worker): mergeRoom = RoomFor(1) = fanInRoom (a single
        // worker writes no partition offset table) = 1_048_576;
        // cursorAndMetadataCostAcrossWorkers = 2564 + 44 = 2608 (the same
        // floorBytesPerFanInSlot, since the window sits at its floor); MergeFanIn
        // = min(2048, floor(1_048_576/2608)) = 402 -- exactly rawFanIn, as it must be
        // at one worker.
        // MergeMetadataBytes = 1*402*44 = 17_688 (one loser tree, MergeParallelism 1,
        // so no partition-offset term).
        // WorstCasePhaseTwoBytes = 402*2564 + 1_048_576 + 17_688
        //                        = 1_030_728 + 1_048_576 + 17_688 = 2_096_992 (<= 2_097_152)
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 2_097_152, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(183_500, plan.ChunkSize);
        Assert.Equal(2867, plan.DescriptorCapacity);
        Assert.Equal(4, plan.Parallelism);
        Assert.Equal(402, plan.MergeFanIn);
        Assert.Equal(1026, plan.ReadAheadBufferSize);
        Assert.Equal(16, plan.ReadAheadDescriptorCapacity);
        Assert.Equal(1_048_576, plan.OutputBufferSize);
        Assert.Equal(65_536, plan.SpillBufferSize);
        Assert.Equal(2_097_108, plan.WorstCasePhaseOneBytes);
        Assert.Equal(17_688, plan.MergeMetadataBytes);
        Assert.Equal(2_096_992, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 2_097_152);
        Assert.True(plan.WorstCasePhaseTwoBytes <= 2_097_152);
        Assert.True(plan.MergeFanIn >= 2);
    }

    [Fact]
    [Trait("Case", "MB-10")]
    public void Grows_the_read_ahead_window_once_the_fan_in_ceiling_leaves_the_budget_idle()
    {
        // budget=4 GiB, parallelism=16, maxLine=64 KiB (65_536), assumedMean=64. The
        // assumed mean here is deliberately not the shipped default (40): it is the
        // value this test's hand-derived arithmetic below is worked out against. Phase
        // one is not asserted here; this case is about phase two.
        //
        // Phase two: floorR = 65_536+2 = 65_538 (the read-ahead floor is one byte above
        // maxLineLength + 1, the largest carry LineCursor can legally hand back --
        // see LineCursorTests and ChunkReader's own Reserve); floorDescriptorCapacity =
        // floor(65_538/64) = 1024; floorBytesPerRunCursor = 2*65_538 + 1024*32 =
        // 131_076+32_768 = 163_844.
        // fanInRoom = 4*1024*1024*1024 - 1_048_576 = 4_293_918_720 -- ONE output buffer,
        // because this is the viability yardstick, and that yardstick and
        // MinimumViableBudget are both stated at one merge worker.
        //
        // floorBytesPerFanInSlot = floorBytesPerRunCursor (163_844) +
        // LoserTreeBytesPerFanInSlot (44) = 163_888.
        // rawFanIn = floor(4_293_918_720/163_888) = 26_202 (>= 2, viable), nowhere near
        // binding.
        //
        // Merge workers: candidates run down from min(parallelism, MaxMergeParallelism)
        // = min(16, 8) = 8, and n is accepted when the window it solves for still clears
        // the floor on its own. room(n) reserves, once n > 1, the partition's own offset
        // table at the worst-case run count (MaxMergeFanIn * (n+1) entries of 8 bytes),
        // and the window solve separately reserves n * MaxMergeFanIn loser trees:
        //   room(8)        = 4_294_967_296 - 8*1_048_576 - 2048*9*8
        //                   = 4_294_967_296 - 8_388_608 - 147_456 = 4_286_431_232
        //   adjustedRoom(8) = 4_286_431_232 - 8*2048*44 = 4_286_431_232 - 720_896
        //                    = 4_285_710_336
        //   denom(8)       = 8*2048*(2*64+32) = 8*2048*160 = 2_621_440
        //   rawWindow(8)   = floor(4_285_710_336*64 / 2_621_440)
        //                  = floor(274_285_461_504 / 2_621_440) = 104_631
        // 104_631 >= floorR (65_538), so 8 workers are accepted at the first candidate
        // and MergeParallelism is 8.
        //
        // rawFanIn (26_202) is far above MaxMergeFanIn (2048), so unlike the
        // small-budget cases this one affords more than 2048 cursors per worker at the
        // floor window, and the window is grown rather than clamped to its floor:
        // window = 104_631, under MaxReadAheadBufferSize (4_194_304), so that clamp does
        // not bind either.
        // Guard: descriptorCapacity(window) = floor(104_631/64) = 1_634;
        // bytesPerRunCursor(window) = 2*104_631 + 1_634*32 = 209_262+52_288 = 261_550;
        // 8*2048*(261_550+44) = 4_285_956_096, which is <= mergeRoom (RoomFor(8) =
        // 4_286_431_232; the loser trees are priced per fan-in slot below rather than
        // deducted here as well), so the guard loop runs zero times, the same way the
        // phase-one verification loop is expected to.
        // MergeFanIn = min(2048, floor(4_286_431_232/(8*(261_550+44)))) =
        //              min(2048, floor(4_286_431_232/2_092_752)) = min(2048, 2048) = 2048.
        // MergeMetadataBytes = 8*2048*44 + 2048*9*8 = 720_896 + 147_456 = 868_352.
        // WorstCasePhaseTwoBytes = 8*2048*261_550 + 8*1_048_576 + 868_352
        //                        = 4_285_235_200 + 8_388_608 + 868_352
        //                        = 4_294_492_160 -- 475_136 bytes under 4 GiB.
        //
        // The window above is the exact integer result of the stated closed form; a hand
        // estimate using floating-point division rounds to a slightly different figure,
        // and it is the integer arithmetic that this pins.
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 16, maxLineLength: 65_536, assumedMeanLineLength: 64);

        Assert.Equal(2048, plan.MergeFanIn);
        Assert.Equal(8, plan.MergeParallelism);
        Assert.Equal(104_631, plan.ReadAheadBufferSize);
        Assert.Equal(1_634, plan.ReadAheadDescriptorCapacity);
        Assert.Equal(868_352, plan.MergeMetadataBytes);
        Assert.Equal(4_294_492_160, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseTwoBytes <= fourGiB);

        // A fixed 65_537-byte window with a fan-in capped at 64 would need a two-pass
        // merge at this budget; deriving both from the budget single-passes any run
        // count up to 2048.
        Assert.True(plan.ReadAheadBufferSize > 65_536 + 1);
        Assert.True(plan.MergeFanIn > 64);
    }

    [Fact]
    [Trait("Case", "MB-11")]
    public void Clamps_the_merge_worker_count_at_the_measured_ceiling_rather_than_at_the_window_floor()
    {
        // The shipped configuration: 4 GiB, parallelism 16, --max-line 64 KiB, assumed
        // mean 32 (Program.AssumedMeanLineLength). MaxMergeParallelism (8) is what binds
        // here, and the point of this case is that the window floor alone would have
        // admitted more -- so the ceiling is doing real work rather than restating a
        // constraint the arithmetic already imposes.
        //
        // floorR = 65_538. room(n) reserves the partition's own offset table at the
        // worst-case run count once n > 1 (MaxMergeFanIn*(n+1)*8), and the window solve
        // separately reserves n*MaxMergeFanIn loser trees:
        //   room(8)        = 4_294_967_296 - 8*1_048_576 - 2048*9*8
        //                   = 4_294_967_296 - 8_388_608 - 147_456 = 4_286_431_232
        //   adjustedRoom(8) = 4_286_431_232 - 8*2048*44 = 4_285_710_336
        //   denom(8)       = 8*2048*(2*32+32) = 8*2048*96 = 1_572_864
        //   rawWindow(8)   = floor(4_285_710_336*32/1_572_864)
        //                  = floor(137_142_730_752/1_572_864) = 87_193 -- about 85 KiB,
        // which is where a fixed-window sweep at eight workers measured fastest on the
        // reference machine.
        // descriptorCapacity = floor(87_193/32) = 2_724;
        // bytesPerRunCursor = 2*87_193 + 2_724*32 = 174_386+87_168 = 261_554;
        // 8*(261_554+44) = 2_092_784; mergeRoom = RoomFor(8) = 4_286_431_232 (the
        // partition-offset term only: the loser trees are priced per fan-in slot in the
        // division below rather than reserved twice); MergeFanIn =
        // min(2048, floor(4_286_431_232/2_092_784)) = min(2048, 2048) = 2048.
        // MergeMetadataBytes = 8*2048*44 + 2048*9*8 = 720_896 + 147_456 = 868_352.
        // WorstCasePhaseTwoBytes = 8*2048*261_554 + 8*1_048_576 + 868_352
        //                        = 4_285_300_736 + 8_388_608 + 868_352
        //                        = 4_294_557_696 -- 409_600 bytes under 4 GiB.
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 16, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(8, plan.MergeParallelism);
        Assert.Equal(87_193, plan.ReadAheadBufferSize);
        Assert.Equal(2_724, plan.ReadAheadDescriptorCapacity);
        Assert.Equal(2048, plan.MergeFanIn);
        Assert.Equal(868_352, plan.MergeMetadataBytes);
        Assert.Equal(4_294_557_696, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseTwoBytes <= fourGiB);

        // The window floor admits n workers while the budget is at least roughly
        // 403_714_048n, so at 4 GiB it would have allowed 9 and 10 as well: the ceiling
        // is the binding rule. Asserted as the shape of the clamp rather than by
        // reaching into the constant: raising `parallelism` past 8 changes nothing,
        // which is only true if something other than `parallelism` is what stopped it.
        MemoryPlan atThirtyTwo = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 32, maxLineLength: 65_536, assumedMeanLineLength: 32);
        Assert.Equal(8, atThirtyTwo.MergeParallelism);
    }

    [Fact]
    [Trait("Case", "MB-11")]
    public void Clamps_the_merge_worker_count_by_the_window_floor_when_the_budget_binds_first()
    {
        // The other side of the clamp: a budget that cannot shave the window eight
        // ways and still clear maxLineLength + 2. 1.5 GiB is 1_610_612_736; this is
        // three workers, chosen by the floor rather than by the ceiling (8) or by
        // parallelism (16).
        //
        //   room(3)        = 1_610_612_736 - 3*1_048_576 - 2048*4*8
        //                   = 1_610_612_736 - 3_145_728 - 65_536 = 1_607_401_472
        //   adjustedRoom(3) = 1_607_401_472 - 3*2048*44 = 1_607_131_136
        //   denom(3)       = 3*2048*96 = 589_824
        //   rawWindow(3)   = floor(1_607_131_136*32/589_824)
        //                  = floor(51_428_196_352/589_824) = 87_192 -- above floorR
        // (65_538), so three is accepted. Four would solve a window below the 65_538
        // floor at this budget, which is what makes this a boundary case rather than a
        // comfortable one.
        const long budget = 3L * 512 * 1024 * 1024;   // 1.5 GiB
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: budget, parallelism: 16, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(3, plan.MergeParallelism);
        Assert.True(plan.ReadAheadBufferSize >= 65_538);
        Assert.True(plan.WorstCasePhaseTwoBytes <= budget);
        Assert.True(plan.MergeFanIn >= 2);
    }

    [Fact]
    [Trait("Case", "MB-11")]
    public void Never_gives_the_merge_more_workers_than_the_parallelism_it_was_asked_for()
    {
        // The third clamp: the operator's own parallelism. A sort told to use three
        // cores must not start eight merge workers on them. The worker count is
        // min(parallelism, MaxMergeParallelism, whatever the window floor admits), and
        // this is the case where the first term is the smallest.
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 3, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(3, plan.MergeParallelism);

        MemoryPlan single = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 1, maxLineLength: 65_536, assumedMeanLineLength: 32);
        Assert.Equal(1, single.MergeParallelism);
    }

    [Fact]
    [Trait("Case", "MB-12")]
    public void Falls_back_to_one_merge_worker_at_a_budget_too_small_to_grow_the_window()
    {
        // Every small-budget case in this file is a one-worker case, and this states why
        // rather than leaving it as an accident of the numbers: at 2 MiB with
        // parallelism 4, maxLine 1024 and assumedMean 64, rawWindow(1) is 204, far
        // below floorR (1026), so the budget cannot even afford MaxMergeFanIn cursors
        // at the floor window for ONE worker. Admitting a second there would halve the
        // fan-in instead of the window -- trading merge passes for workers on a run
        // count the plan cannot know -- so the rule declines, and phase two costs one
        // output buffer, 2_096_992 bytes in total.
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 2_097_152, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(1, plan.MergeParallelism);
        Assert.Equal(2_096_992, plan.WorstCasePhaseTwoBytes);

        // The same (--max-line, assumed mean) pair at parallelism 4 and at this pair's
        // OWN minimum viable budget, so the worker-count loop genuinely runs (its
        // condition is `n > 1`, which at parallelism 1 would be false on the first
        // check, making the assertion vacuous there) and the window rule is what
        // decides:
        //   floorR = 1_026; floorBytesPerRunCursor = 2*1_026 + 16*32 = 2_564.
        //
        // The reference plan MinimumViableBudget builds (MergeFanIn = MinMergeFanIn = 2,
        // MergeParallelism = 1) prices its own loser tree: MergeMetadataBytes = 1*2*44 =
        // 88, with no partition-offset term at one worker, so
        // MinimumViableBudget(4, 1024, 64) = 2*2_564 + 1_048_576 + 88 = 1_053_792.
        //   room(n) = 1_053_792 - n*1_048_576 - (n>1: 2048*(n+1)*8) is already negative
        //   for n = 2, 3 and 4, so WindowFor(n) is 0 -- nowhere near floorR -- for
        //   every n > 1, and the loop falls through to 1 without a single candidate
        //   coming close.
        //   room(1) = 5_216; adjustedRoom(1) (less 1*2048*44 of loser-tree metadata)
        //   = 5_216 - 90_112 = negative, so WindowFor(1) is 0 too -- rawWindow is
        //   clamped up to floorR (1_026); descriptorCapacity = floor(1_026/64) = 16;
        //   bytesPerRunCursor = 2*1_026 + 16*32 = 2_564; mergeRoom = RoomFor(1) =
        //   5_216 (the loser trees are priced per fan-in slot in the division that
        //   follows, not deducted here as a lump sum); MergeFanIn =
        //   min(2048, floor(5_216/(2_564+44))) = min(2048, floor(5_216/2_608)) = 2 --
        //   exactly MinMergeFanIn, since this budget sits right at the bare phase-two
        //   floor by construction.
        //   MergeMetadataBytes = 1*2*44 = 88.
        //   WorstCasePhaseTwoBytes = 1*2*2_564 + 1*1_048_576 + 88 = 1_053_792, exactly
        //   the minimum (this reference plan IS the minimum's own construction).
        long minimumAtP4 = MemoryBudget.MinimumViableBudget(4, 1024, 64);
        Assert.Equal(1_053_792, minimumAtP4);
        MemoryPlan atP4Minimum = MemoryBudget.Calculate(
            budgetBytes: minimumAtP4, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);
        Assert.Equal(1, atP4Minimum.MergeParallelism);
        Assert.Equal(2, atP4Minimum.MergeFanIn);
        Assert.Equal(1_026, atP4Minimum.ReadAheadBufferSize);
        Assert.Equal(88, atP4Minimum.MergeMetadataBytes);
        Assert.Equal(1_053_792, atP4Minimum.WorstCasePhaseTwoBytes);
        Assert.True(atP4Minimum.WorstCasePhaseTwoBytes <= minimumAtP4);

        // The guarantee MinimumViableBudget gives is that the budget it reports is
        // viable -- both phases fit, chosen by arithmetic that is independent of the
        // eventual worker count -- not that the plan built at that budget never
        // partitions. This case is the counterexample to the stronger reading: at
        // parallelism 48, maxLine 64, assumedMean 32, the phase-one spill buffers push
        // the minimum viable budget high enough that the plan built at it affords TWO
        // merge workers. It is the smallest parallelism that does so; below it, room(2)
        // falls under its own window floor once the merge metadata is priced in.
        //   floorR = 66; floorBytesPerRunCursor = 2*66 + 2*32 = 196 (floorDescriptorCapacity
        //   = floor(66/32) = 2).
        //   n=8..3: room(n) = 3_152_495 - n*1_048_576 - 2048*(n+1)*8 is negative for
        //   every one of these (even n=3: room(3) = 3_152_495 - 3_145_728 - 65_536 =
        //   -58_769) -- all fail.
        //   n=2: room(2) = 3_152_495 - 2*1_048_576 - 2048*3*8
        //                 = 3_152_495 - 2_097_152 - 49_152 = 1_006_191;
        //   adjustedRoom(2) (less 2*2048*44 of loser-tree metadata) = 1_006_191 -
        //   180_224 = 825_967; window(2) = floor(825_967*32/(2*2048*96)) =
        //   floor(26_430_944/393_216) = 67 -- at or above floorR (66), so 2 is accepted
        //   and the loop stops there.
        //   descriptorCapacity = floor(67/32) = 2; bytesPerRunCursor = 2*67 + 2*32 = 198;
        //   mergeRoom = RoomFor(2) = 1_006_191 (the partition-offset term only; the
        //   loser trees are priced per fan-in slot below); MergeFanIn =
        //   min(2048, floor(1_006_191/(2*(198+44)))) = min(2048, floor(1_006_191/484))
        //   = min(2048, 2_079) = 2_048.
        //   MergeMetadataBytes = 2*2048*44 + 2048*3*8 = 180_224 + 49_152 = 229_376.
        //   WorstCasePhaseTwoBytes = 2*2_048*198 + 2*1_048_576 + 229_376
        //                          = 811_008 + 2_097_152 + 229_376
        //                          = 3_137_536, comfortably inside 3_152_495.
        long minimumAtP48 = MemoryBudget.MinimumViableBudget(48, 64, 32);
        Assert.Equal(3_152_495, minimumAtP48);
        MemoryPlan atP48Minimum = MemoryBudget.Calculate(
            budgetBytes: minimumAtP48, parallelism: 48, maxLineLength: 64, assumedMeanLineLength: 32);
        Assert.Equal(2, atP48Minimum.MergeParallelism);
        Assert.Equal(67, atP48Minimum.ReadAheadBufferSize);
        Assert.Equal(2048, atP48Minimum.MergeFanIn);
        Assert.Equal(229_376, atP48Minimum.MergeMetadataBytes);
        Assert.Equal(3_137_536, atP48Minimum.WorstCasePhaseTwoBytes);
        Assert.True(atP48Minimum.WorstCasePhaseTwoBytes <= minimumAtP48);
    }

    [Fact]
    [Trait("Case", "MB-13")]
    public void Keeps_phase_two_inside_the_budget_and_the_worker_count_monotone_across_the_range_that_adds_workers()
    {
        // The chunk-size monotonicity sweep further down runs at a configuration that
        // never leaves one merge worker, by design (it pins the window's monotonicity,
        // and a worker transition halves the per-worker window on purpose). This is the
        // sweep for the dimension it cannot cover: the shipped (--max-line, assumed
        // mean) pair across the budgets where the worker count climbs 1 -> 8. Those
        // transitions land at roughly
        // 314_572_800 / 808_452_096 / 1_212_153_856 / 1_615_855_616 / 2_019_557_376 /
        // 2_423_259_136 / 2_826_960_896 / 3_230_662_656 bytes for workers 1 through 8,
        // so a step of 23 MiB puts several sample points inside each regime and never
        // straddles a transition invisibly.
        //
        // The two claims are the ones a worker count can break: phase two must stay
        // inside the budget at every point (the extra (n-1) output buffers are taken out
        // of the budget BEFORE the window is solved, so this is where a sign error in
        // that subtraction would show), and the worker count must never fall as the
        // budget rises -- a plan that gave a larger budget fewer workers would be the
        // same non-monotonicity the sweeps below exclude for chunk size and fan-in.
        const int parallelism = 16;
        const int maxLineLength = 65_536;
        const int assumedMeanLineLength = 32;
        const long first = 300L * 1024 * 1024;
        const long last = 4L * 1024 * 1024 * 1024 + (256L * 1024 * 1024);
        const long step = 23L * 1024 * 1024;

        int previousWorkers = 0;
        int highestWorkers = 0;
        for (long budget = first; budget <= last; budget += step)
        {
            MemoryPlan plan = MemoryBudget.Calculate(budget, parallelism, maxLineLength, assumedMeanLineLength);

            Assert.True(
                plan.WorstCasePhaseTwoBytes <= budget,
                $"Phase two's worst case ({plan.WorstCasePhaseTwoBytes} bytes) exceeded the budget ({budget}) at {plan.MergeParallelism} workers.");
            Assert.True(plan.MergeFanIn >= 2);
            Assert.True(plan.MergeParallelism >= 1);
            Assert.True(plan.MergeParallelism <= parallelism);
            Assert.True(
                plan.ReadAheadBufferSize >= maxLineLength + 2,
                $"The per-worker window ({plan.ReadAheadBufferSize}) fell below its floor at {plan.MergeParallelism} workers.");
            Assert.True(
                plan.MergeParallelism >= previousWorkers,
                $"The merge worker count fell from {previousWorkers} to {plan.MergeParallelism} as the budget rose to {budget}.");

            previousWorkers = plan.MergeParallelism;
            highestWorkers = Math.Max(highestWorkers, plan.MergeParallelism);
        }

        // The sweep has to actually cross the transitions it claims to cover, or it is
        // asserting monotonicity of a constant.
        Assert.Equal(8, highestWorkers);
    }

    [Fact]
    [Trait("Case", "MB-02")]
    public void Fails_clearly_when_the_budget_is_one_byte_below_the_minimum_viable_budget()
    {
        // maxLine=1024, assumedMean=64, parallelism=1.
        // Phase two's reference cost: floorReadAheadBufferSize = maxLineLength + 2 =
        // 1026, floorDescriptorCapacity = floor(1026/64) = 16, bytesPerRunCursor =
        // 2*1026 + 16*32 = 2564. The reference plan also prices its own loser tree
        // (1 worker * 2 (MinMergeFanIn) * LoserTreeBytesPerFanInSlot (44) = 88, no
        // partition-offset term at one worker):
        // reference.WorstCasePhaseTwoBytes = 2*2564 + 1_048_576 + 88 = 1_053_792, of
        // which OutputBufferSize is almost all.
        //
        // Phase one's own minimum uses requiredChunkSize = maxLineLength + 3 = 1027
        // (the fresh-fill region past Reserve, itself maxLineLength + 2, needs a byte
        // of its own on top of the reserve) and PoolCapacity's denominator factor of
        // (parallelism+2):
        // denom(1) = 3*(64+32)+64 = 352
        // budgetAboveSpillBuffer = ceil(1027*352/64) = ceil(5_648.5) = 5_649
        // phaseOneMinimum = 65_536 + 5_649 = 71_185
        //
        // minimum viable budget = max(71_185, 1_053_792) = 1_053_792, so phase two is
        // the binding half at this configuration.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(
                budgetBytes: 1_053_791, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64));

        Assert.Equal("budgetBytes", ex.ParamName);
        Assert.Contains("1053792", ex.Message);
        Assert.Contains("1053791", ex.Message);
        Assert.Contains("1", ex.Message); // parallelism given
    }

    [Fact]
    [Trait("Case", "MB-02")]
    public void Succeeds_at_exactly_the_minimum_viable_budget_with_a_fan_in_of_exactly_two()
    {
        // One byte above the failing case: budget=1_053_792, the minimum derived in the
        // test above. Phase two is the tight constraint at this boundary: mergeRoom =
        // RoomFor(1) = 1_053_792-1_048_576 = 5_216 (no partition-offset term at one
        // worker); cursorAndMetadataCost = 2564+44 = 2608 (the cursor plus its
        // loser-tree slot); MergeFanIn = floor(5_216/2608) = 2 exactly -- the window
        // stays at its floor, nowhere near the 2048 ceiling, so ReadAheadBufferSize and
        // ReadAheadDescriptorCapacity match the reference plan and
        // WorstCasePhaseTwoBytes reproduces the reference figure exactly: 1_053_792.
        //
        // Phase one is comfortably inside the budget at this size:
        // spillRoom(1) = 1_053_792-65_536 = 988_256; denom(1) = 352;
        // chunkSize = floor(988_256*64/352) = floor(63_248_384/352) = 179_682
        // descriptorCapacity = floor(179_682/64) = 2807; bytesPerSlot = 179_682+2807*32 = 269_506
        // WorstCasePhaseOneBytes = 3*269_506 + 179_682 + 1*65_536 = 808_518+179_682+65_536 = 1_053_736 (<= 1_053_792)
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_053_792, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(2, plan.MergeFanIn); // never below two, and not above it here either
        Assert.True(plan.ChunkSize > 1024);
        Assert.Equal(179_682, plan.ChunkSize);
        Assert.Equal(1_053_736, plan.WorstCasePhaseOneBytes);
        Assert.Equal(88, plan.MergeMetadataBytes);
        Assert.Equal(1_053_792, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_053_792);
        Assert.True(plan.WorstCasePhaseTwoBytes <= 1_053_792);
    }

    [Theory]
    [Trait("Case", "MB-02")]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    public void Rejects_a_max_line_length_too_close_to_int_MaxValue_for_this_classs_own_floors_to_fit(int maxLineLength)
    {
        // MemoryBudget adds small, fixed offsets (at most +3) to maxLineLength while
        // computing buffer floors. In plain int arithmetic that addition wraps negative
        // for a maxLineLength this close to int.MaxValue, and the diagnostic then quotes
        // a negative byte figure at the operator. --max-line's CLI ceiling
        // (int.MaxValue - 16) keeps an operator from supplying a value this large; the
        // guard inside MemoryBudget is the defence in depth for callers that bypass the
        // CLI, which is how MinimumViableBudget is called here.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.MinimumViableBudget(parallelism: 1, maxLineLength, assumedMeanLineLength: 64));

        Assert.Equal("maxLineLength", ex.ParamName);
    }

    [Fact]
    [Trait("Case", "MB-02")]
    public void Computes_a_positive_minimum_viable_budget_for_a_max_line_length_just_below_the_overflow_guard()
    {
        // The other side of the overflow guard: a maxLineLength below the guard
        // (int.MaxValue - 3), still above what --max-line's CLI ceiling admits but
        // reachable by a direct caller, must produce a sane positive figure rather than
        // a wrapped negative one.
        long minimum = MemoryBudget.MinimumViableBudget(
            parallelism: 1, maxLineLength: int.MaxValue - 20, assumedMeanLineLength: 64);

        Assert.True(minimum > 0);
    }

    [Theory]
    [Trait("Case", "MB-02")]
    [InlineData(0, 1024, 64)]   // non-positive parallelism
    [InlineData(4, 0, 64)]      // non-positive maxLineLength
    public void Rejects_non_positive_parallelism_or_max_line_length(int parallelism, int maxLineLength, int assumedMean)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(1_000_000, parallelism, maxLineLength, assumedMean));
    }

    [Theory]
    [Trait("Case", "MB-02")]
    [InlineData(0)]     // non-positive
    [InlineData(2000)]  // greater than maxLineLength (1024)
    public void Rejects_an_assumed_mean_line_length_that_is_non_positive_or_exceeds_the_maximum(int assumedMean)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(1_000_000, parallelism: 4, maxLineLength: 1024, assumedMean));
    }

    [Fact]
    [Trait("Case", "MB-03")]
    public void Handles_a_degree_of_parallelism_of_one()
    {
        // budget=1_100_000 (a comfortable margin over MinimumViableBudget for this
        // triple, 1_053_792), parallelism=1, maxLine=1024, assumedMean=64.
        // spillRoom(1) = 1_100_000 - 65_536 = 1_034_464
        // denom(1) = 3*(64+32)+64 = 352 (the leading factor is PoolCapacity,
        // parallelism + 2)
        // chunkSize = floor(1_034_464*64/352) = floor(66_205_696/352) = 188_084
        // descriptorCapacity = floor(188_084/64) = 2938
        // bytesPerSlot = 188_084 + 2938*32 = 282_100
        // WorstCasePhaseOneBytes = 3*282_100 + 188_084 + 1*65_536 = 846_300+188_084+65_536 = 1_099_920 (<= 1_100_000)
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_100_000, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(1, plan.Parallelism);
        Assert.Equal(188_084, plan.ChunkSize);
        Assert.Equal(2938, plan.DescriptorCapacity);
        Assert.Equal(1_099_920, plan.WorstCasePhaseOneBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_100_000);
    }

    [Fact]
    [Trait("Case", "MB-04")]
    public void Fails_clearly_when_the_requested_parallelism_is_more_than_the_budget_can_support()
    {
        // The calculator fails rather than silently shrinking the chunk and reducing
        // parallelism to fit: shrinking is what makes the plan non-monotonic in the
        // budget, which the sweep further down exists to exclude. At maxLine=1024,
        // assumedMean=64, a parallelism of 1000 needs 1000 spill buffers of 64 KiB
        // each, over 65 MiB before one chunk byte is budgeted, so 500_000 cannot
        // support it.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(
                budgetBytes: 500_000, parallelism: 1000, maxLineLength: 1024, assumedMeanLineLength: 64));

        // The diagnostic has to name what the operator typed and the figure that would
        // work, or it tells them nothing they can act on.
        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("500000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("minimum viable budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Case", "MB-04")]
    public void Honours_the_requested_parallelism_exactly_when_the_budget_supports_it()
    {
        // budget=1_100_000 (clear of this triple's MinimumViableBudget, 1_053_792),
        // parallelism=4, maxLine=1024, assumedMean=64.
        // spillRoom(4) = 1_100_000 - 4*65_536 = 837_856
        // denom(4) = 6*(64+32)+64 = 640 (the leading factor is PoolCapacity,
        // parallelism + 2)
        // chunkSize = floor(837_856*64/640) = floor(53_622_784/640) = 83_785
        // descriptorCapacity = floor(83_785/64) = 1309
        // bytesPerSlot = 83_785 + 1309*32 = 125_673
        // WorstCasePhaseOneBytes = 6*125_673 + 83_785 + 4*65_536 = 754_038+83_785+262_144 = 1_099_967 (<= 1_100_000)
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_100_000, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(4, plan.Parallelism);   // never silently reduced
        Assert.Equal(83_785, plan.ChunkSize);
        Assert.Equal(1309, plan.DescriptorCapacity);
        Assert.Equal(1_099_967, plan.WorstCasePhaseOneBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_100_000);

        // The chunk-size floor is one byte stronger than the read-ahead buffer's: a
        // chunk's fresh-fill region is the fixed range [Reserve, ChunkSize), where
        // Reserve is maxLineLength + 2 (one byte above the maxLineLength + 1 a legal
        // carry can be, since a carried CR with no LF behind it yet does not count
        // against LineCursor's content bound), and that range is decided before the
        // carry ahead of it is known, so it must leave at least one byte of room there
        // regardless of how large the carry turns out to be -- ChunkSize >
        // maxLineLength + 2. A read-ahead buffer's floor is exactly maxLineLength + 2,
        // because RunCursor folds its carry in place, already knowing it, and so only
        // needs room for one more fresh byte once that carry is subtracted.
        // ChunkReader and RunCursor both reject an undersized buffer in their
        // constructors; this is the same invariant stated where the size is chosen.
        Assert.True(plan.ChunkSize > 1024 + 2);
        Assert.True(plan.ReadAheadBufferSize > 1024 + 1);
    }

    [Fact]
    [Trait("Case", "MB-05")]
    public void Accounts_for_the_output_buffer_when_sizing_the_fan_in_not_only_the_read_ahead_cost()
    {
        // maxLine=1024, assumedMean=64: readAheadBufferSize=1026, readAheadDescriptorCapacity=16,
        // bytesPerRunCursor = 2*1026+16*32 = 2564. The budget is chosen so that a fan-in
        // of exactly 3 is the largest that fits once the output buffer and one loser
        // tree per fan-in slot (LoserTreeBytesPerFanInSlot, 44) are subtracted first:
        // budget = 1_048_576 + 3*(2564+44) = 1_048_576 + 3*2_608 = 1_056_400.
        // A fan-in computed by dividing the whole budget by bytesPerRunCursor without
        // ever subtracting the output buffer or the loser tree would read
        // floor(1_056_400/2564) = 411, which this plan must not produce.
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_056_400, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(3, plan.MergeFanIn);
        Assert.NotEqual(411, plan.MergeFanIn);
        Assert.Equal(132, plan.MergeMetadataBytes); // 1*3*44, one worker, no partition-offset term
        Assert.Equal(1_056_400, plan.WorstCasePhaseTwoBytes); // exact: fan-in*(cursor+loserTree) + output == budget here
        Assert.True(plan.WorstCasePhaseTwoBytes <= 1_056_400);
    }

    [Fact]
    [Trait("Case", "MB-06")]
    public void Accounts_for_the_descriptor_array_overhead_at_a_short_assumed_mean_line_length()
    {
        // budget=1_150_000, parallelism=1, maxLine=64, assumedMean=8 (worst-case short
        // lines). The budget only has to clear MinimumViableBudget for this pair,
        // 1_049_352, almost all of which is the 1 MiB output buffer.
        // spillRoom(1) = 1_150_000 - 65_536 = 1_084_464
        // denom(1) = 3*(8+32)+8 = 128 (the leading factor is PoolCapacity,
        // parallelism + 2)
        // chunkSize = floor(1_084_464*8/128) = floor(8_675_712/128) = 67_779
        // descriptorCapacity = floor(67_779/8) = 8472
        // bytesPerSlot = 67_779 + 8472*32 = 338_883
        // WorstCasePhaseOneBytes = 3*338_883 + 67_779 + 1*65_536 = 1_016_649+67_779+65_536 = 1_149_964 (<= 1_150_000)
        //
        // A calculation that ignored descriptor overhead entirely would size the chunk
        // from (PoolCapacity+1)*chunkSize + spillRoom's own budget share <= budget alone:
        // 4*chunkSize <= 1_150_000, i.e. chunkSize <= 287_500 -- far larger than the
        // correct 67_779. The descriptor array is not a rounding error at this line
        // length.
        //
        // Phase one only -- this test asserts nothing about phase two, but for
        // completeness: floorR = 64+2 = 66, floorDescriptorCapacity = floor(66/8) = 8,
        // floorBytesPerRunCursor = 2*66+8*32 = 132+256 = 388,
        // fanInRoom = 1_150_000-1_048_576 = 101_424, rawFanIn = floor(101_424/388) = 261,
        // still comfortably >= 2, so this budget remains phase-two-viable and the
        // case still exercises phase one alone.
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_150_000, parallelism: 1, maxLineLength: 64, assumedMeanLineLength: 8);

        Assert.Equal(67_779, plan.ChunkSize);
        Assert.True(plan.ChunkSize < 287_500);
        Assert.Equal(1_149_964, plan.WorstCasePhaseOneBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_150_000);
    }

    [Fact]
    [Trait("Case", "MB-07")]
    public void Responds_monotonically_to_a_larger_budget_at_the_same_parallelism()
    {
        // Both calls use parallelism 1 (so no parallelism-shrinking is involved for
        // either budget), maxLine=1024, assumedMean=64, and the budgets are in a 1:2
        // ratio, so the comparison isolates the effect of the budget alone. Fan-in is
        // unaffected by SpillBufferSize (only the chunk size loop subtracts it).
        // floorBytesPerRunCursor = 2*1026+16*32 = 2564.
        // denom(1) = 3*(64+32)+64 = 352 (the leading factor is PoolCapacity,
        // parallelism + 2).
        // MergeFanIn at one worker divides fanInRoom by floorBytesPerRunCursor +
        // LoserTreeBytesPerFanInSlot (2564 + 44 = 2608).
        // A: spillRoom=1_150_000-65_536=1_084_464 -> chunkSize=floor(1_084_464*64/352)=floor(69_405_696/352)=197_175,
        //    fanInRoom=1_150_000-1_048_576=101_424, fanIn=floor(101_424/2608)=38
        // B: spillRoom=2_300_000-65_536=2_234_464 -> chunkSize=floor(2_234_464*64/352)=floor(143_005_696/352)=406_266,
        //    fanInRoom=2_300_000-1_048_576=1_251_424, fanIn=floor(1_251_424/2608)=479
        // Neither budget affords MaxMergeFanIn (2048) cursors at the floor window
        // (38 and 479 both well short of 2048), so there is no slack to grow the window
        // with: rawWindow for B is floor(1_251_424*64/327_680)=floor(80_091_136/327_680)=244, under the 1026 floor, so
        // ReadAheadBufferSize stays at its floor for both budgets.
        MemoryPlan smaller = MemoryBudget.Calculate(
            budgetBytes: 1_150_000, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);
        MemoryPlan larger = MemoryBudget.Calculate(
            budgetBytes: 2_300_000, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(197_175, smaller.ChunkSize);
        Assert.Equal(38, smaller.MergeFanIn);
        Assert.Equal(406_266, larger.ChunkSize);
        Assert.Equal(479, larger.MergeFanIn);

        Assert.True(larger.ChunkSize >= smaller.ChunkSize);
        Assert.True(larger.MergeFanIn >= smaller.MergeFanIn);
        Assert.True(larger.ReadAheadBufferSize >= smaller.ReadAheadBufferSize);
    }

    [Fact]
    [Trait("Case", "MB-07")]
    public void A_larger_budget_never_yields_a_smaller_chunk_across_the_range_that_used_to_cliff()
    {
        // The trap: a rule that takes the largest parallelism the budget can support
        // lets a budget increase that admits one more slot collapse the chunk. At
        // maxLine=1024, assumedMean=64, requested parallelism 8, a one-byte increase
        // took the chunk from 18_864 to 1_034 -- pinned just above its floor to buy a
        // slot. Parallelism is held at what was asked for, so nothing trades chunk size
        // away. A sweep rather than a pair, because any single pair can be chosen to
        // miss the boundary; the step is deliberately not a round number, so the sample
        // points do not align with any power-of-two boundary in the arithmetic.
        //
        // The range also covers the read-ahead window's own monotonicity by design
        // rather than by luck. At maxLine=1024, assumedMean=64 (floorR=1026, K=2048,
        // denom=K*(2*64+32)=K*160=327_680) the window first exceeds its floor once
        // fanInRoom*64 >= denom*(floorR+1) = 336_527_360, i.e. fanInRoom >= 5_258_240,
        // so once the budget passes 1_048_576 + 5_258_240 = 6_306_816. The first swept
        // budget at or above that crossing, at step 97 from 4_200_000, is 6_306_840,
        // so one pass over [4_200_000, 7_200_000] exercises the flat-at-the-floor
        // regime, the growing-window regime, and the crossing between them.
        const int parallelism = 8;
        const int maxLineLength = 1024;
        const int assumedMeanLineLength = 64;
        const long first = 4_200_000;
        const long last = 7_200_000;
        const long step = 97;

        MemoryPlan previous = MemoryBudget.Calculate(first, parallelism, maxLineLength, assumedMeanLineLength);
        for (long budget = first + step; budget <= last; budget += step)
        {
            MemoryPlan current = MemoryBudget.Calculate(budget, parallelism, maxLineLength, assumedMeanLineLength);

            Assert.True(
                current.ChunkSize >= previous.ChunkSize,
                $"Chunk size fell from {previous.ChunkSize} to {current.ChunkSize} as the budget rose to {budget}.");
            Assert.True(
                current.MergeFanIn >= previous.MergeFanIn,
                $"Fan-in fell from {previous.MergeFanIn} to {current.MergeFanIn} as the budget rose to {budget}.");
            Assert.True(
                current.ReadAheadBufferSize >= previous.ReadAheadBufferSize,
                $"Read-ahead window fell from {previous.ReadAheadBufferSize} to {current.ReadAheadBufferSize} as the budget rose to {budget}.");
            Assert.Equal(parallelism, current.Parallelism);

            // Calculate's own phase-two verification loop is provably a no-op, so this
            // sweep is where a defect in the window closed form would first show up:
            // as budget-dependent overshoot, rather than at one hand-picked point.
            Assert.True(
                current.WorstCasePhaseTwoBytes <= budget,
                $"Phase two's worst case ({current.WorstCasePhaseTwoBytes} bytes) exceeded the budget ({budget}).");
            Assert.True(current.MergeFanIn >= 2);

            previous = current;
        }
    }

    [Fact]
    [Trait("Case", "MB-08")]
    public void Produces_identical_plans_from_identical_inputs_across_repeated_calls()
    {
        // The stronger claim -- no ambient reads at all -- is structural: Calculate's
        // signature (long, int, int, int) -> MemoryPlan has nowhere to smuggle in
        // Environment.ProcessorCount, DriveInfo, or a clock, which is what actually
        // carries the guarantee. This test covers the testable half: identical inputs,
        // called repeatedly, must never disagree with themselves. The budget is 2 MiB
        // because MinimumViableBudget(4, 1024, 64) is 1_053_792, so 1 MiB is not viable
        // for this triple.
        MemoryPlan first = MemoryBudget.Calculate(2_097_152, 4, 1024, 64);

        for (int i = 0; i < 50; i++)
        {
            MemoryPlan repeat = MemoryBudget.Calculate(2_097_152, 4, 1024, 64);
            Assert.Equal(first, repeat);
        }
    }

    [Fact]
    [Trait("Case", "MB-09")]
    public void Passes_its_fan_in_to_the_real_planner_which_accepts_it_and_terminates_in_one_run()
    {
        // Generous, realistic budget -- not a boundary case -- so this exercises the
        // seam between MemoryBudget and MergePlanner under ordinary conditions:
        // budget=2_000_000, parallelism=4, maxLine=1024, assumedMean=64 yields
        // fanInRoom=2_000_000-1_048_576=951_424,
        // floorBytesPerRunCursor=2*1026+16*32=2564,
        // MergeFanIn=floor(951_424/2564)=371, well under the 2048 ceiling.
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 2_000_000, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);
        Assert.True(plan.MergeFanIn >= 2);

        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 500, fanIn: plan.MergeFanIn);

        int count = 500;
        foreach (MergePass pass in passes)
        {
            int next = pass.Groups.Count + pass.CarriedForward.Count;
            Assert.True(next < count); // each pass strictly reduces the run count
            count = next;
        }

        Assert.Equal(1, count); // the final pass produces exactly one output: the plan terminates
    }

    [Fact]
    [Trait("Case", "MB-09")]
    public void Never_returns_a_plan_whose_fan_in_the_real_planner_would_reject()
    {
        // The other side of that seam: a budget that only just reaches a fan-in of one.
        // budget=1_051_140, parallelism=1, maxLine=1024, assumedMean=64:
        // bytesPerRunCursor = 2*1026+16*32 = 2564; fanInRoom = 1_051_140-1_048_576 = 2564;
        // rawFanIn = floor(2564/2564) = 1. MergePlanner rejects a fan-in below 2, so
        // Calculate must fail here first, with its own diagnostic, rather than hand a
        // plan built on that figure downstream.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(
                budgetBytes: 1_051_140, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64));
    }
}
