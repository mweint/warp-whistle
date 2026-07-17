param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$PublishDirectory = (Resolve-Path $PublishDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$root = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "WarpWhistle"))
if (-not $root.StartsWith($OutputDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Portable package root escaped its output directory."
}
if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null

# A single-file publish plus the canonical external runtime content.
# Native PDBs are diagnostic symbols and must never become portable-release files.
Copy-Item -LiteralPath (Join-Path $PublishDirectory "WarpWhistle.exe") -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $PublishDirectory "items.json") -Destination $root -Force
foreach ($folder in @("patches", "tools")) {
    $source = Join-Path $PublishDirectory $folder
    if (-not (Test-Path $source)) { throw "Publish output is missing $folder." }
    Copy-Item -LiteralPath $source -Destination (Join-Path $root $folder) -Recurse -Force
}

foreach ($folder in @("ROMs", "Emulators", "Projects", "Exports", "Data")) {
    New-Item -ItemType Directory -Force (Join-Path $root $folder) | Out-Null
}

@"
Warp Whistle portable workspace

1. Put a verified SMB3 PRG0 or PRG1 ROM in ROMs.
2. Put an emulator such as Mesen in Emulators.
3. Start WarpWhistle.exe.

Projects stores editor project files. Exports is for ROMs and patches made by
the editor. Data stores settings, the palette library, and temporary play tests.
The ZIP does not include ROMs, emulators, or Nintendo assets.
"@ | Set-Content -LiteralPath (Join-Path $root "README.txt") -Encoding utf8

foreach ($required in @(
    "WarpWhistle.exe",
    "items.json",
    "patches/builtins/patch.json",
    "tools/asm6f/asm6f_64.exe",
    "tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt"
)) {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Portable package is missing required content: $required"
    }
}
