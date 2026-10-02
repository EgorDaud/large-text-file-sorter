using BenchmarkDotNet.Running;
using FileSorter.Cli;

namespace FileSorter.Benchmarks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--flatness"])
        {
            return await FlatnessRunner.RunAsync();
        }

        if (args is ["--flatness-parallel-merge"])
        {
            return await FlatnessRunner.RunParallelMergeAsync();
        }

        if (args is ["--flatness-worker", string inputPath, string outputPath, string runsDirectory, string budgetArg, string pipelineArg])
        {
            long memoryBudgetBytes = long.Parse(budgetArg, System.Globalization.CultureInfo.InvariantCulture);
            Pipeline pipeline = Enum.Parse<Pipeline>(pipelineArg, ignoreCase: true);
            return await FlatnessRunner.RunWorkerAsync(inputPath, outputPath, runsDirectory, memoryBudgetBytes, pipeline);
        }

        string[] effectiveArgs = args.Length == 0 ? ["--filter", "*"] : args;
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(effectiveArgs);
        return 0;
    }
}
