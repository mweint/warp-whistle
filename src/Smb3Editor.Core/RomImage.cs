using System.Security.Cryptography;

namespace Smb3Editor.Core;

public sealed class RomImage
{
    private RomImage(
        string sourcePath,
        byte[] bytes,
        RomProfile profile,
        string sha1,
        string sha256,
        int mapper,
        int prgOffset,
        int prgLength,
        int chrOffset,
        int chrLength)
    {
        SourcePath = sourcePath;
        Bytes = bytes;
        Profile = profile;
        Sha1 = sha1;
        Sha256 = sha256;
        Mapper = mapper;
        PrgOffset = prgOffset;
        PrgLength = prgLength;
        ChrOffset = chrOffset;
        ChrLength = chrLength;
    }

    public string SourcePath { get; }
    public byte[] Bytes { get; }
    public RomProfile Profile { get; }
    public string Sha1 { get; }
    public string Sha256 { get; }
    public int Mapper { get; }
    public int PrgOffset { get; }
    public int PrgLength { get; }
    public int ChrOffset { get; }
    public int ChrLength { get; }

    public ReadOnlySpan<byte> Prg => Bytes.AsSpan(PrgOffset, PrgLength);
    public ReadOnlySpan<byte> Chr => Bytes.AsSpan(ChrOffset, ChrLength);

    internal static RomImage CreateForTesting(string path, byte[] bytes, RomProfile profile)
    {
        var trainerLength = (bytes[6] & 0x04) != 0 ? 512 : 0;
        var prgLength = bytes[4] * 16_384;
        var chrLength = bytes[5] * 8_192;
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new RomImage(path, bytes, profile, sha1, sha256, 4, 16 + trainerLength, prgLength, 16 + trainerLength + prgLength, chrLength);
    }

    public static OperationResult<RomImage> Load(string path)
    {
        var diagnostics = new List<Diagnostic>();
        byte[] fileBytes;

        try
        {
            fileBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return OperationResult<RomImage>.Failure(
                Diagnostics.Error("ROM_IO", $"The ROM could not be read: {ex.Message}"));
        }

        if (fileBytes.Length < 16)
        {
            return OperationResult<RomImage>.Failure(
                Diagnostics.Error("ROM_TRUNCATED", "The file is too small to contain an iNES header."));
        }

        if (fileBytes[0] != (byte)'N' || fileBytes[1] != (byte)'E' || fileBytes[2] != (byte)'S' || fileBytes[3] != 0x1A)
        {
            return OperationResult<RomImage>.Failure(
                Diagnostics.Error("ROM_MAGIC", "The file does not have a valid iNES header."));
        }

        var trainerLength = (fileBytes[6] & 0x04) != 0 ? 512 : 0;
        var prgLength = checked(fileBytes[4] * 16_384);
        var chrLength = checked(fileBytes[5] * 8_192);
        var declaredLength = checked(16 + trainerLength + prgLength + chrLength);
        var mapper = (fileBytes[6] >> 4) | (fileBytes[7] & 0xF0);

        if (fileBytes.Length < declaredLength)
        {
            return OperationResult<RomImage>.Failure(
                Diagnostics.Error(
                    "ROM_TRUNCATED",
                    $"The header declares {declaredLength:N0} bytes, but the file contains only {fileBytes.Length:N0}."));
        }

        if (fileBytes.Length > declaredLength)
        {
            diagnostics.Add(Diagnostics.Warning(
                "ROM_TRAILING_DATA",
                $"The file has {fileBytes.Length - declaredLength:N0} bytes after its declared ROM payload. They will never be imported or exported."));
        }

        var normalizedBytes = fileBytes.AsSpan(0, declaredLength).ToArray();
        var sha1 = Convert.ToHexString(SHA1.HashData(normalizedBytes)).ToLowerInvariant();
        var sha256 = Convert.ToHexString(SHA256.HashData(normalizedBytes)).ToLowerInvariant();
        var profile = Smb3Profiles.FindBySha1(sha1);

        if (mapper != 4)
        {
            diagnostics.Add(Diagnostics.Error("ROM_MAPPER", $"Expected MMC3 mapper 4, but the ROM declares mapper {mapper}."));
        }

        if (profile is null)
        {
            diagnostics.Add(Diagnostics.Error(
                "ROM_UNSUPPORTED",
                $"This is not a verified clean US PRG0 or PRG1 ROM (SHA-1 {sha1}). ROM hacks and modified dumps are not accepted."));
            return OperationResult<RomImage>.Failure(diagnostics.ToArray());
        }

        if (profile.PrgBytes != prgLength || profile.ChrBytes != chrLength)
        {
            diagnostics.Add(Diagnostics.Error(
                "ROM_SIZE",
                $"The {profile.DisplayName} profile expects {profile.PrgBytes:N0} PRG and {profile.ChrBytes:N0} CHR bytes."));
            return OperationResult<RomImage>.Failure(diagnostics.ToArray());
        }

        var catalog = RomCatalogBuilder.Build(normalizedBytes);
        diagnostics.AddRange(catalog.Diagnostics);
        if (!catalog.IsSuccess)
        {
            return OperationResult<RomImage>.Failure(diagnostics.ToArray());
        }

        profile = profile with { Levels = catalog.Value! };

        var prgOffset = 16 + trainerLength;
        var chrOffset = prgOffset + prgLength;
        diagnostics.Add(Diagnostics.Info("ROM_VERIFIED", $"Verified {profile.DisplayName}."));

        return OperationResult<RomImage>.Success(
            new RomImage(path, normalizedBytes, profile, sha1, sha256, mapper, prgOffset, prgLength, chrOffset, chrLength),
            diagnostics);
    }

    public OperationResult<ReadOnlyMemory<byte>> ReadRange(int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset > Bytes.Length - length)
        {
            return OperationResult<ReadOnlyMemory<byte>>.Failure(
                Diagnostics.Error("ROM_RANGE", $"{label} points outside the verified ROM image."));
        }

        return OperationResult<ReadOnlyMemory<byte>>.Success(Bytes.AsMemory(offset, length));
    }
}
