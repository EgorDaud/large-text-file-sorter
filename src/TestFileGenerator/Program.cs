using Shared;
using TestFileGenerator.Generation;

namespace TestFileGenerator;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.Success;
        }

        if (!CommandLine.TryParseOptions(args, out GeneratorOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.InvalidArguments;
        }

        return GenerateCommand.Execute(options);
    }
}
