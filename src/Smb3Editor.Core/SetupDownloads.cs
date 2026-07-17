using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Smb3Editor.Core;

/// <summary>Downloads user-authorized ROMs and the external Mesen application into the portable workspace.</summary>
public static class SetupDownloads
{
    private const long MaximumDownloadBytes = 128L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    public static async Task<OperationResult<string>> ImportRomAsync(string source, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(WorkspacePaths.RomsDirectory);
            var staging = Path.Combine(WorkspacePaths.RomsDirectory, $".download-{Guid.NewGuid():N}.nes");
            if (File.Exists(source))
            {
                File.Copy(source, staging, overwrite: false);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme != Uri.UriSchemeHttps)
                    return OperationResult<string>.Failure(Diagnostics.Error("ROM_URL", "ROM download URLs must use HTTPS."));
                await DownloadFileAsync(uri, staging, cancellationToken);
            }
            else
            {
                return OperationResult<string>.Failure(Diagnostics.Error("ROM_SOURCE", "Choose an existing ROM file or enter an HTTPS URL."));
            }

            var rom = RomImage.Load(staging);
            if (!rom.IsSuccess)
            {
                File.Delete(staging);
                return OperationResult<string>.Failure(rom.Diagnostics.ToArray());
            }

            var destination = Path.Combine(WorkspacePaths.RomsDirectory, $"SMB3-{rom.Value!.Profile.Id}.nes");
            File.Move(staging, destination, overwrite: true);
            return OperationResult<string>.Success(destination, rom.Diagnostics);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return OperationResult<string>.Failure(Diagnostics.Error("ROM_IMPORT", $"The ROM could not be imported: {ex.Message}"));
        }
    }

    public static async Task<OperationResult<string>> DownloadMesenAsync(CancellationToken cancellationToken = default)
    {
        var stagingDirectory = Path.Combine(WorkspacePaths.EmulatorsDirectory, $".mesen-{Guid.NewGuid():N}");
        var archive = Path.Combine(Path.GetTempPath(), $"warp-whistle-mesen-{Guid.NewGuid():N}.zip");
        try
        {
            Directory.CreateDirectory(WorkspacePaths.EmulatorsDirectory);
            using var response = await Client.GetAsync("https://api.github.com/repos/nesdev-org/MesenCE/releases/latest", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var release = document.RootElement;
            var tag = release.GetProperty("tag_name").GetString();
            var asset = release.GetProperty("assets").EnumerateArray()
                .Select(item => new
                {
                    Name = item.GetProperty("name").GetString() ?? string.Empty,
                    Url = item.GetProperty("browser_download_url").GetString()
                })
                .FirstOrDefault(item => item.Url is not null && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    item.Name.Contains("windows", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(tag) || asset?.Url is null)
                return OperationResult<string>.Failure(Diagnostics.Error("MESEN_RELEASE", "The official Mesen release did not provide a Windows ZIP asset."));

            await DownloadFileAsync(new Uri(asset.Url), archive, cancellationToken);
            Directory.CreateDirectory(stagingDirectory);
            ExtractArchiveSafely(archive, stagingDirectory);
            var executable = Directory.EnumerateFiles(stagingDirectory, "Mesen.exe", SearchOption.AllDirectories).SingleOrDefault();
            if (executable is null)
                return OperationResult<string>.Failure(Diagnostics.Error("MESEN_ARCHIVE", "The downloaded Mesen archive does not contain Mesen.exe."));

            File.WriteAllText(Path.Combine(stagingDirectory, "WARP-WHISTLE-MESENCE-NOTICE.txt"),
                "Mesen Community Edition (MesenCE) was downloaded from its official GitHub release.\n" +
                "Source and GPLv3 license: https://github.com/nesdev-org/MesenCE\n");

            var destination = Path.Combine(WorkspacePaths.EmulatorsDirectory, "MesenCE", tag);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(stagingDirectory, destination);
            executable = Path.Combine(destination, Path.GetRelativePath(stagingDirectory, executable));
            return OperationResult<string>.Success(executable, [Diagnostics.Info("MESEN_INSTALLED", $"Installed MesenCE {tag} in the Emulators workspace folder.")]);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or TaskCanceledException)
        {
            return OperationResult<string>.Failure(Diagnostics.Error("MESEN_INSTALL", $"Mesen could not be installed: {ex.Message}"));
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            throw new InvalidDataException("The download is larger than the permitted 128 MB.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumDownloadBytes) throw new InvalidDataException("The download is larger than the permitted 128 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WarpWhistle/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void ExtractArchiveSafely(string archive, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Mesen archive contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, overwrite: true);
        }
    }
}
