$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$source=[Drawing.Bitmap]::FromFile((Join-Path $root 'assets/app-icon/source.png'))
function Png([int]$Size,[int]$Inset=0){
    $bitmap=[Drawing.Bitmap]::new($Size,$Size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($source,$Inset,$Inset,$Size-2*$Inset,$Size-2*$Inset)
    $memory=[IO.MemoryStream]::new()
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $bytes=$memory.ToArray()
    $graphics.Dispose();$bitmap.Dispose();$memory.Dispose()
    return ,$bytes
}
function SavePng([string]$Path,[int]$Size,[int]$Inset=0){
    $file=Join-Path $root $Path
    New-Item -ItemType Directory -Force (Split-Path $file -Parent)|Out-Null
    [IO.File]::WriteAllBytes($file,(Png $Size $Inset))
}
function BigEndian([IO.BinaryWriter]$Writer,[int]$Value){
    $bytes=[BitConverter]::GetBytes($Value);[Array]::Reverse($bytes);$Writer.Write($bytes)
}
try{
    SavePng 'assets/app-icon/icon.png' 1024
    SavePng 'macos/src-tauri/icons/icon.png' 1024
    $frames=@(16,24,32,48,64,128,256)|ForEach-Object { @{size=$_;bytes=(Png $_)} }
    $stream=[IO.File]::Create((Join-Path $root 'assets/app-icon/icon.ico'))
    $writer=[IO.BinaryWriter]::new($stream)
    try{
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
        $offset=6+16*$frames.Count
        foreach($frame in $frames){$size=if($frame.size -eq 256){0}else{$frame.size};$writer.Write([byte]$size);$writer.Write([byte]$size);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.bytes.Length);$writer.Write([uint32]$offset);$offset+=$frame.bytes.Length}
        foreach($frame in $frames){$writer.Write([byte[]]$frame.bytes)}
    }finally{$writer.Dispose();$stream.Dispose()}
    $frames=@(@{type='icp4';size=16},@{type='icp5';size=32},@{type='icp6';size=64},@{type='ic07';size=128},@{type='ic08';size=256},@{type='ic09';size=512},@{type='ic10';size=1024})
    $length=8
    foreach($frame in $frames){$frame.bytes=Png $frame.size;$length+=8+$frame.bytes.Length}
    $stream=[IO.File]::Create((Join-Path $root 'macos/src-tauri/icons/icon.icns'));$writer=[IO.BinaryWriter]::new($stream)
    try{$writer.Write([Text.Encoding]::ASCII.GetBytes('icns'));BigEndian $writer $length;foreach($frame in $frames){$writer.Write([Text.Encoding]::ASCII.GetBytes($frame.type));BigEndian $writer (8+$frame.bytes.Length);$writer.Write([byte[]]$frame.bytes)}}finally{$writer.Dispose();$stream.Dispose()}
    foreach($density in @(@{name='mdpi';size=48},@{name='hdpi';size=72},@{name='xhdpi';size=96},@{name='xxhdpi';size=144},@{name='xxxhdpi';size=192})){
        SavePng "android/app/src/main/res/mipmap-$($density.name)/ic_launcher.png" $density.size
    }
    SavePng 'android/app/src/main/res/drawable-nodpi/ic_launcher_foreground.png' 432 40
}finally{$source.Dispose()}
