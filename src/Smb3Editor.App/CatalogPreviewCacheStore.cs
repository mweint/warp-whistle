using System.IO.Compression;
using Smb3Editor.Core;

namespace Smb3Editor.App;

/// <summary>Local ROM-derived thumbnail cache; never project or repository data.</summary>
public static class CatalogPreviewCacheStore
{
    private const int Version = 5;

    public static IReadOnlyDictionary<(bool Enemy, int Id), CatalogPreviewData?> Load(string sha1, int tileset, int objectPalette)
    {
        try
        {
            var path = CachePath(sha1, tileset, objectPalette);
            if (!File.Exists(path)) return new Dictionary<(bool, int), CatalogPreviewData?>();
            using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var reader = new BinaryReader(input);
            if (reader.ReadInt32() != Version) return new Dictionary<(bool, int), CatalogPreviewData?>();
            var count = reader.ReadInt32();
            if (count is < 0 or > 1024) return new Dictionary<(bool, int), CatalogPreviewData?>();
            var result = new Dictionary<(bool, int), CatalogPreviewData?>();
            for (var entry = 0; entry < count; entry++)
            {
                var enemy = reader.ReadBoolean();
                var id = reader.ReadInt32();
                if (!reader.ReadBoolean())
                {
                    result[(enemy, id)] = null;
                    continue;
                }
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                var length = reader.ReadInt32();
                if (width is < 1 or > 4096 || height is < 1 or > 4096 || length != width * height)
                    return new Dictionary<(bool, int), CatalogPreviewData?>();
                var pixels = new uint[length];
                for (var pixel = 0; pixel < length; pixel++) pixels[pixel] = reader.ReadUInt32();
                result[(enemy, id)] = new CatalogPreviewData(width, height, pixels);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return new Dictionary<(bool, int), CatalogPreviewData?>();
        }
    }

    public static void Save(string sha1, int tileset, int objectPalette, IReadOnlyDictionary<(bool Enemy, int Id), CatalogPreviewData?> previews)
    {
        try
        {
            using var memory = new MemoryStream();
            using (var output = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(Version);
                writer.Write(previews.Count);
                foreach (var (key, preview) in previews)
                {
                    writer.Write(key.Enemy);
                    writer.Write(key.Id);
                    writer.Write(preview is not null);
                    if (preview is null) continue;
                    writer.Write(preview.Width);
                    writer.Write(preview.Height);
                    writer.Write(preview.Pixels.Count);
                    foreach (var pixel in preview.Pixels) writer.Write(pixel);
                }
            }
            AtomicFile.Write(CachePath(sha1, tileset, objectPalette), memory.ToArray(), maintainBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This optional cache must never affect editing.
        }
    }

    private static string CachePath(string sha1, int tileset, int objectPalette) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpWhistle", "preview-cache",
        $"{sha1}-t{tileset}-o{objectPalette}-v{Version}.bin.gz");
}
