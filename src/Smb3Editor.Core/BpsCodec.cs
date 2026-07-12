using System.Buffers.Binary;

namespace Smb3Editor.Core;

public interface IBpsCodec
{
    byte[] Create(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target);
    OperationResult<byte[]> Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch);
}

public sealed class BpsCodec : IBpsCodec
{
    private static readonly byte[] Magic = "BPS1"u8.ToArray();

    public byte[] Create(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target)
    {
        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteNumber(stream, (ulong)source.Length);
        WriteNumber(stream, (ulong)target.Length);
        WriteNumber(stream, 0);

        if (target.Length > 0)
        {
            WriteNumber(stream, ((ulong)(target.Length - 1) << 2) | 1UL); // TargetRead
            stream.Write(target);
        }

        WriteUInt32(stream, Crc32.Compute(source));
        WriteUInt32(stream, Crc32.Compute(target));
        var withoutPatchChecksum = stream.ToArray();
        WriteUInt32(stream, Crc32.Compute(withoutPatchChecksum));
        return stream.ToArray();
    }

    public OperationResult<byte[]> Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch)
    {
        try
        {
            if (patch.Length < 16 || !patch[..4].SequenceEqual(Magic))
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_MAGIC", "The patch is not a BPS1 patch."));
            }

            var storedPatchCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch[^4..]);
            if (storedPatchCrc != Crc32.Compute(patch[..^4]))
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_PATCH_CRC", "The BPS patch checksum is invalid."));
            }

            var cursor = 4;
            var sourceSize = checked((int)ReadNumber(patch, ref cursor));
            var targetSize = checked((int)ReadNumber(patch, ref cursor));
            var metadataSize = checked((int)ReadNumber(patch, ref cursor));
            if (sourceSize != source.Length)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_SOURCE_SIZE", "The source ROM size does not match the BPS patch."));
            }

            if (cursor > patch.Length - 12 - metadataSize)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_TRUNCATED", "The BPS metadata is truncated."));
            }

            cursor += metadataSize;
            var output = new byte[targetSize];
            var outputOffset = 0;
            while (outputOffset < output.Length)
            {
                if (cursor >= patch.Length - 12)
                {
                    return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_TRUNCATED", "The BPS command stream is truncated."));
                }

                var command = ReadNumber(patch, ref cursor);
                var action = (int)(command & 3);
                var length = checked((int)((command >> 2) + 1));
                if (length > output.Length - outputOffset)
                {
                    return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_RANGE", "A BPS action writes past the target size."));
                }

                switch (action)
                {
                    case 0: // SourceRead
                        if (outputOffset > source.Length - length)
                        {
                            return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_SOURCE_RANGE", "A BPS source-read action is out of range."));
                        }

                        source.Slice(outputOffset, length).CopyTo(output.AsSpan(outputOffset));
                        outputOffset += length;
                        break;
                    case 1: // TargetRead
                        if (cursor > patch.Length - 12 - length)
                        {
                            return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_TRUNCATED", "A BPS target-read action is truncated."));
                        }

                        patch.Slice(cursor, length).CopyTo(output.AsSpan(outputOffset));
                        cursor += length;
                        outputOffset += length;
                        break;
                    default:
                        return OperationResult<byte[]>.Failure(
                            Diagnostics.Error("BPS_ACTION", "This safety-focused decoder does not accept copy-compressed BPS actions."));
                }
            }

            var expectedSourceCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch[^12..^8]);
            var expectedTargetCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch[^8..^4]);
            if (Crc32.Compute(source) != expectedSourceCrc)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_SOURCE_CRC", "The source ROM checksum does not match the BPS patch."));
            }

            if (Crc32.Compute(output) != expectedTargetCrc)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_TARGET_CRC", "The patched ROM checksum does not match the BPS target."));
            }

            return OperationResult<byte[]>.Success(output);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("BPS_INVALID", $"The BPS patch is invalid: {ex.Message}"));
        }
    }

    private static void WriteNumber(Stream stream, ulong value)
    {
        while (true)
        {
            var data = (byte)(value & 0x7F);
            value >>= 7;
            if (value == 0)
            {
                stream.WriteByte((byte)(data | 0x80));
                return;
            }

            stream.WriteByte(data);
            value--;
        }
    }

    private static ulong ReadNumber(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        ulong data = 0;
        ulong shift = 1;
        while (true)
        {
            if (cursor >= bytes.Length)
            {
                throw new InvalidDataException("Unexpected end of variable-length integer.");
            }

            var current = bytes[cursor++];
            data = checked(data + ((ulong)(current & 0x7F) * shift));
            if ((current & 0x80) != 0)
            {
                return data;
            }

            shift = checked(shift << 7);
            data = checked(data + shift);
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = (crc >> 8) ^ Table[(crc ^ value) & 0xFF];
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}

