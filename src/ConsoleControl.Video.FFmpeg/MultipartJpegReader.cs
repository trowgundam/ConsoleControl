using System.Globalization;
using System.Text;

namespace ConsoleControl.Video.FFmpeg;

internal sealed class MultipartJpegReader(Stream stream)
{
    private const int MaximumHeaderBytes = 8192;
    private const int MaximumFrameBytes = 4 * 1024 * 1024;

    public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) =>
        ReadAsync(0, cancellationToken);

    private async Task<byte[]?> ReadAsync(int discardedFrames, CancellationToken cancellationToken)
    {
        string? boundary;
        do
        {
            boundary = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (boundary is null)
            {
                return null;
            }
        } while (boundary.Length == 0);

        if (!boundary.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidDataException("FFmpeg emitted an invalid multipart boundary.");
        }
        if (boundary.EndsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        int headerBytes = boundary.Length + 2;
        int? contentLength = null;
        bool jpeg = false;
        while (true)
        {
            string? nextLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (nextLine is null && headerBytes == boundary.Length + 2)
            {
                return null;
            }
            string line = nextLine
                ?? throw new EndOfStreamException("FFmpeg ended inside multipart headers.");
            headerBytes += line.Length + 2;
            if (headerBytes > MaximumHeaderBytes)
            {
                throw new InvalidDataException("FFmpeg multipart headers exceeded 8 KiB.");
            }
            if (line.Length == 0)
            {
                break;
            }
            int separator = line.IndexOf(':');
            if (separator < 1)
            {
                throw new InvalidDataException("FFmpeg emitted a malformed multipart header.");
            }
            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (name.Equals("Content-type", StringComparison.OrdinalIgnoreCase))
            {
                jpeg = value.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase);
            }
            else if (name.Equals("Content-length", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                contentLength = parsed;
            }
        }

        if (!jpeg || contentLength is not > 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("FFmpeg emitted an invalid JPEG part length or content type.");
        }

        byte[] frame = GC.AllocateUninitializedArray<byte>(contentLength.Value);
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != 0xD8 ||
            frame[^2] != 0xFF || frame[^1] != 0xD9)
        {
            if (discardedFrames >= 15)
            {
                throw new InvalidDataException("FFmpeg emitted 16 consecutive invalid JPEG frames.");
            }
            return await ReadAsync(discardedFrames + 1, cancellationToken).ConfigureAwait(false);
        }
        return frame;
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        List<byte> bytes = [];
        while (bytes.Count <= MaximumHeaderBytes)
        {
            byte[] one = new byte[1];
            int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : throw new EndOfStreamException();
            }
            if (one[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("FFmpeg emitted an overlong multipart line.");
    }
}
