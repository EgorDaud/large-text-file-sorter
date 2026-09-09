using BenchmarkDotNet.Running;

namespace FileSorter.Benchmarks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Flatness samples each size once instead of using BenchmarkDotNet iterations.
        if (args is ["--flatness"])
        {
            return await FlatnessRunner.RunAsync();
        }

        if (args is ["--flatness-parallel-merge"])
        {
            return await FlatnessRunner.RunParallelMergeAsync();
        }

        // Each input size uses a fresh worker process and passes its own budget.
        if (args is ["--flatness-worker", string inputPath, string outputPath, string runsDirectory, string budgetArg])
        {
            long memoryBudgetBytes = long.Parse(budgetArg, System.Globalization.CultureInfo.InvariantCulture);
            return await FlatnessRunner.RunWorkerAsync(inputPath, outputPath, runsDirectory, memoryBudgetBytes);
        }

        // Avoid the interactive picker when no filter is supplied.
        string[] effectiveArgs = args.Length == 0 ? ["--filter", "*"] : args;
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(effectiveArgs);
        return 0;
    }
}
