$ErrorActionPreference = 'Stop'
$root = 'd:\OpenSource\KTexturePacker'
$preview = Join-Path $root 'preview_test'
$in = (Join-Path $preview 'in').Replace('\', '/')

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = "run --project $root\KTexturePacker.Web\KTexturePacker.Web.csproj --urls http://localhost:5056"
$psi.WorkingDirectory = "$root\KTexturePacker.Web"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 15

function Curl($name, $url, $body) {
    $tmp = Join-Path $preview ($name + '_resp.txt')
    $arg = "-s", "-i", "-X", "POST", $url, "-H", "Content-Type: application/x-www-form-urlencoded", "--data", $body
    & curl.exe @arg | Out-File $tmp -Encoding UTF8
    "=== $name (saved $tmp) ===" | Out-File (Join-Path $preview 'diag.log') -Append
    Get-Content $tmp | Out-File (Join-Path $preview 'diag.log') -Append
}

'' | Set-Content (Join-Path $preview 'diag.log')
Curl 'preview' 'http://localhost:5056/api/preview' "inputFolder=$in&maxSize=512&padding=2&algorithm=best&allowRotation=false"
Curl 'pack' 'http://localhost:5056/api/pack' "inputFolder=$in&outputFolder=$((Join-Path $preview 'out').Replace('\','/'))&maxSize=512&padding=2&algorithm=best&allowRotation=false&format=json"

# 同时 dump app stdout（前若干行）
"=== APP STDOUT/ERR ===" | Out-File (Join-Path $preview 'diag.log') -Append
$proc.StandardOutput.ReadToEnd() | Out-File (Join-Path $preview 'diag.log') -Append
$proc.StandardError.ReadToEnd() | Out-File (Join-Path $preview 'diag.log') -Append

try { $proc.Kill() } catch {}
"=== DONE ===" | Out-File (Join-Path $preview 'diag.log') -Append
