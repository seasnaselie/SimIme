param(
  [string]$Root = (Split-Path -Parent $PSScriptRoot),
  [ValidateSet('pypinyin', 'jieba')]
  [string]$Source = 'pypinyin',
  [string]$Package = '',
  [string]$Mirror = 'http://mirrors.aliyun.com/pypi/simple/',
  [string]$Out = '',
  [string]$Work = '',
  [string]$Proxy = '',
  [string]$DictFile = '',
  [ValidateSet('auto', 'merge', 'replace')]
  [string]$Mode = 'auto',
  [int]$Cap = 120000
)
# 手动运行的内置词典刷新脚本（不进入产品运行期）。
# 默认源：pypinyin 轮子（约 840KB，含 phrases_dict.json 的常用词组）；备用源：jieba 源码包（-Source jieba，约 19MB）。
# 并发安全：每次运行使用唯一工作目录；护栏：任何失败都只打印原因并非零退出，绝不改动目标词表（先写临时文件，成功才替换）。
$ErrorActionPreference = 'Continue'
# 控制台/重定向都用 UTF-8，避免日志里的中文变成乱码（C 的 cmd 里也好看）
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$env:PYTHONIOENCODING = 'utf-8'
if ([string]::IsNullOrEmpty($Out)) { $Out = Join-Path $Root 'src\Assets\common_words.tsv' }
if ([string]::IsNullOrEmpty($Work)) { $Work = Join-Path $Root '_devA\dictfx' }
if ([string]::IsNullOrEmpty($Package)) { if ($Source -eq 'jieba') { $Package = 'jieba' } else { $Package = 'pypinyin' } }

function Fail($msg, $code) {
  Write-Host ('[fetch_dict] FAIL: ' + $msg)
  Write-Host '[fetch_dict] 现有词表未被修改。'
  $n = 1
  if ($code -is [int] -and $code -gt 0) { $n = $code }
  exit $n
}

# 走系统代理（未显式给 -Proxy 时由 Invoke-WebRequest 使用系统代理设置）
function WebGet($url, $dest) {
  if ([string]::IsNullOrEmpty($dest)) {
    if ($Proxy -ne '') { return Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60 -Proxy $Proxy }
    return Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60
  }
  if ($Proxy -ne '') { Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -TimeoutSec 600 -Proxy $Proxy }
  else { Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -TimeoutSec 600 }
}

$beforeLines = -1
$beforeBytes = -1
if (Test-Path $Out) {
  $beforeBytes = (Get-Item $Out).Length
  $beforeLines = @([System.IO.File]::ReadAllLines($Out, (New-Object System.Text.UTF8Encoding($false)))).Count
}
Write-Host ('[fetch_dict] 目标=' + $Out)
Write-Host ('[fetch_dict] 改前: lines=' + $beforeLines + ' bytes=' + $beforeBytes)
Write-Host ('[fetch_dict] 源=' + $Source + ' (包=' + $Package + ') 镜像=' + $Mirror + ' cap=' + $Cap + ' mode=' + $Mode)
if ($Proxy -ne '') { Write-Host ('[fetch_dict] 代理=' + $Proxy) }

if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Force -Path $Work | Out-Null }
try {
  $cut = (Get-Date).AddHours(-2)
  Get-ChildItem $Work -Directory -Filter 'run-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt $cut } |
    ForEach-Object { try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch { Write-Host ('[fetch_dict] 残留目录未清理（忽略）: ' + $_.FullName) } }
} catch { Write-Host '[fetch_dict] 残留清理跳过（忽略）' }

$runDir = Join-Path $Work ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
Write-Host ('[fetch_dict] 工作目录=' + $runDir)

# auto：pypinyin 是注音消歧词表（缺 星星/太阳/明天 这类常用词），默认为「与现有表合并」；jieba 是语料词表，默认「替换」
$doMerge = $false
if ($Mode -eq 'merge') { $doMerge = $true }
elseif ($Mode -eq 'auto') { $doMerge = ($Source -eq 'pypinyin') }
Write-Host ('[fetch_dict] 合并模式=' + $doMerge + '（' + $Source + ' 源在 mode=auto 下的默认行为）')

$candidate = Join-Path $runDir 'words_candidate.tsv'
$pin = Join-Path $Root 'src\Assets\pinyin_full.tsv'

if ($Source -eq 'pypinyin') {
  $indexUrl = $Mirror.TrimEnd('/') + '/' + $Package + '/'
  try { $page = WebGet $indexUrl '' }
  catch { Fail ('下载索引页失败 ' + $indexUrl + ' -> ' + $_.Exception.Message) 10 }
  $hrefs = @()
  foreach ($m in [regex]::Matches($page.Content, 'href="([^"]+)"')) { $hrefs += $m.Groups[1].Value }
  $cands = @($hrefs | Where-Object { $_ -match '\.whl(#|$)' })
  if ($cands.Count -eq 0) { Fail ('索引页里没有 .whl 分发包: ' + $indexUrl) 11 }
  $best = $null
  $bestv = $null
  foreach ($h in $cands) {
    $mm = [regex]::Match($h, [regex]::Escape($Package) + '-([0-9][0-9.\-a-zA-Z]*?)-')
    if (-not $mm.Success) { continue }
    $vv = $null
    try { $vv = [version]$mm.Groups[1].Value.TrimEnd('.') } catch { $vv = $null }
    if ($vv -eq $null) { continue }
    if ($bestv -eq $null -or $vv -gt $bestv) { $bestv = $vv; $best = $h }
  }
  if ($best -eq $null) { Fail '无法从索引页解析出轮子版本号' 12 }
  $url = (New-Object System.Uri([System.Uri]$indexUrl, $best)).AbsoluteUri
  $leaf = Split-Path ([System.Uri]$url).AbsolutePath -Leaf
  $whl = Join-Path $runDir $leaf
  Write-Host ('[fetch_dict] 选中 ' + $bestv + ' -> ' + $leaf)
  try { WebGet $url $whl }
  catch { Fail ('下载轮子失败 ' + $url + ' -> ' + $_.Exception.Message) 13 }
  if (-not (Test-Path $whl)) { Fail ('下载后文件不存在: ' + $whl) 13 }
  $size = (Get-Item $whl).Length
  if ($size -lt 10000) { Fail ('轮子过小（' + $size + ' 字节），判定下载不完整: ' + $whl) 13 }
  Write-Host ('[fetch_dict] 下载完成 bytes=' + $size + ' sha256=' + (Get-FileHash $whl -Algorithm SHA256).Hash)

  $zip = Join-Path $runDir 'pkg.zip'
  Copy-Item $whl $zip -Force
  $ex = Join-Path $runDir 'x'
  New-Item -ItemType Directory -Force -Path $ex | Out-Null
  try { Expand-Archive -Path $zip -DestinationPath $ex -Force }
  catch { Fail ('轮子解包失败 -> ' + $_.Exception.Message) 14 }
  $jsons = @(Get-ChildItem -Recurse -File $ex -Filter 'phrases_dict.json' | Select-Object -First 1)
  if ($jsons.Count -eq 0) { Fail ('解包后找不到 phrases_dict.json（包结构可能变了），目录=' + $ex) 15 }
  Write-Host ('[fetch_dict] phrases_dict.json bytes=' + $jsons[0].Length + ' -> ' + $jsons[0].FullName)
  $script = Join-Path $PSScriptRoot 'extract_pypinyin_phrases.py'
  if (-not (Test-Path $script)) { Fail ('缺少 ' + $script) 16 }
  $pyOut = & py -3 $script $jsons[0].FullName $pin $candidate $Cap 2>&1
  $pyCode = $LASTEXITCODE
  foreach ($l in $pyOut) { Write-Host ('[fetch_dict] ' + $l) }
  if ($pyCode -ne 0) { Fail ('从 phrases_dict.json 提取词组失败（退出码 ' + $pyCode + '）') 17 }
} else {
  if (-not [string]::IsNullOrEmpty($DictFile)) {
    if (-not (Test-Path $DictFile)) { Fail ('指定的 -DictFile 不存在: ' + $DictFile) 9 }
    if ((Get-Item $DictFile).Length -lt 100000) { Fail ('指定的 -DictFile 只有 ' + (Get-Item $DictFile).Length + ' 字节，判定格式不符: ' + $DictFile) 15 }
    $dictPath = $DictFile
    Write-Host ('[fetch_dict] 跳过下载，使用 -DictFile=' + $DictFile)
  } else {
    $indexUrl = $Mirror.TrimEnd('/') + '/' + $Package + '/'
    try { $page = WebGet $indexUrl '' }
    catch { Fail ('下载索引页失败 ' + $indexUrl + ' -> ' + $_.Exception.Message) 10 }
    $hrefs = @()
    foreach ($m in [regex]::Matches($page.Content, 'href="([^"]+)"')) { $hrefs += $m.Groups[1].Value }
    $cands = @($hrefs | Where-Object { $_ -match '\.(zip|tar\.gz)(#|$)' })
    if ($cands.Count -eq 0) { Fail ('索引页里没有可用的源码包（.zip/.tar.gz）: ' + $indexUrl) 11 }
    $best = $null
    $bestv = $null
    foreach ($h in $cands) {
      $mm = [regex]::Match($h, [regex]::Escape($Package) + '-([0-9][0-9.\-a-zA-Z]*?)(\.tar\.gz|\.zip|-)')
      if (-not $mm.Success) { continue }
      $vv = $null
      try { $vv = [version]$mm.Groups[1].Value.TrimEnd('.') } catch { $vv = $null }
      if ($vv -eq $null) { continue }
      if ($bestv -eq $null -or $vv -gt $bestv) { $bestv = $vv; $best = $h }
    }
    if ($best -eq $null) { Fail '无法从索引页解析出版本号' 12 }
    $url = (New-Object System.Uri([System.Uri]$indexUrl, $best)).AbsoluteUri
    $leaf = Split-Path ([System.Uri]$url).AbsolutePath -Leaf
    $pkg = Join-Path $runDir $leaf
    Write-Host ('[fetch_dict] 选中 ' + $bestv + ' -> ' + $leaf)
    try { WebGet $url $pkg }
    catch { Fail ('下载源码包失败 ' + $url + ' -> ' + $_.Exception.Message) 13 }
    if (-not (Test-Path $pkg)) { Fail ('下载后文件不存在: ' + $pkg) 13 }
    $size = (Get-Item $pkg).Length
    if ($size -lt 10000) { Fail ('源码包过小（' + $size + ' 字节），判定下载不完整: ' + $pkg) 13 }
    Write-Host ('[fetch_dict] 下载完成 bytes=' + $size + ' sha256=' + (Get-FileHash $pkg -Algorithm SHA256).Hash)

    $ex = Join-Path $runDir 'x'
    New-Item -ItemType Directory -Force -Path $ex | Out-Null
    if ($leaf -like '*.tar.gz') {
      & tar -xzf $pkg -C $ex
      if ($LASTEXITCODE -ne 0) { Fail ('tar 解包失败，退出码 ' + $LASTEXITCODE + '，包=' + $pkg) 14 }
    } else {
      try { Expand-Archive -Path $pkg -DestinationPath $ex -Force }
      catch { Fail ('解包失败 -> ' + $_.Exception.Message + '，包=' + $pkg) 14 }
    }
    $found = @(Get-ChildItem -Recurse -File $ex -Filter 'dict.txt' | Select-Object -First 1)
    if ($found.Count -eq 0) { Fail ('解包后找不到 dict.txt（包结构可能变了），目录=' + $ex) 15 }
    $dictPath = $found[0].FullName
    if ($found[0].Length -lt 100000) { Fail ('dict.txt 只有 ' + $found[0].Length + ' 字节，判定格式不符: ' + $dictPath) 15 }
    Write-Host ('[fetch_dict] dict.txt bytes=' + $found[0].Length + ' -> ' + $dictPath)
  }
  $script = Join-Path $PSScriptRoot 'rebuild_common_words.py'
  if (-not (Test-Path $script)) { Fail ('缺少 ' + $script) 16 }
  $pyOut = & py -3 $script $dictPath $pin $candidate $Cap 2>&1
  $pyCode = $LASTEXITCODE
  foreach ($l in $pyOut) { Write-Host ('[fetch_dict] ' + $l) }
  if ($pyCode -ne 0) { Fail ('从 jieba dict.txt 重建词表失败（退出码 ' + $pyCode + '）') 17 }
}

if (-not (Test-Path $candidate)) { Fail '未产出候选词表' 18 }
$newLines = @([System.IO.File]::ReadAllLines($candidate, (New-Object System.Text.UTF8Encoding($false)))).Count
$newBytes = (Get-Item $candidate).Length
if ($newLines -lt 1000) { Fail ('候选词表只有 ' + $newLines + ' 条，拒绝写入') 19 }
Write-Host ('[fetch_dict] 来源产出: 源=' + $Source + ' lines=' + $newLines + ' bytes=' + $newBytes)

$stageSource = $candidate
if ($doMerge) {
  $mergeScript = Join-Path $PSScriptRoot 'merge_word_tables.py'
  if (-not (Test-Path $mergeScript)) { Fail ('缺少 ' + $mergeScript) 16 }
  $merged = Join-Path $runDir 'words_merged.tsv'
  $mOut = & py -3 $mergeScript $Out $candidate $merged $Cap 2>&1
  $mCode = $LASTEXITCODE
  foreach ($l in $mOut) { Write-Host ('[fetch_dict] ' + $l) }
  if ($mCode -ne 0) { Fail ('合并现有词表失败（退出码 ' + $mCode + '）') 17 }
  $stageSource = $merged
}

$stage = $Out + '.new'
try {
  Copy-Item $stageSource $stage -Force
  Move-Item -Force $stage $Out
} catch {
  if (Test-Path $stage) { try { Remove-Item $stage -Force } catch { } }
  Fail ('替换目标失败 -> ' + $_.Exception.Message + '（目标保持原样）') 20
}
$afterLines = @([System.IO.File]::ReadAllLines($Out, (New-Object System.Text.UTF8Encoding($false)))).Count
$afterBytes = (Get-Item $Out).Length
Write-Host ('[fetch_dict] OK 源=' + $Source + ' 改后: lines=' + $afterLines + ' bytes=' + $afterBytes + ' (改前 lines=' + $beforeLines + ' bytes=' + $beforeBytes + ')')
Write-Host '[fetch_dict] 提醒：内置词表已改，需要重新编译 exe 才能生效。'
exit 0
