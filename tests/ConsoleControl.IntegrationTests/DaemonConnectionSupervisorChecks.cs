using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Gui;

internal static class DaemonConnectionSupervisorChecks
{
    public static async Task RunAsync()
    {
        await RecoversAutomaticallyAsync();
        await RecoversWhenRestorationFailsAsync();
        await CoalescesManualRetryAsync();
    }

    private static async Task RecoversAutomaticallyAsync()
    {
        ScriptedConsoleSession session = new(failuresBeforeSuccess: 1);
        ManualRetryWaiter waiter = new();
        List<DaemonConnectionEvent> events = [];
        await using DaemonConnectionSupervisor supervisor = new(
            session,
            (value, _) =>
            {
                lock (events)
                {
                    events.Add(value);
                }
                return Task.CompletedTask;
            },
            waiter);

        supervisor.Start();
        await WaitUntilAsync(
            () => Contains<DaemonConnectionEvent.Unavailable>(events),
            TimeSpan.FromSeconds(1));
        TestAssert.Require(waiter.Delays is [var first] &&
                           first == TimeSpan.FromMilliseconds(250),
            "the first automatic daemon retry did not use the expected backoff");

        waiter.Advance();
        await WaitUntilAsync(
            () => Contains<DaemonConnectionEvent.RecoveredStatus>(events),
            TimeSpan.FromSeconds(1));
        TestAssert.Require(session.StatusCalls == 2,
            "the connection supervisor did not retry after the daemon became available");
    }

    private static async Task CoalescesManualRetryAsync()
    {
        ScriptedConsoleSession session = new(failuresBeforeSuccess: 1);
        ManualRetryWaiter waiter = new();
        List<DaemonConnectionEvent> events = [];
        await using DaemonConnectionSupervisor supervisor = new(
            session,
            (value, _) =>
            {
                lock (events)
                {
                    events.Add(value);
                }
                return Task.CompletedTask;
            },
            waiter);

        supervisor.Start();
        await WaitUntilAsync(
            () => Contains<DaemonConnectionEvent.Unavailable>(events),
            TimeSpan.FromSeconds(1));

        supervisor.RetryNow();
        supervisor.RetryNow();
        supervisor.RetryNow();
        await WaitUntilAsync(
            () => Contains<DaemonConnectionEvent.RecoveredStatus>(events),
            TimeSpan.FromSeconds(1));

        TestAssert.Require(session.StatusCalls == 2,
            "repeated manual retries created duplicate connection attempts");
        TestAssert.Require(session.MaximumConcurrentStatusCalls == 1,
            "automatic and manual daemon attempts overlapped");
    }

    private static async Task RecoversWhenRestorationFailsAsync()
    {
        ScriptedConsoleSession session = new(failuresBeforeSuccess: 0);
        ManualRetryWaiter waiter = new();
        List<DaemonConnectionEvent> events = [];
        int restorationAttempts = 0;
        await using DaemonConnectionSupervisor supervisor = new(
            session,
            (value, _) =>
            {
                lock (events)
                {
                    events.Add(value);
                }
                if (value is DaemonConnectionEvent.RecoveredStatus &&
                    Interlocked.Increment(ref restorationAttempts) == 1)
                {
                    throw new IOException("inventory restoration failed");
                }
                return Task.CompletedTask;
            },
            waiter);

        supervisor.Start();
        await WaitUntilAsync(
            () => Contains<DaemonConnectionEvent.Unavailable>(events),
            TimeSpan.FromSeconds(1));

        waiter.Advance();
        await WaitUntilAsync(
            () => Volatile.Read(ref restorationAttempts) == 2,
            TimeSpan.FromSeconds(1));
        TestAssert.Require(session.StatusCalls == 2,
            "a post-status restoration failure ended automatic daemon reconnection");
    }

    private static bool Contains<T>(List<DaemonConnectionEvent> events)
        where T : DaemonConnectionEvent
    {
        lock (events)
        {
            return events.OfType<T>().Any();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource stop = new(timeout);
        while (!condition())
        {
            await Task.Delay(5, stop.Token);
        }
    }

    private sealed class ManualRetryWaiter : IRetryWaiter
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _waiting;
        private bool _pendingWake;

        public List<TimeSpan> Delays { get; } = [];

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Delays.Add(delay);
                if (_pendingWake)
                {
                    _pendingWake = false;
                    return Task.CompletedTask;
                }
                _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _waiting.Task.WaitAsync(cancellationToken);
            }
        }

        public void Wake()
        {
            TaskCompletionSource? waiting;
            lock (_gate)
            {
                waiting = _waiting;
                if (waiting is null)
                {
                    _pendingWake = true;
                }
            }
            waiting?.TrySetResult();
        }

        public void Advance() => Wake();

        public ValueTask DisposeAsync()
        {
            Wake();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedConsoleSession(int failuresBeforeSuccess) : IConsoleSession
    {
        private int _remainingFailures = failuresBeforeSuccess;
        private int _activeStatusCalls;

        public int StatusCalls { get; private set; }
        public int MaximumConcurrentStatusCalls { get; private set; }

        public Task<ConsoleStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeStatusCalls);
            MaximumConcurrentStatusCalls = Math.Max(MaximumConcurrentStatusCalls, active);
            try
            {
                StatusCalls++;
                if (Interlocked.Decrement(ref _remainingFailures) >= 0)
                {
                    throw new HttpRequestException("daemon unavailable");
                }
                return Task.FromResult(CreateStatus());
            }
            finally
            {
                Interlocked.Decrement(ref _activeStatusCalls);
            }
        }

        public Task<VideoInventory> GetVideoInventoryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ControllerBridgeInventory> GetControllerBridgeInventoryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ControllerBridgeSelection> SelectControllerBridgeAsync(ControllerBridgeId bridgeId, ulong expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<VideoSelection> SelectVideoSourceAsync(VideoSourceId sourceId, ulong expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<Screenshot> GetScreenshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IAutomationSession> RequestAutomationControlAsync(string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeclineControlRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IControlSession> TakeControlAsync(ControlPriority priority, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ConsoleStatus CreateStatus() => new(
            "test",
            1,
            ControlOwner.None,
            new(null, HardwareAvailability.Unknown, ControllerOutputConnection.NotConfigured,
                "No bridge", null),
            new(null, HardwareAvailability.Unknown, VideoCaptureState.SelectionRequired,
                null, null, "No video"),
            null);
    }
}