param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"
$PublishDirectory = (Resolve-Path $PublishDirectory).Path
$root = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $PublishDirectory "WarpWhistle.exe"
$skiaNative = Join-Path $PublishDirectory "libSkiaSharp.dll"

if (-not (Test-Path $executable)) { throw "Publish output is missing WarpWhistle.exe." }
if (-not (Test-Path $skiaNative)) { throw "Publish output is missing libSkiaSharp.dll." }
if (Test-Path (Join-Path $PublishDirectory "WarpWhistle.dll")) {
    throw "Root artifact publishing requires the same single-file publish mode used for releases."
}

function Assert-SameFile([string]$source, [string]$published) {
    if (-not (Test-Path $source) -or -not (Test-Path $published)) {
        throw "Runtime content is missing: $source or $published"
    }
    if ((Get-FileHash $source -Algorithm SHA256).Hash -ne (Get-FileHash $published -Algorithm SHA256).Hash) {
        throw "Published runtime content does not match its canonical source: $source"
    }
}

function Get-DirectoryManifest([string]$directory) {
    $base = (Resolve-Path $directory).Path.TrimEnd('\') + '\'
    @(Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object {
        "$($_.FullName.Substring($base.Length).Replace('\', '/'))|$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
}

Assert-SameFile (Join-Path $root "items.json") (Join-Path $PublishDirectory "items.json")
Assert-SameFile (Join-Path $root "tools/asm6f/asm6f_64.exe") (Join-Path $PublishDirectory "tools/asm6f/asm6f_64.exe")
Assert-SameFile (Join-Path $root "tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt") (Join-Path $PublishDirectory "tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt")
$sourcePatches = Get-DirectoryManifest (Join-Path $root "patches")
$publishedPatches = Get-DirectoryManifest (Join-Path $PublishDirectory "patches")
if (Compare-Object $sourcePatches $publishedPatches) {
    throw "Published patches do not match the canonical root patches directory."
}

Copy-Item -LiteralPath $executable -Destination (Join-Path $root "WarpWhistle.exe") -Force
Copy-Item -LiteralPath $skiaNative -Destination (Join-Path $root "libSkiaSharp.dll") -Force

foreach ($required in @(
    "WarpWhistle.exe",
    "libSkiaSharp.dll",
    "items.json",
    "tools/asm6f/asm6f_64.exe",
    "tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt",
    "patches/builtins/patch.json",
    "ROMs",
    "Emulators",
    "Projects",
    "Exports",
    "Data"
)) {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Root runtime layout is missing $required."
    }
}

Get-FileHash (Join-Path $root "WarpWhistle.exe") -Algorithm SHA256
