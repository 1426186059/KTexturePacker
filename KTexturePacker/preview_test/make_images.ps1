$ErrorActionPreference = 'Stop'

function Write-U32BE($bw, [uint32]$v) {
    $bw.Write([byte](($v -shr 24) -band 0xFF))
    $bw.Write([byte](($v -shr 16) -band 0xFF))
    $bw.Write([byte](($v -shr 8)  -band 0xFF))
    $bw.Write([byte]($v -band 0xFF))
}

function Compress-Deflate([byte[]]$data) {
    $out = New-Object System.IO.MemoryStream
    $ds = New-Object System.IO.Compression.DeflateStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
    $ds.Write($data, 0, $data.Length)
    $ds.Close()
    return $out.ToArray()
}

function Compute-Crc32([byte[]]$bytes) {
    if (-not $script:crcTable) {
        $script:crcTable = New-Object uint32[] 256
        for ($n = 0; $n -lt 256; $n++) {
            $c = [uint32]$n
            for ($k = 0; $k -lt 8; $k++) { $c = $(if ($c -band 1) { 0xEDB88320 -bxor ($c -shr 1) } else { $c -shr 1 }) }
            $script:crcTable[$n] = $c
        }
    }
    $c = 0xFFFFFFFF
    foreach ($b in $bytes) { $c = $script:crcTable[($c -bxor $b) -band 0xFF] -bxor ($c -shr 8) }
    return ($c -bxor 0xFFFFFFFF)
}

function Write-Chunk($bw, [string]$type, [byte[]]$data) {
    Write-U32BE $bw ([uint32]$data.Length)
    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($type)
    $bw.Write($typeBytes)
    $bw.Write($data)
    $crc = Compute-Crc32 ($typeBytes + $data)
    Write-U32BE $bw $crc
}

function New-Png([string]$path, [int]$w, [int]$h, [byte[]]$rgb) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([byte[]]@(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A))
    $ih = New-Object System.IO.MemoryStream; $ib = New-Object System.IO.BinaryWriter($ih)
    Write-U32BE $ib ([uint32]$w); Write-U32BE $ib ([uint32]$h)
    $ib.Write([byte]8); $ib.Write([byte]2); $ib.Write([byte]0); $ib.Write([byte]0); $ib.Write([byte]0)
    Write-Chunk $bw "IHDR" $ih.ToArray()
    $raw = New-Object System.IO.MemoryStream; $rb = New-Object System.IO.BinaryWriter($raw)
    for ($y = 0; $y -lt $h; $y++) { $rb.Write([byte]0); for ($x = 0; $x -lt $w; $x++) { $rb.Write($rgb) } }
    $def = Compress-Deflate $raw.ToArray()
    Write-Chunk $bw "IDAT" $def
    Write-Chunk $bw "IEND" @()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
}

New-Png 'a.png' 32 24 @(200,60,60)
New-Png 'b.png' 16 16 @(60,200,60)
New-Png 'c.png' 48 12 @(60,60,200)
Write-Host "DONE a=$([System.IO.File]::ReadAllBytes('a.png').Length) b=$([System.IO.File]::ReadAllBytes('b.png').Length) c=$([System.IO.File]::ReadAllBytes('c.png').Length)"
