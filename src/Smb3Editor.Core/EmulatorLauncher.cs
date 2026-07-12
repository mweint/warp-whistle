using System.Diagnostics;

namespace Smb3Editor.Core;

public sealed record EmulatorConfiguration(string ExecutablePath, IReadOnlyList<string> Arguments);

public interface IEmulatorLauncher
{
    OperationResult<int> Launch(EmulatorConfiguration configuration, string romPath);
}

public sealed class EmulatorLauncher : IEmulatorLauncher
{
    public OperationResult<int> Launch(EmulatorConfiguration configuration, string romPath)
    {
        try
        {
            if (!File.Exists(configuration.ExecutablePath))
            {
                return OperationResult<int>.Failure(Diagnostics.Error("EMULATOR_MISSING", "The configured emulator executable does not exist."));
            }

            if (!File.Exists(romPath))
            {
                return OperationResult<int>.Failure(Diagnostics.Error("PLAYTEST_ROM", "The temporary play-test ROM does not exist."));
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = configuration.ExecutablePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(configuration.ExecutablePath) ?? Environment.CurrentDirectory
            };

            var includedRom = false;
            foreach (var argument in configuration.Arguments)
            {
                var expanded = argument.Replace("{rom}", romPath, StringComparison.Ordinal);
                includedRom |= !string.Equals(expanded, argument, StringComparison.Ordinal);
                startInfo.ArgumentList.Add(expanded);
            }

            if (!includedRom)
            {
                startInfo.ArgumentList.Add(romPath);
            }

            var process = Process.Start(startInfo);
            return process is null
                ? OperationResult<int>.Failure(Diagnostics.Error("EMULATOR_START", "Windows did not start the emulator process."))
                : OperationResult<int>.Success(process.Id, [Diagnostics.Info("EMULATOR_STARTED", $"Started emulator process {process.Id}.")]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return OperationResult<int>.Failure(Diagnostics.Error("EMULATOR_START", $"The emulator could not be started: {ex.Message}"));
        }
    }
}

