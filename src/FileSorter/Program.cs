using FileSorter.Cli;
using Shared;

namespace FileSorter;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.Success;
        }

        return args is ["--verify", ..] ? VerifyCommand.Execute(args) : SortCommand.Execute(args);
    }
}
