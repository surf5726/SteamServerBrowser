param(
    [Parameter(Mandatory = $true)]
    [string]$OriginalSource,
    [string]$Destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../ServerBrowser.Desktop/Assets/Flags'))
)
$ErrorActionPreference = 'Stop'
# Extract existing PNG bytes; never deserialize the DevExpress/BinaryFormatter object.
[xml]$resx = Get-Content -LiteralPath (Join-Path $OriginalSource 'ServerBrowser/ServerBrowserForm.resx') -Raw
$resource = $resx.root.data | Where-Object name -eq 'imgFlags.ImageStream'
$bytes = [Convert]::FromBase64String($resource.value)
$designer = [IO.File]::ReadAllText((Join-Path $OriginalSource 'ServerBrowser/ServerBrowserForm.Designer.cs'))
$keys = [regex]::Matches($designer, 'imgFlags\.Images\.SetKeyName\((\d+), "([a-z]{2}\.png)"\)') |
    Sort-Object { [int]$_.Groups[1].Value }
$offsets = [Collections.Generic.List[int]]::new()
for ($i = 0; $i -le $bytes.Length - 8; $i++) {
    if ($bytes[$i] -eq 137 -and $bytes[$i+1] -eq 80 -and $bytes[$i+2] -eq 78 -and $bytes[$i+3] -eq 71 -and
        $bytes[$i+4] -eq 13 -and $bytes[$i+5] -eq 10 -and $bytes[$i+6] -eq 26 -and $bytes[$i+7] -eq 10) {
        $offsets.Add($i)
    }
}
if ($offsets.Count -ne $keys.Count -or $keys.Count -eq 0) {
    throw "PNG/key count mismatch: $($offsets.Count) PNGs, $($keys.Count) keys"
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
for ($i = 0; $i -lt $keys.Count; $i++) {
    if ([int]$keys[$i].Groups[1].Value -ne $i) { throw 'Non-contiguous flag indices' }
    $start = $offsets[$i]
    $cursor = $start + 8
    $ended = $false
    while ($cursor + 12 -le $bytes.Length) {
        $length = [uint32]$bytes[$cursor] * 16777216 + [uint32]$bytes[$cursor+1] * 65536 +
                  [uint32]$bytes[$cursor+2] * 256 + [uint32]$bytes[$cursor+3]
        $type = [Text.Encoding]::ASCII.GetString($bytes, $cursor + 4, 4)
        if ($cursor + 12 + $length -gt $bytes.Length) { throw 'Invalid PNG chunk length' }
        $cursor += 12 + $length
        if ($type -eq 'IEND') { $ended = $true; break }
    }
    if (!$ended) { throw 'Missing PNG end marker' }
    $png = [byte[]]::new($cursor - $start)
    [Array]::Copy($bytes, $start, $png, 0, $png.Length)
    [IO.File]::WriteAllBytes((Join-Path $Destination $keys[$i].Groups[2].Value), $png)
}
Write-Output "Extracted $($keys.Count) original country flags to $Destination"
