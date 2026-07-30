$ErrorActionPreference = 'Stop'
$root = 'd:\OpenSource\KTexturePacker'
$web = Join-Path $root 'KTexturePacker.Web'
$test = Join-Path $root 'preview_test'
$port = 5055
$base = "http://localhost:$port"

$p = Start-Process -FilePath 'dotnet' -ArgumentList 'run','-c','Debug','--no-build','--urls',"http://localhost:$port" -WorkingDirectory $web -RedirectStandardOutput (Join-Path $test 'app.out.log') -RedirectStandardError (Join-Path $test 'app.err.log') -PassThru
Write-Host "started pid=$($p.Id)"

$ready = $false
for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 1
    $code = & curl.exe -s -o $null -w "%{http_code}" "$base/" 2>$null
    if ($code -eq '200') { $ready = $true; break }
}
if (-not $ready) { Write-Host "SERVER_NOT_READY"; $p.Kill(); exit 1 }
Write-Host "READY"

& curl.exe -s -F "files=@$(Join-Path $test 'a.png')" -F "files=@$(Join-Path $test 'b.png')" -F "files=@$(Join-Path $test 'c.png')" -F "algorithm=best" -F "maxSize=2048" -F "padding=2" -D (Join-Path $test 'hdr_best.txt') -o (Join-Path $test 'pv_best.png') "$base/api/preview"
& curl.exe -s -F "files=@$(Join-Path $test 'a.png')" -F "files=@$(Join-Path $test 'b.png')" -F "files=@$(Join-Path $test 'c.png')" -F "algorithm=contact" -F "maxSize=2048" -F "padding=2" -D (Join-Path $test 'hdr_contact.txt') -o (Join-Path $test 'pv_contact.png') "$base/api/preview"
& curl.exe -s -o $null -w "root_html=%{http_code}`n" "$base/"

function Is-Png($f){ $b=[System.IO.File]::ReadAllBytes($f); return ($b.Length -ge 8 -and $b[0]-eq 0x89 -and $b[1]-eq 0x50 -and $b[2]-eq 0x4E -and $b[3]-eq 0x47) }
Write-Host "best valid=$(Is-Png (Join-Path $test 'pv_best.png')) size=$((Get-Item (Join-Path $test 'pv_best.png')).Length)"
Write-Host "contact valid=$(Is-Png (Join-Path $test 'pv_contact.png')) size=$((Get-Item (Join-Path $test 'pv_contact.png')).Length)"

$hb = [System.IO.File]::ReadAllText((Join-Path $test 'hdr_best.txt'))
$hc = [System.IO.File]::ReadAllText((Join-Path $test 'hdr_contact.txt'))
Write-Host "=== best headers ===`n$hb"
Write-Host "=== contact headers ===`n$hc"

$bh = (Get-FileHash (Join-Path $test 'pv_best.png') -Algorithm MD5).Hash
$ch = (Get-FileHash (Join-Path $test 'pv_contact.png') -Algorithm MD5).Hash
Write-Host "bestHash=$bh"
Write-Host "contactHash=$ch"
Write-Host "differ=$($bh -ne $ch)"

$p.Kill(); $p.WaitForExit()
Write-Host "STOPPED"
