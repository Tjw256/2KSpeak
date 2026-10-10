# Renders src/TwoKSpeak.App/Ui/Icons.xaml into the multi-size application icon (PNG-compressed ICO entries).
# Run with Windows PowerShell after changing the icon artwork:  powershell -STA -File tools/render-app-icon.ps1
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$dict = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $Root 'src\TwoKSpeak.App\Ui\Icons.xaml')))
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
  $image = if ($size -le 20) { $dict['IconSmall'] } else { $dict['IconLarge'] }
  $visual = New-Object Windows.Media.DrawingVisual; $ctx = $visual.RenderOpen()
  $ctx.DrawImage($image, (New-Object Windows.Rect 0, 0, $size, $size)); $ctx.Close()
  $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $bmp.Render($visual)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object IO.MemoryStream; $enc.Save($ms); ,$ms.ToArray()
}
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$pngs[$i].Length); $w.Write([int]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$target = Join-Path $Root 'src\TwoKSpeak.App\Assets\2KSpeak.ico'
[IO.File]::WriteAllBytes($target, $out.ToArray())
"wrote $target ($($out.Length) bytes, sizes $($sizes -join ','))"
