namespace ConsoleControl.Daemon;

internal sealed class DaemonSessionLock : IDisposable
{
    private readonly FileStream _stream;

    private DaemonSessionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static DaemonSessionLock? TryAcquire(string? path = null)
    {
        path ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ConsoleControl",
            "daemon-session.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new(new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}