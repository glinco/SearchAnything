# Renders every test scene in scenes.html to data/screens/sceneNN.png (1920x1080) plus
# sceneNN.json with the ground-truth boxes, using headless Microsoft Edge.
# Run fetch-photos.ps1 first.
# Not 'Stop': Edge logs harmless messages to stderr, which Windows PowerShell treats as errors.
$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$out = Join-Path $here '..\data\screens'
New-Item -ItemType Directory -Force $out | Out-Null
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found.' }

$page = ([Uri](Join-Path $here 'scenes.html')).AbsoluteUri
$common = @('--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', '--window-size=1920,1080',
            '--allow-file-access-from-files', '--virtual-time-budget=5000', '--no-first-run', '--disable-extensions')

$scene = 0
$count = 1
while ($scene -lt $count) {
    $name = 'scene{0:D2}' -f $scene
    $url = "$page`?scene=$scene"
    $dom = & $edge @common --dump-dom $url 2>$null | Out-String
    if ($dom -notmatch '(?s)<script id="gt" type="application/json">(.*?)</script>') { throw "No ground truth in $name" }
    $json = $Matches[1]
    $count = ($json | ConvertFrom-Json).count
    Set-Content -Encoding utf8 (Join-Path $out "$name.json") $json
    & $edge @common "--screenshot=$(Join-Path $out "$name.png")" $url 2>$null | Out-Null
    Write-Host "$name rendered"
    $scene++
}
