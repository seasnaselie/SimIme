param(
  [string]$Root = (Split-Path -Parent $PSScriptRoot),
  [string]$Out = '',
  [switch]$Exe,
  [string]$Main = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($Out)) {
  if ($Exe) { $Out = Join-Path $Root '_devA\SimIme.Engine.exe' } else { $Out = Join-Path $Root '_devA\SimIme.Engine.dll' }
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw ('csc not found: ' + $csc) }

# 1) 所有 .cs 必须是 UTF-8 带 BOM（C# 5 编译器 + 中文字面量，必须）
$enc = New-Object System.Text.UTF8Encoding($true)
$srcDir = Join-Path $Root 'src\Engine'
$sources = @(Get-ChildItem (Join-Path $srcDir '*.cs') | Sort-Object Name)
foreach ($f in $sources) {
  $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
  $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
  if (-not $hasBom) {
    $text = [System.IO.File]::ReadAllText($f.FullName, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($f.FullName, $text, $enc)
    Write-Host ('BOM added: ' + $f.Name)
  }
}

# 2) 资源（名称一字不差，App 构建脚本应照抄这三行）
$assetsDir = Join-Path $Root 'src\Assets'
$resourceArgs = @()
foreach ($a in @('pinyin_full.tsv', 'char_freq.tsv', 'common_words.tsv')) {
  $p = Join-Path $assetsDir $a
  if (Test-Path $p) { $resourceArgs += ('/resource:' + $p + ',SimIme.Assets.' + $a) }
  else { Write-Warning ('asset missing: ' + $p) }
}

$outDirFull = Split-Path -Parent $Out
if (-not [string]::IsNullOrEmpty($outDirFull) -and -not (Test-Path $outDirFull)) {
  New-Item -ItemType Directory -Force -Path $outDirFull | Out-Null
}

$cscArgs = @('/nologo')
if ($Exe) { $cscArgs += '/target:exe' } else { $cscArgs += '/target:library' }
$cscArgs += '/codepage:65001'
$cscArgs += '/optimize+'
$cscArgs += ('/out:' + $Out)
$cscArgs += $resourceArgs
if ($Exe -and -not [string]::IsNullOrEmpty($Main)) { $cscArgs += ('/main:' + $Main) }
foreach ($f in $sources) { $cscArgs += $f.FullName }

$output = & $csc $cscArgs 2>&1
$code = $LASTEXITCODE
foreach ($line in $output) { Write-Host $line }
Write-Host ('CSC_EXIT=' + $code)
if ($code -ne 0) { exit $code }
Write-Host ('OUT=' + $Out + ' BYTES=' + (Get-Item $Out).Length)
exit 0
