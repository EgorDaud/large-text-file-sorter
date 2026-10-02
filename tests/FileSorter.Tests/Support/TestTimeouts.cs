namespace FileSorter.Tests.Support;

internal static class TestTimeouts
{
    // An upper bound for a saturated thread pool, not an expectation of how long a case
    // takes: it turns a hung run into a failure instead of a stalled suite.
    public static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);
}
