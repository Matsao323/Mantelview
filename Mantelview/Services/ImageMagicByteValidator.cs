using System;
using System.IO;

namespace Mantelview.Services;

public static class ImageMagicByteValidator
{
    private const int HeaderLength = 12;

    public static bool HasValidMagicBytes(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderLength];

        try
        {
            using var stream = File.OpenRead(filePath);
            var bytesRead = ReadHeader(stream, header);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch
            {
                ".jpg" or ".jpeg" => HasPrefix(header, bytesRead, 0xFF, 0xD8, 0xFF),
                ".png" => HasPrefix(header, bytesRead, 0x89, 0x50, 0x4E, 0x47),
                ".bmp" => HasPrefix(header, bytesRead, 0x42, 0x4D),
                ".gif" => HasPrefix(header, bytesRead, 0x47, 0x49, 0x46, 0x38),
                ".webp" => HasPrefix(header, bytesRead, 0x52, 0x49, 0x46, 0x46)
                    && HasOffsetPrefix(header, bytesRead, 8, 0x57, 0x45, 0x42, 0x50),
                _ => false,
            };
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ReadHeader(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var bytesRead = stream.Read(buffer[totalRead..]);

            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> header, int bytesRead, params byte[] expected)
    {
        if (bytesRead < expected.Length)
        {
            return false;
        }

        return header[..expected.Length].SequenceEqual(expected);
    }

    private static bool HasOffsetPrefix(ReadOnlySpan<byte> header, int bytesRead, int offset, params byte[] expected)
    {
        if (bytesRead < offset + expected.Length)
        {
            return false;
        }

        return header.Slice(offset, expected.Length).SequenceEqual(expected);
    }
}
