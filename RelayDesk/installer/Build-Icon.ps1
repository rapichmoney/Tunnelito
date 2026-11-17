$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$stream = [IO.File]::Create((Join-Path $assets 'RelayDesk.ico'))
$writer = New-Object IO.BinaryWriter($stream)
try {
    $images = @()
    foreach ($size in @(16,24,32,48,64,128,256)) {
        $bitmap = New-Object Drawing.Bitmap($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#101318'))
        $graphics.ScaleTransform($size/64.0,$size/64.0)
        $ink=[Drawing.ColorTranslator]::FromHtml('#ECEEF2')
        $pen = New-Object Drawing.Pen($ink,4)
        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $route=New-Object Drawing.Drawing2D.GraphicsPath
        $route.AddLine(12,46,24,46);$route.AddBezier(24,46,32,46,32,46,32,38)
        $route.AddLine(32,38,32,26);$route.AddBezier(32,26,32,18,32,18,40,18);$route.AddLine(40,18,52,18)
        $graphics.DrawPath($pen,$route)
        $brush=New-Object Drawing.SolidBrush($ink);$graphics.FillEllipse($brush,7,41,10,10)
        $dark=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#101318'));$graphics.FillEllipse($dark,47,13,10,10)
        $pen.Width=3;$graphics.DrawEllipse($pen,47,13,10,10)
        $route.Dispose();$brush.Dispose();$dark.Dispose()
        $memory = New-Object IO.MemoryStream
        $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
        $images += ,@($size,$memory.ToArray())
        $memory.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16*$images.Count
    foreach ($entry in $images) {
        $dimension = if ($entry[0] -eq 256) { 0 } else { $entry[0] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$entry[1].Length); $writer.Write([uint32]$offset)
        $offset += $entry[1].Length
    }
    foreach ($entry in $images) { $writer.Write([byte[]]$entry[1]) }
} finally { $writer.Dispose(); $stream.Dispose() }
