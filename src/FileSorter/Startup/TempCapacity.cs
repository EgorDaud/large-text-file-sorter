namespace FileSorter.Startup;

internal static class TempCapacity
{
    // Run files keep one input copy while a merge can create part of another, so twice
    // the input is a conservative temporary-space bound.
    public const int RequirementMultiplier = 2;

    // Covers newline normalization and drift between input-size and free-space readings.
    public const long CapacityMarginBytes = 1L << 20;

    // Temp-only requirement on a separate output volume. Null free space remains unknown.
    public static CapacityDecision Evaluate(long inputSizeBytes, long? freeBytes) =>
        EvaluateAgainstRequirement(RequiredBytes(inputSizeBytes, RequirementMultiplier, marginBytes: 0), freeBytes);

    // One volume must hold temporary runs and final output, including the safety margin.
    public static CapacityDecision EvaluateSameVolume(long inputSizeBytes, long? freeBytes) =>
        EvaluateAgainstRequirement(RequiredBytes(inputSizeBytes, RequirementMultiplier, CapacityMarginBytes), freeBytes);

    // A separate output volume needs one input-sized result plus the margin. Some
    // replacement paths briefly stage another output file; this known transient is not
    // included in the conservative capacity arithmetic.
    public static CapacityDecision EvaluateOutputVolume(long inputSizeBytes, long? freeBytes) =>
        EvaluateAgainstRequirement(RequiredBytes(inputSizeBytes, multiplier: 1, CapacityMarginBytes), freeBytes);

    // Clamp overflow so a negative requirement cannot appear sufficient.
    private static long RequiredBytes(long inputSizeBytes, int multiplier, long marginBytes)
    {
        if (inputSizeBytes > (long.MaxValue - marginBytes) / multiplier)
        {
            return long.MaxValue;
        }

        return (inputSizeBytes * multiplier) + marginBytes;
    }

    private static CapacityDecision EvaluateAgainstRequirement(long required, long? freeBytes)
    {
        if (freeBytes is null)
        {
            // -1 keeps unavailable free space distinct from a real zero value.
            return new CapacityDecision(CapacityOutcome.Unknown, required, -1);
        }

        // Equality fits because the multiplier and margin are already conservative.
        CapacityOutcome outcome = freeBytes.Value >= required
            ? CapacityOutcome.Sufficient
            : CapacityOutcome.Insufficient;

        return new CapacityDecision(outcome, required, freeBytes.Value);
    }
}
