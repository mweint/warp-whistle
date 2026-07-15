param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

$ErrorActionPreference = "Stop"
$ZipPath = (Resolve-Path $ZipPath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $paths = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $required = @(
        "WarpWhistle/WarpWhistle.exe",
        "WarpWhistle/Resources/items.json",
        "WarpWhistle/Resources/patches/builtins/patch.json",
        "WarpWhistle/Tools/asm6f/asm6f_64.exe",
        "WarpWhistle/Tools/asm6f/THIRD-PARTY-ASM6F-LICENSE.txt",
        "WarpWhistle/README.txt"
    )
    foreach ($path in $required) {
        if ($paths -notcontains $path) { throw "Portable ZIP is missing required content: $path" }
    }
    $forbidden = @($paths | Where-Object { $_ -match '^WarpWhistle/[^/]+\.(dll|pdb)$' })
    if ($forbidden.Count -gt 0) { throw "Portable ZIP contains root runtime files: $($forbidden -join ', ')" }
    Write-Host "Portable ZIP verified: $ZipPath"
}
finally {
    $archive.Dispose()
}
