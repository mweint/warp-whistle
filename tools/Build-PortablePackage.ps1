param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$Assembler,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$PublishDirectory = (Resolve-Path $PublishDirectory).Path
$Assembler = (Resolve-Path $Assembler).Path
$root = Join-Path $OutputDirectory "WarpWhistle"
New-Item -ItemType Directory -Force $root | Out-Null

# A single-file publish needs only its executable and explicit app resources.
# Native PDBs are diagnostic symbols and must never become portable-release files.
Copy-Item -LiteralPath (Join-Path $PublishDirectory "WarpWhistle.exe") -Destination $root -Force
foreach ($folder in @("Resources", "Tools")) {
    $source = Join-Path $PublishDirectory $folder
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $root $folder) -Recurse -Force
    }
}
New-Item -ItemType Directory -Force (Join-Path $root "Tools/asm6f") | Out-Null
Copy-Item -LiteralPath $Assembler -Destination (Join-Path $root "Tools/asm6f/asm6f_64.exe") -Force
$license = Join-Path (Split-Path -Parent $Assembler) "readme-original.txt"
if (-not (Test-Path $license)) { throw "Assembler license is missing beside $Assembler" }
Copy-Item -LiteralPath $license -Destination (Join-Path $root "Tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt") -Force

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
    "Resources/items.json",
    "Resources/patches/builtins/patch.json",
    "Tools/asm6f/asm6f_64.exe",
    "Tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt"
)) {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Portable package is missing required content: $required"
    }
}
