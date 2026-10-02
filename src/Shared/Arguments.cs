using System.Diagnostics.CodeAnalysis;

namespace Shared;

internal static class Arguments
{
    public static bool TryTakeValue(
        string[] args,
        ref int index,
        string option,
        [NotNullWhen(true)] out string? value,
        [NotNullWhen(false)] out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"{option} expects a value.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }
}
