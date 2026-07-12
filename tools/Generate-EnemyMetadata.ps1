param(
    [Parameter(Mandatory = $true)][string]$GameXml,
    [Parameter(Mandatory = $true)][string]$Output
)

$raw = Get-Content -LiteralPath $GameXml -Raw
$objectsStart = $raw.IndexOf('<objects>')
$objectsEnd = $raw.IndexOf('</objects>', $objectsStart) + '</objects>'.Length
[xml]$document = '<game>' + $raw.Substring($objectsStart, $objectsEnd - $objectsStart) + '</game>'
function Convert-Number([string]$value) {
    if ($value.StartsWith('0x')) { return [Convert]::ToInt32($value.Substring(2), 16) }
    return [int]$value
}
$entries = foreach ($object in $document.game.objects.object) {
    $sprites = foreach ($sprite in @($object.sprite | Where-Object { $null -ne $_ })) {
        [ordered]@{
            x = [int]$sprite.x
            y = [int]$sprite.y
            bank = Convert-Number ([string]$sprite.bank)
            pattern = Convert-Number ([string]$sprite.pattern)
            palette = [int]$sprite.palette
            hFlip = $sprite.hflip -eq '1'
            vFlip = $sprite.vflip -eq '1'
        }
    }
    [ordered]@{
        id = Convert-Number ([string]$object.id)
        name = [string]$object.name
        description = [string]$object.desc
        sprites = @($sprites)
    }
}

$entries | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Output -Encoding utf8
