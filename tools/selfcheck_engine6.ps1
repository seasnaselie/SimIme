param(
  [string]$Root = (Split-Path -Parent $PSScriptRoot),
  [string]$Dll = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($Dll)) { $Dll = Join-Path $Root '_devA\SimIme.Engine.dll' }
Add-Type -Path $Dll
$script:pass = 0
$script:fail = 0
function Check($desc, $ok, $detail) {
  if ($ok) { $script:pass++; Write-Host ('  [PASS] ' + $desc + ' :: ' + $detail) }
  else { $script:fail++; Write-Host ('  [FAIL] ' + $desc + ' :: ' + $detail) }
}
function NewCfg($seed, $jitter) {
  $c = New-Object SimIme.Engine.EngineConfig
  $c.RandomSeed = $seed
  if ($jitter -ne $null) { $c.PhraseJitterMaxChars = $jitter }
  return $c
}
function NormEol([string]$t) {
  $t = $t.Replace(([string][char]13 + [string][char]10), [string][char]10)
  $t = $t.Replace([char]13, [char]10)
  return $t
}
function SeqHash($texts, $seeds, $jitter) {
  $sb = New-Object System.Text.StringBuilder
  foreach ($tx in $texts) {
    for ($s = 1; $s -le $seeds; $s++) {
      $b = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx, (NewCfg $s $jitter)))
      [void]$sb.Append(($b -join '|')); [void]$sb.Append([char]10)
    }
  }
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
  $sha = (([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) | ForEach-Object { $_.ToString('x2') }) -join ''
  return @{ Sha = $sha; Bytes = $bytes.Length }
}
$T = [SimIme.Engine.EngineFactory]
$cr = [char]13
$lf = [char]10

# ================= F0 配置字段 =================
Write-Host '=== F0 新配置字段 ==='
$c0 = New-Object SimIme.Engine.EngineConfig
Check 'EngineConfig.PhraseJitterMaxChars 默认 = 1' ($c0.PhraseJitterMaxChars -eq 1) ('PhraseJitterMaxChars=' + $c0.PhraseJitterMaxChars)

# ================= F1 用户句：非词块对照 =================
Write-Host '=== F1 用户句非词块对照 ==='
$sent = '一个红色苹果和一个红色苹果。天上有十二个星星，我们明天去公园散步？'
$allowed = @('一个', '红色', '苹果', '天上', '十二', '十二个', '星星', '我们', '明天', '公园', '散步')
$badCited = @('去公', '园散步', '个星', '有十二', '和一', '个星星', '们明', '天去', '十二个星')
foreach ($s in @(7, 42, 20260317)) {
  $b = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, (NewCfg $s $null)))
  Write-Host ('    改后 seed=' + $s + ' : ' + ($b -join ' / '))
}
foreach ($s in @(7, 42, 20260317)) {
  $b = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, (NewCfg $s 4)))
  Write-Host ('    改前(抖动4) seed=' + $s + ' : ' + ($b -join ' / '))
}
$observed = @{}
$outside = New-Object System.Collections.Generic.List[string]
$cited = New-Object System.Collections.Generic.List[string]
for ($s = 1; $s -le 200; $s++) {
  foreach ($bl in @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, (NewCfg $s $null)))) {
    $n = 0
    foreach ($ch in $bl.ToCharArray()) { if ([int]$ch -ge 0x4E00 -and [int]$ch -le 0x9FA5) { $n++ } }
    if ($n -lt 2) { continue }
    $observed[$bl] = $true
    if ($allowed -notcontains $bl) { $outside.Add($bl) }
    if ($badCited -contains $bl) { $cited.Add($bl) }
  }
}
Check '默认抖动(1)：200 个种子下多字块全部落在真词集合内（无非词块）' ($outside.Count -eq 0) ('观测到的多字块=' + (($observed.Keys | Sort-Object) -join '/') + ' 越界=' + (($outside | Select-Object -Unique) -join '/'))
Check '默认抖动(1)：用户点名的非词块不再出现' ($cited.Count -eq 0) ('命中=' + (($cited | Select-Object -Unique) -join '/'))

$observed4 = @{}
$outside4 = New-Object System.Collections.Generic.List[string]
for ($s = 1; $s -le 200; $s++) {
  foreach ($bl in @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, (NewCfg $s 4)))) {
    $n = 0
    foreach ($ch in $bl.ToCharArray()) { if ([int]$ch -ge 0x4E00 -and [int]$ch -le 0x9FA5) { $n++ } }
    if ($n -lt 2) { continue }
    $observed4[$bl] = $true
    if ($allowed -notcontains $bl) { $outside4.Add($bl) }
  }
}
Check '对照：PhraseJitterMaxChars=4 时仍会造出非词块（证明收紧确实生效）' ($outside4.Count -gt 0) ('抖动4 越界块=' + (($outside4 | Select-Object -Unique) -join '/'))

# ================= F2 硬契约 =================
Write-Host '=== F2 硬契约（默认与抖动4 各 6 段 x 150 种子）==='
$texts = @(
  '输入法是我们每天都在使用的工具，但很少有人会去去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。',
  'SimIme v1.0 于2026年3月发布；测试 1234 次，成功率 99.9%！',
  ('第一行。' + $lf + '第二行：混排 English 与 2026。' + $lf + '第三行结束。'),
  ('第一行。' + $cr + $lf + '第二行正文。' + $cr + $lf + '第三行结束。'),
  ('甲。' + $cr + $lf + $cr + $lf + '乙。' + $lf + $lf + '丙。'),
  ('说明：' + [char]9 + 'abc-123/xyz 结束。')
)
foreach ($jm in @($null, 4)) {
  $total = 0; $bad = 0
  foreach ($tx in $texts) {
    $expected = NormEol ([string]$tx)
    for ($s = 1; $s -le 150; $s++) {
      $b = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx, (NewCfg $s $jm)))
      $total++
      if ((($b -join '')) -cne $expected) { $bad++ }
    }
  }
  $tag = '(默认 jitter=1)'
  if ($jm -ne $null) { $tag = '(jitter=4)' }
  Check ('硬契约：6 段 x 150 种子 ' + $tag + ' Concat===Normalize') ($bad -eq 0) ('total=' + $total + ' failures=' + $bad)
}

# ================= F3 默认 vs 抖动4 的哈希 =================
Write-Host '=== F3 行为变更对照（哈希）==='
$hNew = SeqHash $texts 150 $null
$h4 = SeqHash $texts 150 4
$oldSha = 'b54f705bf53164d032f3ecf18108aa295bc3d13600ba5772a44384201f22b862'
$newSha = 'b139ba9388bb25cf24f521b4d7b963206d834b31ffddf3dddfdcd456ef20f6ed'
Check 'PhraseJitterMaxChars=4 逐字段复现改动前行为（旧基线 sha256）' ($h4.Sha -ceq $oldSha) ('sha256=' + $h4.Sha + ' 旧基线=' + $oldSha + ' bytes=' + $h4.Bytes)
Check '默认（jitter=1）为有意的行为变更，基线已更新' ($hNew.Sha -ceq $newSha) ('sha256=' + $hNew.Sha + ' 新基线=' + $newSha + ' bytes=' + $hNew.Bytes + ' 与旧基线不同=' + ($hNew.Sha -cne $oldSha))

# ================= F4 钳位 =================
Write-Host '=== F4 钳位 1~4 ==='
$h0 = SeqHash $texts 40 0
$h1 = SeqHash $texts 40 1
$h9 = SeqHash $texts 40 9
$h4b = SeqHash $texts 40 4
Check 'PhraseJitterMaxChars <=1 视为 1，>4 视为 4' (($h0.Sha -ceq $h1.Sha) -and ($h9.Sha -ceq $h4b.Sha) -and ($h1.Sha -cne $h4b.Sha)) ('0==1:' + ($h0.Sha -ceq $h1.Sha) + ' 9==4:' + ($h9.Sha -ceq $h4b.Sha) + ' 1!=4:' + ($h1.Sha -cne $h4b.Sha))

# ================= F5 最长匹配优先级不变 =================
Write-Host '=== F5 用户词组 / 外部词表优先不变 ==='
$store = New-Object SimIme.Engine.HabitStore('')
foreach ($k in 1..5) { $store.ObserveText('天上有') }
$withHabits = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, (NewCfg 7 $null), $store))
Check '用户词组仍整块切出（天上有）' ($withHabits -contains '天上有') ('blocks=' + ($withHabits -join ' / '))
$extraPath = Join-Path $Root '_devA\extra_words17.tsv'
[System.IO.File]::WriteAllText($extraPath, ('公园散步' + [char]9 + '900' + [char]10), (New-Object System.Text.UTF8Encoding($false)))
$st = [SimIme.Engine.EngineFactory]::SetExtraWordFile($extraPath)
$withExtra = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, (NewCfg 7 $null)))
Check '外部词表仍参与最长匹配（公园散步 4 字外部词整块切出）' ($withExtra -contains '公园散步') ('注册=' + $st + ' blocks=' + ($withExtra -join ' / '))
[void][SimIme.Engine.EngineFactory]::SetExtraWordFile('')

Write-Host ''
Write-Host ('SUMMARY pass=' + $script:pass + ' fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
