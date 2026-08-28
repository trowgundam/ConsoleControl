using System.Buffers.Binary;

namespace ConsoleControl.Video.FFmpeg;

internal static class JpegDimensions
{
    public static (ushort Width, ushort Height) Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("The frame does not start with a JPEG SOI marker.");
        }

        int offset = 2;
        while (offset + 1 < jpeg.Length)
        {
            if (jpeg[offset++] != 0xFF)
            {
                continue;
            }
            while (offset < jpeg.Length && jpeg[offset] == 0xFF)
            {
                offset++;
            }
            if (offset >= jpeg.Length)
            {
                break;
            }

            byte marker = jpeg[offset++];
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            if (offset + 2 > jpeg.Length)
            {
                break;
            }
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg[offset..]);
            if (segmentLength < 2 || offset + segmentLength > jpeg.Length)
            {
                throw new InvalidDataException("The JPEG contains an invalid segment length.");
            }
            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    throw new InvalidDataException("The JPEG SOF segment is too short.");
                }
                ushort height = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(offset + 3)..]);
                ushort width = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(offset + 5)..]);
                if (width == 0 || height == 0)
                {
                    throw new InvalidDataException("The JPEG has zero width or height.");
                }
                return (width, height);
            }
            offset += segmentLength;
        }
        throw new InvalidDataException("The JPEG does not contain a supported SOF marker.");
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or
        0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or
        0xCD or 0xCE or 0xCF;
}
