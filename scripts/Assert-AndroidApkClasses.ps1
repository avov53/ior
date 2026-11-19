param([Parameter(Mandatory=$true)][string]$Apk)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required=@('Lapp/ior/android/MainActivity;','Lapp/ior/android/QrActivity;','Lapp/ior/android/IorVpnService;')
$missing=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($name in $required){[void]$missing.Add($name)}
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk).Path)
try {
    foreach($entry in $zip.Entries){
        if($entry.FullName -notmatch '^classes(?:[0-9]+)?\.dex$'){continue}
        $inputStream=$entry.Open()
        $buffer=[IO.MemoryStream]::new()
        try{$inputStream.CopyTo($buffer);$bytes=$buffer.ToArray()}finally{$inputStream.Dispose();$buffer.Dispose()}
        # DEX class_defs -> type_ids -> string_ids -> descriptor. References alone do not define a class.
        $strings=[BitConverter]::ToUInt32($bytes,60)
        $types=[BitConverter]::ToUInt32($bytes,68)
        $count=[BitConverter]::ToUInt32($bytes,96)
        $classes=[BitConverter]::ToUInt32($bytes,100)
        for($i=0;$i -lt $count -and $missing.Count;$i++){
            $type=[BitConverter]::ToUInt32($bytes,$classes+32*$i)
            $index=[BitConverter]::ToUInt32($bytes,$types+4*$type)
            $start=[BitConverter]::ToUInt32($bytes,$strings+4*$index)
            while($bytes[$start++] -ge 128){}
            $end=[Array]::IndexOf($bytes,[byte]0,[int]$start)
            [void]$missing.Remove([Text.Encoding]::ASCII.GetString($bytes,$start,$end-$start))
        }
    }
}finally{$zip.Dispose()}
if($missing.Count){throw "APK missing Android class definitions: $($missing -join ', ')"}
Write-Host 'Android launcher, QR activity and VPN service verified in APK.'
