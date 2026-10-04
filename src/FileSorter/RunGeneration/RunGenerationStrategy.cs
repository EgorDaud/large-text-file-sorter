namespace FileSorter.RunGeneration;

internal delegate Task<string> ChunkSpill(Chunk chunk, CancellationToken ct);

// Implementations must join all work they start and surface the original failure; run order is undefined.
internal delegate Task<IReadOnlyList<string>> RunGenerationStrategy(
    ChunkReader reader,
    ChunkSpill  spill,
    int         parallelism,
    CancellationToken ct);
