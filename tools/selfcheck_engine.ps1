param(
  [string]$Root = (Split-Path -Parent $PSScriptRoot),
  [string]$Dll = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($Dll)) { $Dll = Join-Path $Root '_devA\SimIme.Engine.dll' }
Add-Type -Path $Dll
Write-Host ('LOADED ' + $Dll)

$script:pass = 0
$script:fail = 0
function Check($desc, $ok, $detail) {
  if ($ok) { $script:pass++; Write-Host ('  [PASS] ' + $desc + ' :: ' + $detail) }
  else { $script:fail++; Write-Host ('  [FAIL] ' + $desc + ' :: ' + $detail) }
}
function Serial($plan) {
  $parts = New-Object System.Collections.Generic.List[string]
  foreach ($s in $plan) { $parts.Add(([int]$s.Kind).ToString() + '|' + $s.DelayMs + '|' + $s.Reason + '|' + $s.Text) }
  return ($parts -join '~')
}
function NewCfg($seed) {
  $c = New-Object SimIme.Engine.EngineConfig
  $c.RandomSeed = $seed
  return $c
}

# ============ A0. 契约 API 反射可调用性 ============
Write-Host '=== A0 API 契约（反射） ==='
$T = [SimIme.Engine.EngineFactory]
$m1 = $T.GetMethod('CreatePinyinTable', [Type[]]@())
Check 'CreatePinyinTable() 0 参' ($m1 -ne $null) ('ReturnType=' + $(if ($m1) { $m1.ReturnType.FullName } else { 'NULL' }))
$m2 = $T.GetMethod('CreateHabitStore', [Type[]]@([string]))
Check 'CreateHabitStore(string)' ($m2 -ne $null) ('ReturnType=' + $(if ($m2) { $m2.ReturnType.FullName } else { 'NULL' }))
$m3 = $T.GetMethod('BuildPlan', [Type[]]@([string], [SimIme.Engine.EngineConfig], [SimIme.Engine.IPinyinTable], [SimIme.Engine.IHabitStore]))
Check 'BuildPlan(string,EngineConfig,IPinyinTable,IHabitStore)' ($m3 -ne $null) ('ReturnType=' + $(if ($m3) { $m3.ReturnType.FullName } else { 'NULL' }))
$m4 = $T.GetMethod('VerifyReplay', [Type[]]@([string], [SimIme.Engine.EngineConfig]))
Check 'VerifyReplay(string,EngineConfig)' ($m4 -ne $null) ('ReturnType=' + $(if ($m4) { $m4.ReturnType.FullName } else { 'NULL' }))

# ============ A2. 数据表规模与查表命中率 ============
Write-Host '=== A2 拼音表规模 + 200 常用字命中率 ==='
$tsv = Join-Path $Root 'src\Assets\pinyin_full.tsv'
$fi = Get-Item $tsv
$lines = [System.IO.File]::ReadAllLines($tsv, [System.Text.UTF8Encoding]::new($false))
Check 'pinyin_full.tsv 条数 >= 20000' ($lines.Count -ge 20000) ('lines=' + $lines.Count + ' bytes=' + $fi.Length)
$table = [SimIme.Engine.EngineFactory]::CreatePinyinTable()
Check 'CreatePinyinTable IsReady' ($table.IsReady) ('CharCount=' + $table.CharCount + ' CommonCharCount=' + $table.CommonCharCount)

$freqLines = [System.IO.File]::ReadAllLines((Join-Path $Root 'src\Assets\char_freq.tsv'), [System.Text.UTF8Encoding]::new($false))
$top = New-Object System.Collections.Generic.List[char]
for ($i = 0; $i -lt [Math]::Min(1000, $freqLines.Count); $i++) {
  $ch = $freqLines[$i].Split([char]9)[0]
  if ($ch.Length -ge 1) { $top.Add([char]$ch[0]) }
}
$rnd = New-Object System.Random(20260317)
$hit = 0
$sample200 = New-Object System.Collections.Generic.List[char]
for ($i = 0; $i -lt 200; $i++) { $sample200.Add($top[$rnd.Next($top.Count)]) }
foreach ($c in $sample200) { if ($table.GetPinyin($c).Length -gt 0) { $hit++ } }
$sampleStr = -join ($sample200[0..19])
Check '随机 200 常用汉字查表命中率 100%' ($hit -eq 200) ('hit=' + $hit + '/200 (池=' + $top.Count + ' 常用字) 样例=' + $sampleStr)

# ============ A3. 回放校验 ============
Write-Host '=== A3 回放校验（最终文本 === 原文） ==='
$text1 = '输入法是我们每天都在使用的工具，但很少有人会去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。一个好的输入法应当在准确率和速度之间取得平衡，同时还要照顾到不同用户的使用习惯。有人喜欢把整段话一次打完再回头修改，也有人习惯一个字一个字地确认。无论哪种方式，屏幕上最终呈现的文字才是唯一重要的事情，中间的退格与重打都不会留下痕迹。因此回放校验必须保证最终文本与原文完全一致，一个字符都不能差。'
$text2 = 'SimIme v1.0 于2026年3月发布，支持 Windows 10/11 与 .NET Framework 4.8；测试共执行 1234 次，成功率 99.9%！'
$text3 = ('第一行：键盘输入测试。' + [char]10 + '第二行：混排 English 与 2026，还有标点。' + [char]10 + '第三行：结束。')
$text4 = ('第一行：CRLF 多行。' + [char]13 + [char]10 + '第二行：正文。' + [char]13 + [char]10 + [char]13 + [char]10 + '第四行：结束。')
Check 'text1 长度 >= 200 汉字' ($text1.Length -ge 200) ('len=' + $text1.Length)

# 引擎会把 CRLF/CR/LF 统一成 LF，故比对基准是归一化后的原文
function NormEol([string]$t) {
  $t = $t.Replace(([string][char]13 + [string][char]10), [string][char]10)
  $t = $t.Replace([char]13, [char]10)
  return $t
}

$texts = @($text1, $text2, $text3, $text4)
$idx = 0
$script:bsTotal = 0
$script:kindsSeen = @{}
foreach ($tx in $texts) {
  $idx++
  $cfg = NewCfg (1000 + $idx)
  $plan = @([SimIme.Engine.EngineFactory]::BuildPlan($tx, $cfg, $table, (New-Object SimIme.Engine.HabitStore(''))))
  # 独立回放（不依赖引擎自带 VerifyReplay）
  $sb = New-Object System.Text.StringBuilder
  $bs = 0
  $underflow = $false
  $reasonSet = @{}
  foreach ($s in $plan) {
    if ($null -ne $s.Reason) { $reasonSet[$s.Reason] = $true }
    if ($s.Kind -eq [SimIme.Engine.StepKind]::Char) { [void]$sb.Append($s.Text) }
    elseif ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { [void]$sb.Append([char]10) }
    elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Backspace) { $bs++; if ($sb.Length -gt 0) { $sb.Length = $sb.Length - 1 } else { $underflow = $true } }
  }
  $final = $sb.ToString()
  $typoKinds = 0
  foreach ($r in @('同音字错字', '鼻音错字', '句首重打', '漏字回填', '重复输入')) { if ($reasonSet.ContainsKey($r)) { $typoKinds++ } }
  $detail = 'text' + $idx + ' steps=' + $plan.Count + ' bs=' + $bs + ' typoKinds=' + $typoKinds + ' reasons=' + (($reasonSet.Keys | Sort-Object) -join ',')
  $expect = NormEol $tx
  Check ('回放最终文本 === 原文（行结束符归一化为 LF）(text' + $idx + ', ' + $tx.Length + ' chars)') ($final -ceq $expect) ($detail + ' finalLen=' + $final.Length + ' expectLen=' + $expect.Length)
  $script:bsTotal = $script:bsTotal + $bs
  $script:kindsSeen[$typoKinds] = $true
  if ($idx -eq 1) { Check '默认配置长文本确实出现错字与退格 (text1)' (($bs -gt 0) -and ($typoKinds -ge 3)) ('backspaceSteps=' + $bs + ' typoKinds=' + $typoKinds) }
  Check ('无退格下溢 (text' + $idx + ')') (-not $underflow) ('underflow=' + $underflow)
  $cfgB = NewCfg (1000 + $idx)
  $cfgB.Typo.HomophonePct = 30; $cfgB.Typo.NasalPct = 20; $cfgB.Typo.HeadRedoPct = 40; $cfgB.Typo.OmissionPct = 20; $cfgB.Typo.RepeatWordPct = 20
  $cfgB.Typo.MaxTyposPerSentence = 3
  $vrB = [SimIme.Engine.EngineFactory]::VerifyReplay($tx, $cfgB)
  Check ('提升错字概率后错字与退格确实出现 (text' + $idx + ')') (($vrB -match 'RESULT=PASS') -and ($vrB -match 'hasTypos=True')) ((($vrB.Split([char]10) | Where-Object { $_ -match 'finalMatches|hasTypos|backspaceSteps|typoReasonSteps' }) -join ' '))

  # 引擎自带 VerifyReplay
  $vr = [SimIme.Engine.EngineFactory]::VerifyReplay($tx, (NewCfg (1000 + $idx)))
  $vrOk = $vr -match 'RESULT=PASS'
  $vrTypo = $vr -match 'hasTypos=True'
  Check ('VerifyReplay RESULT=PASS (text' + $idx + ')') $vrOk ($vr.Split([char]10)[0] + ' ' + (($vr.Split([char]10) | Where-Object { $_ -match 'finalMatches|backspaceSteps|typoReasonSteps|hasTypos' }) -join ' '))
  if ($idx -eq 1) { Check 'VerifyReplay hasTypos=True (text1 默认配置)' $vrTypo ('hasTypos line = ' + (($vr.Split([char]10) | Where-Object { $_ -match 'hasTypos' }) -join '')) }
}
Check '三段文本累计退格 > 0 且出现 >= 2 类错字' (($script:bsTotal -gt 0) -and ($script:kindsSeen.Count -ge 2)) ('totalBackspaces=' + $script:bsTotal + ' typoKindClasses=' + $script:kindsSeen.Count)

# ============ A4. 同音字正确性 ============
Write-Host '=== A4 同音字正确性（50 字） ==='
$rnd2 = New-Object System.Random(4242)
$checked4 = 0
$bad4 = 0
$totalCand = 0
$sample4 = New-Object System.Collections.Generic.List[string]
$guard = 0
while ($checked4 -lt 50 -and $guard -lt 20000) {
  $guard++
  $c = $top[$rnd2.Next(3000)]
  $p = $table.GetPinyin($c)
  if ($p.Length -eq 0) { continue }
  $cands = @($table.HomophonesOf($c, 8))
  if ($cands.Count -eq 0) { continue }
  $checked4++
  if ($sample4.Count -lt 8) { $sample4.Add(([string]$c) + '(' + $p + ')->' + ($cands -join '')) }
  foreach ($cd in $cands) {
    $totalCand++
    $pp = $table.GetPinyin([char]$cd[0])
    if ($pp -ne $p) { $bad4++; if ($bad4 -le 3) { Write-Host ('    MISMATCH ' + $c + '=' + $p + ' vs ' + $cd + '=' + $pp) } }
  }
}
Check 'HomophonesOf 返回字与被查字拼音完全相同' (($checked4 -eq 50) -and ($bad4 -eq 0)) ('charsChecked=' + $checked4 + ' candidatesChecked=' + $totalCand + ' mismatches=' + $bad4 + ' 样例: ' + ($sample4 -join ' '))

# ============ A5. 鼻音变体正确性 ============
Write-Host '=== A5 鼻音变体正确性（50 字） ==='
function NormNasal([string]$p) { if ($p.Length -ge 2 -and $p.EndsWith('ng')) { return $p.Substring(0, $p.Length - 1) } ; return $p }
$rnd3 = New-Object System.Random(777)
$checked5 = 0
$bad5 = 0
$same5 = 0
$totalCand5 = 0
$sample5 = New-Object System.Collections.Generic.List[string]
$guard = 0
while ($checked5 -lt 50 -and $guard -lt 40000) {
  $guard++
  $c = $top[$rnd3.Next(3000)]
  $p = $table.GetPinyin($c)
  if ($p.Length -eq 0) { continue }
  $cands = @($table.NasalVariantsOf($c, 8))
  if ($cands.Count -eq 0) { continue }
  $checked5++
  if ($sample5.Count -lt 8) { $sample5.Add(([string]$c) + '(' + $p + ')->' + ($cands -join '')) }
  foreach ($cd in $cands) {
    $totalCand5++
    $pp = $table.GetPinyin([char]$cd[0])
    if ((NormNasal $pp) -ne (NormNasal $p)) { $bad5++; if ($bad5 -le 3) { Write-Host ('    BAD-NASAL ' + $c + '=' + $p + ' vs ' + $cd + '=' + $pp) } }
    if ($pp -eq $p) { $same5++ }
  }
}
Check 'NasalVariantsOf 归一化后拼音相等且不同音' (($checked5 -eq 50) -and ($bad5 -eq 0) -and ($same5 -eq 0)) ('charsChecked=' + $checked5 + ' candidatesChecked=' + $totalCand5 + ' nasalMismatch=' + $bad5 + ' samePinyin=' + $same5 + ' 样例: ' + ($sample5 -join ' '))

# ============ A6. 种子可复现 ============
Write-Host '=== A6 种子可复现 ==='
$stores = New-Object SimIme.Engine.HabitStore('')
$h1 = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, (NewCfg 4242), $table, $stores))
$h2 = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, (NewCfg 4242), $table, $stores))
$h3 = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, (NewCfg 9999), $table, $stores))
$s1 = Serial $h1
$s2 = Serial $h2
$s3 = Serial $h3
Check '同种子两次 BuildPlan 逐字段相等' (($s1 -ceq $s2) -and ($h1.Count -eq $h2.Count)) ('steps=' + $h1.Count + '/' + $h2.Count + ' serializedLen=' + $s1.Length + ' equal=' + ($s1 -ceq $s2))
Check '不同种子结果不同' ($s1 -cne $s3) ('equal=' + ($s1 -ceq $s3) + ' steps=' + $h1.Count + ' vs ' + $h3.Count)

# ============ A7. 延时区间 + Sleep 理由 ============
Write-Host '=== A7 延时区间与 Sleep 理由 ==='
$cfg7 = NewCfg 31337
$r7 = $cfg7.Rhythm
$plan7 = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $cfg7, $table, $stores))
$cmin = $r7.CharDelayMinMs
$cmax = $r7.CharDelayMaxMs
$violations = 0
$neg = 0
$sleepReasons = @{}
$charCount = 0; $bsCount = 0; $lbCount = 0; $slCount = 0
function IsPunctChar([char]$ch) {
  if ([int]$ch -lt 128) {
    if (($ch -ge '0' -and $ch -le '9') -or ($ch -ge 'a' -and $ch -le 'z') -or ($ch -ge 'A' -and $ch -le 'Z')) { return $false }
    if ($ch -eq ' ' -or $ch -eq [char]9 -or $ch -eq [char]10 -or $ch -eq [char]13) { return $false }
    return $true
  }
  return ('。，、；：？！…—～·「」『』《》〈〉【】（）〔〕“”‘’－＋').Contains($ch)
}
$pmin7 = $r7.PunctuationExtraMinMs
$pmax7 = $r7.PunctuationExtraMaxMs
$punctCount = 0
$punctSeen = New-Object System.Collections.Generic.List[string]
foreach ($s in $plan7) {
  if ($s.DelayMs -lt 0) { $neg++ }
  if ($s.Kind -eq [SimIme.Engine.StepKind]::Char) {
    $charCount++
    $isP = ($s.Text.Length -eq 1) -and (IsPunctChar $s.Text[0])
    $lo = $cmin; $hi = $cmax
    if ($isP) { $lo = $cmin + $pmin7; $hi = $cmax + $pmax7; $punctCount++; if ($punctSeen.Count -lt 12) { $punctSeen.Add('' + $s.Text + '=' + $s.DelayMs) } }
    if ($s.DelayMs -lt $lo -or $s.DelayMs -gt $hi) { $violations++; if ($violations -le 3) { Write-Host ('    CHAR-OUT ' + $s.Text + ' ' + $s.DelayMs + ' allowed[' + $lo + ',' + $hi + ']') } }
  } elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Backspace) {
    $bsCount++
    if ($s.DelayMs -lt $cmin -or $s.DelayMs -gt $cmax) { $violations++; if ($violations -le 3) { Write-Host ('    BS-OUT ' + $s.DelayMs) } }
  } elseif ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) {
    $lbCount++
    if ($s.DelayMs -lt $cmin -or $s.DelayMs -gt $cmax) { $violations++ }
  } else {
    $slCount++
    $sleepReasons[$s.Reason] = $true
    if ($s.Reason -eq '思考停顿') { if ($s.DelayMs -lt $r7.ThinkPauseMinMs -or $s.DelayMs -gt $r7.ThinkPauseMaxMs) { $violations++ } }
    elseif ($s.Reason -eq '休息') { if ($s.DelayMs -lt $r7.RestPauseMinMs -or $s.DelayMs -gt $r7.RestPauseMaxMs) { $violations++ } }
    else { $violations++ }
  }
}
Check '所有 DelayMs >= 0' ($neg -eq 0) ('negatives=' + $neg + ' totalSteps=' + $plan7.Count)
Check ('延时区间：普通字/Backspace/LineBreak 在 [' + $cmin + ',' + $cmax + ']；标点在 [' + ($cmin + $pmin7) + ',' + ($cmax + $pmax7) + ']') ($violations -eq 0) ('violations=' + $violations + ' char=' + $charCount + '(标点' + $punctCount + ') bs=' + $bsCount + ' lb=' + $lbCount + ' sleep=' + $slCount)
Check '标点后步骤实测延时（字[90,260] + PunctuationExtra[80,400]）' ($punctCount -gt 0) ('标点步共 ' + $punctCount + ' 个，前 10 个: ' + ($punctSeen -join ' '))
Check 'Sleep 步 Reason 集合 = {思考停顿,休息}' (($sleepReasons.Keys.Count -gt 0) -and (@($sleepReasons.Keys | Where-Object { $_ -ne '思考停顿' -and $_ -ne '休息' }).Count -eq 0)) ('reasons=' + (($sleepReasons.Keys | Sort-Object) -join ',') + ' think=' + (($plan7 | Where-Object { $_.Reason -eq '思考停顿' }).Count) + ' rest=' + (($plan7 | Where-Object { $_.Reason -eq '休息' }).Count))

# 标点额外延时：构造可判定的配置（区间 500..1100，标点额外固定 600 -> 标点必为 1100）
$cfgP = NewCfg 1
$cfgP.Rhythm.CharDelayMinMs = 500
$cfgP.Rhythm.CharDelayMaxMs = 1100
$cfgP.Rhythm.PunctuationExtraMinMs = 600
$cfgP.Rhythm.PunctuationExtraMaxMs = 600
$cfgP.Typo.Enabled = $false
$tp = '啊，哦、呃。嗯；哈：嘿'
$planP = @([SimIme.Engine.EngineFactory]::BuildPlan($tp, $cfgP, $table, $stores))
$punctDelays = New-Object System.Collections.Generic.List[int]
$plainDelays = New-Object System.Collections.Generic.List[int]
foreach ($s in $planP) {
  if ($s.Kind -ne [SimIme.Engine.StepKind]::Char) { continue }
  if ('，、。；：'.Contains($s.Text)) { $punctDelays.Add($s.DelayMs) } else { $plainDelays.Add($s.DelayMs) }
}
$punctBase = @($punctDelays | ForEach-Object { $_ - 600 })
$punctOk = ($punctDelays.Count -eq 5) -and (@($punctBase | Where-Object { $_ -lt 500 -or $_ -gt 1100 }).Count -eq 0) -and (@($punctDelays | Where-Object { $_ -lt 1100 -or $_ -gt 1700 }).Count -eq 0)
$plainAvg = if ($plainDelays.Count -gt 0) { ($plainDelays | Measure-Object -Average).Average } else { 0 }
$plainInRange = (@($plainDelays | Where-Object { $_ -lt 500 -or $_ -gt 1100 }).Count -eq 0)
$minPunct = ($punctDelays | Measure-Object -Minimum).Minimum
$maxPlain = ($plainDelays | Measure-Object -Maximum).Maximum
Check '标点叠加：总延时 = 字延时[500,1100] + 额外600 = [1100,1700]，且不低于任何普通字延时' ($punctOk -and $plainInRange -and ($minPunct -ge $maxPlain)) ('punct=' + ($punctDelays -join ',') + ' (减600后base=' + ($punctBase -join ',') + ') plain=' + ($plainDelays -join ',') + ' plainAvg=' + [Math]::Round($plainAvg, 1))

# ============ A8. 习惯库落盘/读回 ============
Write-Host '=== A8 习惯库持久化 ==='
$hp = Join-Path $Root '_devA\habit_test.txt'
if (Test-Path $hp) { Remove-Item $hp -Force }
$h = [SimIme.Engine.EngineFactory]::CreateHabitStore($hp)
$created = Test-Path $hp
$h.ObserveText('我们今天去公园散步，公园里的花开了。')
$h.ObserveRhythm(120, 900)
$w1 = $h.WordWeight('公园')
$w2 = $h.WordWeight('散步')
$h.Save()
$h2 = [SimIme.Engine.EngineFactory]::CreateHabitStore($hp)
$w3 = $h2.WordWeight('公园')
$content = [System.IO.File]::ReadAllText($hp, [System.Text.UTF8Encoding]::new($false))
Check 'CreateHabitStore 不存在则新建' $created ('path=' + $hp + ' exists=' + $created)
Check 'ObserveText/WordWeight 生效且 >= 0' (($w1 -gt $w2) -and ($w2 -ge 0)) ('公园=' + [Math]::Round($w1, 3) + ' 散步=' + [Math]::Round($w2, 3))
Check 'Save/Load 往返一致' ([Math]::Abs($w3 - $w1) -lt 0.0001) ('before=' + [Math]::Round($w1, 3) + ' after=' + [Math]::Round($w3, 3) + ' bytes=' + $content.Length)
Check '文件为纯文本且含 word 行' ($content -match 'word') ('head=' + ($content.Split([char]10)[0]))

Write-Host ''
Write-Host ('SUMMARY pass=' + $script:pass + ' fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
