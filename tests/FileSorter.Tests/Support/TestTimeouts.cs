namespace FileSorter.Tests.Support;

internal static class TestTimeouts
{
    // Sized for a saturated thread pool; it only turns a hang into a failure.
    public static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);
}
