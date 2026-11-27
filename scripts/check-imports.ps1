# Fails unless every DLL the executable imports statically is one Windows loads from System32:
# an API set (api-ms-*, ext-ms-*), a KnownDLL, or any DLL when the image's load configuration
# carries DependentLoadFlags LOAD_LIBRARY_SEARCH_SYSTEM32 (link /DEPENDENTLOADFLAG:0x800).
# Otherwise it would be looked for first in the executable's own folder, which for the per-user
# install the user can write.
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'

function Read-Imports([string]$file) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) { throw "$file is not a PE image" }
    $sections = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optional = $pe + 24
    $optionalSize = [BitConverter]::ToUInt16($bytes, $pe + 20)
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    $directories = $optional + $(if ($magic -eq 0x20B) { 112 } else { 96 })
    $table = $optional + $optionalSize
    function Offset([uint32]$rva) {
        for ($i = 0; $i -lt $sections; $i++) {
            $s = $table + $i * 40
            $va = [BitConverter]::ToUInt32($bytes, $s + 12)
            $size = [Math]::Max([BitConverter]::ToUInt32($bytes, $s + 8), [BitConverter]::ToUInt32($bytes, $s + 16))
            if ($rva -ge $va -and $rva -lt $va + $size) { return $rva - $va + [BitConverter]::ToUInt32($bytes, $s + 20) }
        }
        throw "RVA $rva is in no section"
    }
    function Name([uint32]$rva) {
        $at = Offset $rva
        $end = [Array]::IndexOf($bytes, [byte]0, [int]$at)
        [Text.Encoding]::ASCII.GetString($bytes, $at, $end - $at)
    }
    $names = @()
    foreach ($entry in @(@{ Dir = 1; Size = 20; NameAt = 12 }, @{ Dir = 13; Size = 32; NameAt = 4 })) {
        $rva = [BitConverter]::ToUInt32($bytes, $directories + $entry.Dir * 8)
        if ($rva -eq 0) { continue }
        $at = Offset $rva
        while ($true) {
            $nameRva = [BitConverter]::ToUInt32($bytes, $at + $entry.NameAt)
            if ($nameRva -eq 0) { break }
            $names += (Name $nameRva).ToLowerInvariant()
            $at += $entry.Size
        }
    }
    # IMAGE_LOAD_CONFIG_DIRECTORY64.DependentLoadFlags, a WORD at 0x4E.
    $flags = 0
    $loadConfig = [BitConverter]::ToUInt32($bytes, $directories + 10 * 8)
    if ($loadConfig -ne 0 -and $magic -eq 0x20B) {
        $at = Offset $loadConfig
        if ([BitConverter]::ToUInt32($bytes, $at) -ge 0x50) { $flags = [BitConverter]::ToUInt16($bytes, $at + 0x4E) }
    }
    [pscustomobject]@{ Names = @($names | Sort-Object -Unique); DependentLoadFlags = $flags }
}

$known = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs').PSObject.Properties |
    Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Value)".ToLowerInvariant() })
$image = Read-Imports $Path
$system32Only = ($image.DependentLoadFlags -band 0x800) -ne 0
$bad = @($image.Names | Where-Object { $_ -notmatch '^(api|ext)-ms-' -and $_ -notin $known })
if ($bad -and -not $system32Only) { throw "$Path imports DLLs Windows may load from its own folder: $($bad -join ', ')" }
Write-Host "$(Split-Path -Leaf $Path) imports $($image.Names -join ', '); DependentLoadFlags 0x$('{0:X}' -f $image.DependentLoadFlags)"
