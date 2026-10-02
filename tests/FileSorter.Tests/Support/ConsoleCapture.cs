namespace FileSorter.Tests.Support;

// Console.Error is process-wide: every class using this must sit in the "Program" collection.
internal static class ConsoleCapture
{
    internal static string Error(Action body) =>
        Error(() =>
        {
            body();
            return 0;
        }).Stderr;

    internal static (T Result, string Stderr) Error<T>(Func<T> body)
    {
        TextWriter original = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            return (body(), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    internal static async Task<string> ErrorAsync(Func<Task> body, StringWriter? writer = null) =>
        (await ErrorAsync(
            async () =>
            {
                await body();
                return 0;
            },
            writer)).Stderr;

    internal static async Task<(T Result, string Stderr)> ErrorAsync<T>(Func<Task<T>> body, StringWriter? writer = null)
    {
        TextWriter original = Console.Error;
        StringWriter captured = writer ?? new StringWriter();
        Console.SetError(captured);
        try
        {
            return (await body(), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
