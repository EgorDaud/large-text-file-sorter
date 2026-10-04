namespace FileSorter.Planning;

internal enum CapacityOutcome { Sufficient, Insufficient, Unknown }

internal readonly record struct CapacityDecision(
    CapacityOutcome Outcome,
    long RequiredBytes,
    long AvailableBytes);
