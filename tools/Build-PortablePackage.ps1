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
Copy-Item -Path (Join-Path $PublishDirectory "*") -Destination $root -Recurse -Force
Copy-Item -LiteralPath $Assembler -Destination (Join-Path $root "asm6f_64.exe") -Force

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

foreach ($required in @("WarpWhistle.exe", "asm6f_64.exe", "items.json", "patches")) {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Portable package is missing required content: $required"
    }
}
