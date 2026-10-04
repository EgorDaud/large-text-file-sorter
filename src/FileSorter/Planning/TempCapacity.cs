namespace FileSorter.Planning;

internal static class TempCapacity
{
    // Runs hold one input copy while a merge may already be writing part of another.
    public const int RequirementMultiplier = 2;

    // Covers newline normalization and drift between input-size and free-space readings.
    public const long CapacityMarginBytes = 1L << 20;

    public static CapacityDecision Evaluate(long inputSizeBytes, long? freeBytes) =>
        EvaluateAgainstRequirement(RequiredBytes(inputSizeBytes, RequirementMultiplier, marginBytes: 0), freeBytes);

    public static CapacityDecision EvaluateSameVolume(long inputSizeBytes, long? freeBytes) =>
        EvaluateAgainstRequirement(RequiredBytes(inputSizeBytes, RequirementMultiplier, CapacityMarginBytes), freeBytes);

    public static CapacityDecision EvaluateOutputVolume(long inputSizeBytes, long? freeBytes) =>
        EvaluateAgainstRequirement(RequiredBytes(inputSizeBytes, multiplier: 1, CapacityMarginBytes), freeBytes);

    // Clamp so an overflowed requirement cannot wrap negative and look sufficient.
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
            return new CapacityDecision(CapacityOutcome.Unknown, required, -1);
        }

        CapacityOutcome outcome = freeBytes.Value >= required
            ? CapacityOutcome.Sufficient
            : CapacityOutcome.Insufficient;

        return new CapacityDecision(outcome, required, freeBytes.Value);
    }
}
