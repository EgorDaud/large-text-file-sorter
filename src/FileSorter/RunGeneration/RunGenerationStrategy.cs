namespace FileSorter.RunGeneration;

internal delegate Task<string> ChunkSpill(Chunk chunk, CancellationToken ct);

/// Schedules reads and spills without owning caller resources. It returns the original
/// branch failure and joins work it started before completing; ChunkReader owns its look-ahead fill.
/// Run order is undefined.
internal delegate Task<IReadOnlyList<string>> RunGenerationStrategy(
    ChunkReader reader,
    ChunkSpill  spill,
    int         parallelism,
    CancellationToken ct);
