using ConsoleControl.Client;
using ConsoleControl.Core;

namespace ConsoleControl.Gui;

internal enum DaemonAvailability
{
    Connecting,
    Available,
    Unavailable,
}

internal abstract record DaemonConnectionEvent
{
    private DaemonConnectionEvent()
    {
    }

    internal sealed record Attempting : DaemonConnectionEvent;
    internal sealed record Unavailable(Exception Error) : DaemonConnectionEvent;
    internal sealed record RecoveredStatus(ConsoleStatus Status) : DaemonConnectionEvent;
    internal sealed record ObservedStatus(ConsoleStatus Status) : DaemonConnectionEvent;
}

internal interface IRetryWaiter : IAsyncDisposable
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
    void Wake();
}

internal sealed class CoalescingRetryWaiter : IRetryWaiter
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await _wake.WaitAsync(delay, cancellationToken);
    }

    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _wake.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class DaemonConnectionSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan ConnectedPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly IConsoleSession _session;
    private readonly Func<DaemonConnectionEvent, CancellationToken, Task> _handleEvent;
    private readonly IRetryWaiter _waiter;
    private readonly CancellationTokenSource _stop = new();
    private Task? _runTask;
    private bool _disposed;

    internal DaemonConnectionSupervisor(
        IConsoleSession session,
        Func<DaemonConnectionEvent, CancellationToken, Task> handleEvent,
        IRetryWaiter waiter)
    {
        _session = session;
        _handleEvent = handleEvent;
        _waiter = waiter;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runTask is not null)
        {
            throw new InvalidOperationException("Daemon connection supervision has already started.");
        }
        _runTask = RunAsync(_stop.Token);
    }

    internal void RetryNow()
    {
        if (!_disposed)
        {
            _waiter.Wake();
        }
    }

    internal static TimeSpan RetryDelay(int consecutiveFailures) =>
        TimeSpan.FromMilliseconds(Math.Min(2000, 250 * (1 << Math.Min(consecutiveFailures - 1, 3))));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _waiter.Wake();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _waiter.DisposeAsync();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        bool connected = false;
        int consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!connected)
            {
                await _handleEvent(new DaemonConnectionEvent.Attempting(), cancellationToken);
                try
                {
                    ConsoleStatus status = await _session.GetStatusAsync(cancellationToken);
                    await _handleEvent(
                        new DaemonConnectionEvent.RecoveredStatus(status),
                        cancellationToken);
                    connected = true;
                    consecutiveFailures = 0;
                    continue;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    await _handleEvent(
                        new DaemonConnectionEvent.Unavailable(exception),
                        cancellationToken);
                    await _waiter.WaitAsync(
                        RetryDelay(consecutiveFailures),
                        cancellationToken);
                    continue;
                }
            }

            await _waiter.WaitAsync(ConnectedPollInterval, cancellationToken);
            try
            {
                ConsoleStatus status = await _session.GetStatusAsync(cancellationToken);
                await _handleEvent(
                    new DaemonConnectionEvent.ObservedStatus(status),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                connected = false;
                consecutiveFailures = 1;
                await _handleEvent(
                    new DaemonConnectionEvent.Unavailable(exception),
                    cancellationToken);
                await _waiter.WaitAsync(RetryDelay(consecutiveFailures), cancellationToken);
            }
        }
    }
}