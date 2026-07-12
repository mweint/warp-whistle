namespace Smb3Editor.Core;

public static class AtomicFile
{
    public static OperationResult<string> Write(string destinationPath, ReadOnlySpan<byte> contents, bool maintainBackup = true)
    {
        string? temporaryPath = null;
        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return OperationResult<string>.Failure(Diagnostics.Error("FILE_PATH", "The destination has no valid parent directory."));
            }

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (maintainBackup && File.Exists(fullPath))
            {
                File.Copy(fullPath, fullPath + ".bak", overwrite: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
            return OperationResult<string>.Success(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return OperationResult<string>.Failure(
                Diagnostics.Error("FILE_WRITE", $"The file could not be written safely: {ex.Message}"));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A stale temporary file is preferable to touching the destination.
                }
                catch (UnauthorizedAccessException)
                {
                    // A stale temporary file is preferable to touching the destination.
                }
            }
        }
    }
}

