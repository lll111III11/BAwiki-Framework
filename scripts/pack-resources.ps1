param(
  [string]$Source = 'E:\kivo_wiki',
  [string]$Dest = 'D:\BAwiki-Resources',
  [ValidateSet('Lite', 'Full')]
  [string]$Profile = 'Lite',
  [int]$VolumeMB = 3900
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "Source not found: $Source" }
New-Item -ItemType Directory -Path $Dest -Force | Out-Null

$lite = @('01_学生', '02_官方画廊', '03_游戏音乐', '06_回忆大厅', '09_物品图标', '10_无损音乐')
$full = @('01_学生', '02_官方画廊', '03_游戏音乐', '04_官方PV视频', '05_B站视频', '06_回忆大厅',
          '07_3D模型', '09_物品图标', '10_无损音乐', '_BA模型库')

$dirs = if ($Profile -eq 'Lite') { $lite } else { $full }
$include = @()
foreach ($d in $dirs) {
  $p = Join-Path $Source $d
  if (Test-Path $p) { $include += $p } else { Write-Warning "Skip missing: $d" }
}
if ($include.Count -eq 0) { throw 'No resource directories selected.' }

$stamp = Get-Date -Format 'yyyyMMdd'
$base = Join-Path $Dest "BAwiki-Resources-$Profile-$stamp"

# Never pack caches, secrets, logs or imports.
$exclude = @('_本地维基', '_inbox', '_BA提取试点', '_keys', '*.log', 'accounts.json', 'ai.json', 'console_*.txt')

$sevenZip = @(
  'C:\Program Files\7-Zip\7z.exe',
  'C:\Program Files (x86)\7-Zip\7z.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($sevenZip) {
  $list = Join-Path $Dest 'filelist.txt'
  Set-Content -Path $list -Value ($include | ForEach-Object { $_ }) -Encoding UTF8
  & $sevenZip a -t7z -mx=1 -mmt=on "-v${VolumeMB}m" "$base.7z" "@$list" "-x!$($exclude -join ';')"
} elseif (Test-Path 'C:\Program Files\WinRAR\WinRAR.exe') {
  $rar = 'C:\Program Files\WinRAR\WinRAR.exe'
  & $rar a -m1 -r -ep1 "-v${VolumeMB}m" "$base.rar" $include `
    '-x*_本地维基*' '-x*_inbox*' '-x*_BA提取试点*' '-x*_keys*' '-x*accounts.json' '-x*ai.json'
} else {
  throw 'Neither 7-Zip nor WinRAR found. Install one, or compress manually.'
}

$sum = Join-Path $Dest 'SHA256SUMS.txt'
Get-ChildItem "$base.*" | Sort-Object Name | ForEach-Object {
  $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
  "$h  $($_.Name)"
} | Set-Content $sum -Encoding UTF8

$total = (Get-ChildItem $include -Recurse -File | Measure-Object Length -Sum).Sum
[pscustomobject]@{
  Profile = $Profile
  Archives = (Get-ChildItem "$base.*").Count
  SourceGB = [math]::Round($total / 1GB, 2)
  Dest = $Dest
  Checksums = $sum
} | Format-List
