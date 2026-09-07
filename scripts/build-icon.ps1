$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$frames=@()
foreach($size in @(16,24,32,48,64,128,256)) {
    $bitmap=New-Object System.Drawing.Bitmap($size,$size)
    $g=[System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size/256.0,$size/256.0)
    $g.Clear([System.Drawing.Color]::Transparent)
    $path=New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8,8,64,64,180,90);$path.AddArc(184,8,64,64,270,90)
    $path.AddArc(184,184,64,64,0,90);$path.AddArc(8,184,64,64,90,90);$path.CloseFigure()
    $bg=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,34,37,47))
    $purple=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,163,138,245))
    $white=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,244,241,255))
    $g.FillPath($bg,$path)
    $g.FillRectangle($white,54,55,29,147);$g.FillRectangle($white,54,176,147,26)
    $g.FillRectangle($purple,104,114,28,47);$g.FillRectangle($purple,151,78,28,83)
    $stream=New-Object System.IO.MemoryStream
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames+=,@{Size=$size;Bytes=$stream.ToArray()}
    if($size -eq 256){[System.IO.File]::WriteAllBytes((Join-Path $root 'assets/UsageLoom.png'),$stream.ToArray())}
    $stream.Dispose();$g.Dispose();$bitmap.Dispose();$path.Dispose();$bg.Dispose();$purple.Dispose();$white.Dispose()
}
$output=[System.IO.File]::Create((Join-Path $root 'assets/UsageLoom.ico'))
$writer=New-Object System.IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
    $offset=6+16*$frames.Count
    foreach($frame in $frames){$dimension=if($frame.Size -eq 256){0}else{$frame.Size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$frame.Bytes.Length}
    foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
} finally {$writer.Dispose();$output.Dispose()}
