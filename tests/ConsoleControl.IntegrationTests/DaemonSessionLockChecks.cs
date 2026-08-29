using ConsoleControl.Daemon;

internal static class DaemonSessionLockChecks
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"consolecontrol-daemon-lock-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "session.lock");
        try
        {
            using (DaemonSessionLock first = DaemonSessionLock.TryAcquire(path)
                ?? throw new InvalidOperationException("the first daemon session lock was refused"))
            {
                using DaemonSessionLock? second = DaemonSessionLock.TryAcquire(path);
                TestAssert.Require(second is null, "a second daemon acquired the same console session lock");
            }

            using DaemonSessionLock reacquired = DaemonSessionLock.TryAcquire(path)
                ?? throw new InvalidOperationException("the daemon session lock remained held after disposal");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}