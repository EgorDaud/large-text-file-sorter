namespace FileSorter.RunGeneration;

internal readonly record struct Chunk(PooledBuffer Buffer, int Count);
