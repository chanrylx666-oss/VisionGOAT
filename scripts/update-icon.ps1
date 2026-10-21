param(
    [string]$SourceImage = (Join-Path $PSScriptRoot '../assets/jpVision.png'),
    [string]$OutputIcon = (Join-Path $PSScriptRoot '../assets/jpVision.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$sourcePath = [IO.Path]::GetFullPath($SourceImage)
$outputPath = [IO.Path]::GetFullPath($OutputIcon)
if($sourcePath -eq $outputPath){throw 'The source image and output icon must be different files.'}
$stream = [IO.File]::OpenRead($sourcePath)
try {
    $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($stream,
        [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $source = $decoder.Frames[0]
    $source.Freeze()
} finally { $stream.Dispose() }
if($source.PixelWidth -ne $source.PixelHeight){throw 'Use a square source image to preserve its proportions.'}
$rgba = [Windows.Media.Imaging.FormatConvertedBitmap]::new($source,[Windows.Media.PixelFormats]::Bgra32,$null,0)
$rgba.Freeze()
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach($size in $sizes){
    $scale = [Windows.Media.ScaleTransform]::new($size/[double]$source.PixelWidth,$size/[double]$source.PixelHeight)
    $scaled = [Windows.Media.Imaging.TransformedBitmap]::new($rgba,$scale)
    $scaled.Freeze()
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
    $buffer = [IO.MemoryStream]::new()
    try { $encoder.Save($buffer); $frames.Add($buffer.ToArray()) } finally { $buffer.Dispose() }
}
# ICO directory followed by lossless PNG frames; 0 represents 256 pixels.
$icon = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset = [uint32](6 + 16 * $sizes.Count)
    for($index=0;$index -lt $sizes.Count;++$index){
        $dimension = if($sizes[$index] -eq 256){[byte]0}else{[byte]$sizes[$index]}
        $writer.Write($dimension);$writer.Write($dimension)
        $writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length);$writer.Write($offset)
        $offset += [uint32]$frames[$index].Length
    }
    foreach($frame in $frames){$writer.Write($frame)}
    $writer.Flush()
    [IO.File]::WriteAllBytes($outputPath,$icon.ToArray())
} finally { $writer.Dispose();$icon.Dispose() }
Write-Output "Application icon generated: $outputPath (16 to 256 pixels, $($sizes.Count) sizes)."
