using System.Globalization;
using System.Net.Http.Headers;
using System.Threading.Channels;

using Avalonia.Media.Imaging;

namespace ConsoleControl.Gui;

internal sealed class VideoPresenter : IAsyncDisposable
{
    private const int MaximumFrameBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly Action<Bitmap> _onFrame;
    private readonly Action<string> _onStatus;
    private readonly Action _onDisconnected;
    private Task? _reader;
    private Task? _decoder;

    public VideoPresenter(
        Action<Bitmap> onFrame,
        Action<string> onStatus,
        Action onDisconnected)
    {
        _onFrame = onFrame;
        _onStatus = onStatus;
        _onDisconnected = onDisconnected;
    }

    public void Start(Uri streamUri)
    {
        if (_reader is not null)
        {
            throw new InvalidOperationException("The video presenter has already started.");
        }

        _reader = SuperviseStreamAsync(streamUri, _stop.Token);
        _decoder = DecodeFramesAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (Task task in new[] { _reader, _decoder }.OfType<Task>())
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _http.Dispose();
        _stop.Dispose();
    }

    private async Task SuperviseStreamAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        int failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ReadConnectionAsync(
                        streamUri,
                        () => failures = 0,
                        cancellationToken).ConfigureAwait(false);
                    throw new EndOfStreamException("The video stream ended.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    failures++;
                    _onDisconnected();
                    _onStatus($"Video disconnected; reconnecting: {exception.Message}");
                    TimeSpan delay = TimeSpan.FromMilliseconds(
                        Math.Min(2000, 200 * (1 << Math.Min(failures - 1, 3))));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    private async Task ReadConnectionAsync(
        Uri streamUri,
        Action onFrameReceived,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            streamUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureMultipartJpeg(response.Content.Headers.ContentType);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        BufferedByteReader reader = new(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? frame = await ReadPartAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }
            onFrameReceived();
            await _frames.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DecodeFramesAsync(CancellationToken cancellationToken)
    {
        await foreach (byte[] jpeg in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                using MemoryStream stream = new(jpeg, writable: false);
                Bitmap bitmap = new(stream);
                _onFrame(bitmap);
            }
            catch (Exception exception)
            {
                _onStatus($"Video frame decode failed: {exception.Message}");
            }
        }
    }

    private static void EnsureMultipartJpeg(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.MediaType?.Equals(
                "multipart/x-mixed-replace",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidDataException("The daemon returned an unsupported video stream.");
        }
    }

    private static async Task<byte[]?> ReadPartAsync(
        BufferedByteReader reader,
        CancellationToken cancellationToken)
    {
        string? boundary;
        do
        {
            boundary = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (boundary is null)
            {
                return null;
            }
        } while (boundary.Length == 0);

        if (!boundary.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The daemon returned an invalid video boundary.");
        }

        int? contentLength = null;
        while (true)
        {
            string line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The video stream ended inside a frame header.");
            if (line.Length == 0)
            {
                break;
            }
            int separator = line.IndexOf(':');
            if (separator < 1)
            {
                throw new InvalidDataException("The daemon returned an invalid video header.");
            }
            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength is not > 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("The daemon returned an invalid video frame length.");
        }
        return await reader.ReadExactlyAsync(contentLength.Value, cancellationToken).ConfigureAwait(false);
    }

    private sealed class BufferedByteReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _offset;
        private int _available;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            List<byte> line = [];
            while (line.Count <= 8192)
            {
                int next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next < 0)
                {
                    return line.Count == 0 ? null : throw new EndOfStreamException();
                }
                if (next == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }
                    return System.Text.Encoding.ASCII.GetString([.. line]);
                }
                line.Add((byte)next);
            }
            throw new InvalidDataException("The daemon returned an overlong video header.");
        }

        public async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            byte[] result = GC.AllocateUninitializedArray<byte>(length);
            int written = 0;
            while (written < length)
            {
                if (_available == 0)
                {
                    await FillAsync(cancellationToken).ConfigureAwait(false);
                    if (_available == 0)
                    {
                        throw new EndOfStreamException("The video stream ended inside a frame.");
                    }
                }
                int count = Math.Min(_available, length - written);
                _buffer.AsSpan(_offset, count).CopyTo(result.AsSpan(written));
                _offset += count;
                _available -= count;
                written += count;
            }
            return result;
        }

        private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_available == 0)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
                if (_available == 0)
                {
                    return -1;
                }
            }
            int value = _buffer[_offset++];
            _available--;
            return value;
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            _offset = 0;
            _available = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}