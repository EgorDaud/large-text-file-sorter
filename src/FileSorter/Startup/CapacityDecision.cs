namespace FileSorter.Startup;

internal enum CapacityOutcome { Sufficient, Insufficient, Unknown }

// This is a pure decision over byte counts. Program adds the probed directory to messages.
internal readonly record struct CapacityDecision(
    CapacityOutcome Outcome,
    long RequiredBytes,
    long AvailableBytes);
