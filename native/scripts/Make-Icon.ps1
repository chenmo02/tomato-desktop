$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconSource = Join-Path $PSScriptRoot '..\DesktopNook.App\Assets\AppIcon.png'
$iconOutput = Join-Path $PSScriptRoot '..\DesktopNook.App\Assets\AppIcon.ico'
$image = [System.Drawing.Image]::FromFile([IO.Path]::GetFullPath($iconSource))
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
# ICO 的每一帧都写成标准 32bpp DIB（BITMAPINFOHEADER + BGRA 像素 + AND 掩码）。
# 早期版本把所有尺寸都写成 PNG 数据：Win32 图标资源、任务栏按钮和快捷方式只认 DIB，
# 于是任务栏会退回默认的白色文档图标。只有 DIB 帧才能保证所有入口都显示番茄图标。
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($image,0,0,$size,$size)
        $graphics.Dispose()

        # LockBits 拿到的是自上而下的 BGRA 行；DIB 要求自下而上，写帧时倒序逐行输出。
        $rect = [System.Drawing.Rectangle]::new(0,0,$size,$size)
        $locked = $bitmap.LockBits($rect,[System.Drawing.Imaging.ImageLockMode]::ReadOnly,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $raw = [byte[]]::new($locked.Stride * $size)
        try { [Runtime.InteropServices.Marshal]::Copy($locked.Scan0,$raw,0,$raw.Length) }
        finally { $bitmap.UnlockBits($locked); $bitmap.Dispose() }
        $rowSize = $size * 4
        $stride = $locked.Stride
        # AND 掩码按 32 位（4 字节）对齐，全 0 表示完全使用 32bpp 的 alpha 通道。
        $maskStride = [int][Math]::Ceiling($size / 32) * 4
        $mask = [byte[]]::new($maskStride * $size)

        $stream = [IO.MemoryStream]::new()
        $writer = [IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint32]40)                 # biSize
            $writer.Write([int32]$size)                # biWidth
            $writer.Write([int32]($size * 2))          # biHeight：XOR 图与 AND 掩码各占一半
            $writer.Write([uint16]1)                   # biPlanes
            $writer.Write([uint16]32)                  # biBitCount
            $writer.Write([uint32]0)                   # biCompression = BI_RGB
            $writer.Write([uint32]($rowSize * $size + $mask.Length)) # biSizeImage
            $writer.Write([int32]0)                    # biXPelsPerMeter
            $writer.Write([int32]0)                    # biYPelsPerMeter
            $writer.Write([uint32]0)                   # biClrUsed
            $writer.Write([uint32]0)                   # biClrImportant
            for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($raw,$row * $stride,$rowSize) }
            $writer.Write($mask)
            $writer.Flush()
            $frames.Add($stream.ToArray())
        } finally { $writer.Dispose(); $stream.Dispose() }
    }
    $file = [IO.File]::Create([IO.Path]::GetFullPath($iconOutput))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $image.Dispose() }
Get-Item -LiteralPath $iconOutput | Select-Object FullName,Length
