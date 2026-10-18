# Re-render the existing WPF TyprIcon resource; no separate logo design to maintain.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$projectDir = Join-Path $PSScriptRoot '../src/Typr.Windows'
[xml]$app = Get-Content -LiteralPath (Join-Path $projectDir 'App.xaml') -Raw
$namespaces = [System.Xml.XmlNamespaceManager]::new($app.NameTable)
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$iconXml = $app.SelectSingleNode('//*[@x:Key="TyprIcon"]', $namespaces).CloneNode($true)
$iconXml.RemoveAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
$drawing = [System.Windows.Markup.XamlReader]::Parse($iconXml.OuterXml)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, [System.Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $images += ,$stream.ToArray()
    $stream.Dispose()
}
$assetsDir = Join-Path $projectDir 'Assets'
[System.IO.Directory]::CreateDirectory($assetsDir) | Out-Null
$path = Join-Path $assetsDir 'Typr.ico'
$file = [System.IO.File]::Create($path)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose() }
Write-Output $path
