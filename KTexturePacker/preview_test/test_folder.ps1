$ErrorActionPreference = 'Stop'
$root = 'd:\OpenSource\KTexturePacker'
$preview = Join-Path $root 'preview_test'
$in = Join-Path $preview 'in'
$out = Join-Path $preview 'out'
$log = Join-Path $preview 'test.log'
'' | Set-Content $log
function Log($m) { $m | Out-File $log -Append }

# 启动应用
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = "run --project $root\KTexturePacker.Web\KTexturePacker.Web.csproj --urls http://localhost:5055"
$psi.WorkingDirectory = "$root\KTexturePacker.Web"
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 15

function PostForm($url, $fields) {
    $body = @()
    foreach ($k in $fields.Keys) { $body += "$k=$([Uri]::EscapeDataString($fields[$k]))" }
    $data = $body -join '&'
    return Invoke-WebRequest -Uri $url -Method POST -ContentType 'application/x-www-form-urlencoded' -Body $data -UseBasicParsing
}

try {
    $r = PostForm 'http://localhost:5055/api/preview' @{ inputFolder = $in; maxSize = '512'; padding = '2'; algorithm = 'best'; allowRotation = 'false' }
    $pv = Join-Path $preview 'pv.png'
    [System.IO.File]::WriteAllBytes($pv, $r.Content)
    Log "PREVIEW status=$($r.StatusCode) bytes=$($r.Content.Length) W=$($r.Headers['X-Atlas-Width']) H=$($r.Headers['X-Atlas-Height']) sprites=$($r.Headers['X-Sprite-Count']) unplaced=$($r.Headers['X-Unplaced-Count'])"
} catch {
    Log "PREVIEW ERR: $($_.Exception.Message)"
    if ($_.Exception.Response) {
        $sr = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        Log "PREVIEW BODY: $($sr.ReadToEnd())"
    }
}

try {
    $r2 = PostForm 'http://localhost:5055/api/pack' @{ inputFolder = $in; outputFolder = $out; maxSize = '512'; padding = '2'; algorithm = 'best'; allowRotation = 'false'; format = 'json' }
    Log "PACK status=$($r2.StatusCode)"
    Log "PACK body=$($r2.Content)"
    Log "OUT atlas.png exists=$(Test-Path (Join-Path $out 'atlas.png'))"
    Log "OUT atlas.json exists=$(Test-Path (Join-Path $out 'atlas.json'))"
} catch {
    Log "PACK ERR: $($_.Exception.Message)"
    if ($_.Exception.Response) {
        $sr = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        Log "PACK BODY: $($sr.ReadToEnd())"
    }
}

try { $proc.Kill() } catch {}
Log "DONE"
