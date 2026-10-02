using System.Runtime.InteropServices;

namespace Shared;

// SIGHUP is deliberately not handled so a run started under nohup survives the session ending.
internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConsoleCancelEventHandler _onCancelKey;
    private readonly PosixSignalRegistration _terminate;

    public ConsoleCancellation()
    {
        _onCancelKey = (_, e) => e.Cancel = BeginCancel();
        _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = BeginCancel());
        Console.CancelKeyPress += _onCancelKey;
    }

    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= _onCancelKey;
        _terminate.Dispose();
        _cts.Dispose();
    }

    // Leaving Cancel unset on a repeat request lets the default action kill the process during a slow cleanup.
    private bool BeginCancel()
    {
        if (_cts.IsCancellationRequested)
        {
            return false;
        }

        _cts.Cancel();
        return true;
    }
}
