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
function NewCfg($seed) { $c = New-Object SimIme.Engine.EngineConfig; $c.RandomSeed = $seed; return $c }
function NormEol([string]$t) {
  $t = $t.Replace(([string][char]13 + [string][char]10), [string][char]10)
  $t = $t.Replace([char]13, [char]10)
  return $t
}
$cr = [char]13
$lf = [char]10
$table = [SimIme.Engine.EngineFactory]::CreatePinyinTable()
$store = New-Object SimIme.Engine.HabitStore('')

# ================= C0 反射：新增 API 与既有 4 个并存 =================
Write-Host '=== C0 公开 API 反射 ==='
$T = [SimIme.Engine.EngineFactory]
$pub = @($T.GetMethods() | Where-Object { $_.IsStatic -and $_.IsPublic })
foreach ($m in $pub) {
  $ps = @($m.GetParameters() | ForEach-Object { $_.ParameterType.Name })
  Write-Host ('    ' + $m.ReturnType.Name + ' ' + $m.Name + '(' + ($ps -join ',') + ')')
}
$sigOf = { param($n, $ps) @($T.GetMethods() | Where-Object { $_.Name -eq $n -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq $ps }).Count }
Check '既有 4 个冻结方法仍在' (((& $sigOf 'CreatePinyinTable' '') -eq 1) -and ((& $sigOf 'CreateHabitStore' 'String') -eq 1) -and ((& $sigOf 'BuildPlan' 'String,EngineConfig,IPinyinTable,IHabitStore') -eq 1) -and ((& $sigOf 'VerifyReplay' 'String,EngineConfig') -eq 1)) ('publicStaticMethodCount=' + $pub.Count)
$spMethod = @($T.GetMethods() | Where-Object { $_.Name -eq 'SplitPhrases' -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq 'String,EngineConfig' })
Check 'SplitPhrases(string,EngineConfig) -> IList<string>' (($spMethod.Count -eq 1) -and $spMethod[0].ReturnType.IsGenericType) ('found=' + $spMethod.Count + ' ret=' + $(if ($spMethod.Count -eq 1) { $spMethod[0].ReturnType.FullName } else { '-' }))
$ks1 = @($T.GetMethods() | Where-Object { $_.Name -eq 'MakeKeyboardSlip' -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq 'String,EngineConfig' })
$ks2 = @($T.GetMethods() | Where-Object { $_.Name -eq 'MakeKeyboardSlip' -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq 'String,Int32' })
$nk = @($T.GetMethods() | Where-Object { $_.Name -eq 'NeighborKeysOf' -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq 'Char' })
Check 'MakeKeyboardSlip(string,EngineConfig)/(string,int) 与 NeighborKeysOf(char) 存在' (($ks1.Count -eq 1) -and ($ks2.Count -eq 1) -and ($nk.Count -eq 1)) ('ks1=' + $ks1.Count + ' ks2=' + $ks2.Count + ' nk=' + $nk.Count)
$tc = New-Object SimIme.Engine.TypoConfig
Check 'TypoConfig 新字段 WordHomophonePct=2 / KeyboardSlipPct=3' (($tc.WordHomophonePct -eq 2) -and ($tc.KeyboardSlipPct -eq 3)) ('WordHomophonePct=' + $tc.WordHomophonePct + ' KeyboardSlipPct=' + $tc.KeyboardSlipPct)

# ================= C1 划分精确性穷举 =================
Write-Host '=== C1 划分精确性穷举（Concat == Normalize）==='
$texts = @(
  @('长中文+标点', '输入法是我们每天都在使用的工具，但很少有人会去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。'),
  @('数字英文标点', 'SimIme v1.0 于2026年3月发布；测试 1234 次，成功率 99.9%！'),
  @('LF 多行', ('第一行。' + $lf + '第二行：混排 English 与 2026。' + $lf + '第三行结束。')),
  @('CRLF 多行', ('第一行。' + $cr + $lf + '第二行正文。' + $cr + $lf + '第三行结束。')),
  @('含空行', ('甲。' + $cr + $lf + $cr + $lf + '乙。' + $lf + $lf + '丙。')),
  @('制表与斜杠', ('说明：' + [char]9 + 'abc-123/xyz 结束。'))
)
$seeds = 150
$total = 0
$bad = 0
$emptyBad = 0
$firstBad = ''
foreach ($tx in $texts) {
  $expected = NormEol ([string]$tx[1])
  for ($s = 1; $s -le $seeds; $s++) {
    $blocks = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx[1], (NewCfg $s)))
    $concat = ($blocks -join '')
    $total++
    if ($concat -cne $expected) { $bad++; if ($firstBad -eq '') { $firstBad = $tx[0] + ' seed=' + $s } }
    if (@($blocks | Where-Object { $_.Length -eq 0 }).Count -gt 0) { $emptyBad++ }
  }
}
Check ('划分精确性：' + $texts.Count + ' 段文本 x ' + $seeds + ' 种子 = ' + $total + ' 次，Concat===Normalize') (($bad -eq 0) -and ($emptyBad -eq 0)) ('total=' + $total + ' failures=' + $bad + ' emptyBlocks=' + $emptyBad + ' firstFail=' + $(if ($bad -eq 0) { 'none' } else { $firstBad }))

$edge = @(@('空串', ''), @('单汉字', '中'), @('单标点', '。'), @('纯换行', ([string]$lf + [string]$lf)), @('CRLF', ([string]$cr + [string]$lf)))
$eBad = 0
$eDetail = New-Object System.Collections.Generic.List[string]
foreach ($e in $edge) {
  for ($s = 1; $s -le 20; $s++) {
    $blocks = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$e[1], (NewCfg $s)))
    if ((($blocks -join '')) -cne (NormEol ([string]$e[1]))) { $eBad++ }
  }
  $b0 = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$e[1], (NewCfg 1)))
  $eDetail.Add('' + $e[0] + '->[' + ($b0 -join '|') + ']')
}
Check '边界文本划分（空串/单字/纯标点/纯换行/CRLF，各 20 种子）' (($eBad -eq 0) -and (@([SimIme.Engine.EngineFactory]::SplitPhrases('', (NewCfg 1))).Count -eq 0)) ('failures=' + $eBad + '  ' + ($eDetail -join ' '))

Write-Host '--- 块序列样例（用户例句粒度）---'
$demo = '天上有十二个星星，但是你是太阳。'
foreach ($s in @(7, 42, 2026)) {
  $b = @([SimIme.Engine.EngineFactory]::SplitPhrases($demo, (NewCfg $s)))
  Write-Host ('    seed=' + $s + ': ' + ($b -join ' / ') + '   (块数=' + $b.Count + ')')
}
$b1 = (@([SimIme.Engine.EngineFactory]::SplitPhrases($demo, (NewCfg 7))) -join '|')
$b2 = (@([SimIme.Engine.EngineFactory]::SplitPhrases($demo, (NewCfg 7))) -join '|')
$variants = @{}
foreach ($s in 1..40) { $variants[(@([SimIme.Engine.EngineFactory]::SplitPhrases($demo, (NewCfg $s))) -join '|')] = $true }
Check '同种子可复现、不同种子有抖动' (($b1 -ceq $b2) -and ($variants.Count -gt 1)) ('sameSeedEqual=' + ($b1 -ceq $b2) + ' distinctOver40Seeds=' + $variants.Count)
$maxLen = 0
foreach ($tx in $texts) {
  for ($s = 1; $s -le 40; $s++) {
    foreach ($bl in @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx[1], (NewCfg $s)))) {
      $n = 0
      foreach ($ch in $bl.ToCharArray()) { if ([int]$ch -ge 0x4E00 -and [int]$ch -le 0x9FA5) { $n++ } }
      if ($n -gt $maxLen) { $maxLen = $n }
    }
  }
}
Check '汉字块长不超过 4 字' ($maxLen -le 4) ('maxCjkBlockLen=' + $maxLen)

# ================= C2 词组级同音错词 =================
Write-Host '=== C2 词组级同音错词（词组错词）==='
$cfgW = NewCfg 20260711
$cfgW.Typo.HomophonePct = 0
$cfgW.Typo.NasalPct = 0
$cfgW.Typo.HeadRedoPct = 0
$cfgW.Typo.OmissionPct = 0
$cfgW.Typo.RepeatWordPct = 0
$cfgW.Typo.WordHomophonePct = 100
$cfgW.Typo.MaxTyposPerSentence = 8
$cfgW.Rhythm.ThinkEveryMinChars = 500; $cfgW.Rhythm.ThinkEveryMaxChars = 600
$cfgW.Rhythm.RestEveryMinSentences = 50; $cfgW.Rhythm.RestEveryMaxSentences = 60
$wt = '星星但是太阳朋友。'
$blocksW = @([SimIme.Engine.EngineFactory]::SplitPhrases($wt, $cfgW))
Write-Host ('    文本块序列: ' + ($blocksW -join ' / '))
$planW = @([SimIme.Engine.EngineFactory]::BuildPlan($wt, $cfgW, $table, $store))
$i = 0
$events = 0
$badPattern = 0
$badPinyin = 0
$examples = New-Object System.Collections.Generic.List[string]
while ($i -lt $planW.Count) {
  $s = $planW[$i]
  if ($s.Kind -eq [SimIme.Engine.StepKind]::Char -and $s.Reason -eq '词组错词') {
    $wrong = ''
    while ($i -lt $planW.Count -and $planW[$i].Kind -eq [SimIme.Engine.StepKind]::Char -and $planW[$i].Reason -eq '词组错词') { $wrong += $planW[$i].Text; $i++ }
    $k = $wrong.Length
    $bs = 0
    while ($i -lt $planW.Count -and $planW[$i].Kind -eq [SimIme.Engine.StepKind]::Backspace -and $planW[$i].Reason -eq '词组错词') { $bs++; $i++ }
    $right = ''
    while ($i -lt $planW.Count -and $right.Length -lt $k -and $planW[$i].Kind -eq [SimIme.Engine.StepKind]::Char -and $null -eq $planW[$i].Reason) { $right += $planW[$i].Text; $i++ }
    $events++
    if (($bs -ne $k) -or ($right.Length -ne $k)) { $badPattern++; continue }
    for ($t = 0; $t -lt $k; $t++) {
      if ($table.GetPinyin([char]$wrong[$t]) -ne $table.GetPinyin([char]$right[$t])) { $badPinyin++ }
    }
    if ($examples.Count -lt 8) { $examples.Add('' + $right + ' -> ' + $wrong) }
    continue
  }
  $i++
}
$rpW = New-Object System.Text.StringBuilder
foreach ($s in $planW) {
  if ($s.Kind -eq [SimIme.Engine.StepKind]::Char) { [void]$rpW.Append($s.Text) }
  elseif ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { [void]$rpW.Append($lf) }
  elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Backspace) { if ($rpW.Length -gt 0) { $rpW.Length = $rpW.Length - 1 } }
}
Check 'WordHomophonePct=100：出现词组级错词事件，模式=整块错词->退格整块->正确块' (($events -ge 3) -and ($badPattern -eq 0)) ('events=' + $events + ' patternViolations=' + $badPattern + ' 实例: ' + ($examples -join ' ; '))
Check '错词每个字都与正确字同音（引擎自校验）' ($badPinyin -eq 0) ('charsChecked=' + ($events * 2) + ' mismatches=' + $badPinyin)
Check '词组级错词后回放仍 === 原文' ($rpW.ToString() -ceq $wt) ('final=' + $rpW.ToString() + ' len=' + $rpW.Length)

$cfgW0 = NewCfg 20260711
$cfgW0.Typo.WordHomophonePct = 0
$planW0 = @([SimIme.Engine.EngineFactory]::BuildPlan($wt, $cfgW0, $table, $store))
Check 'WordHomophonePct=0 时不产生词组错词步' (@($planW0 | Where-Object { $_.Reason -eq '词组错词' }).Count -eq 0) ('steps=' + $planW0.Count)

$long = '输入法是我们每天都在使用的工具，但很少有人会去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。一个好的输入法应当在准确率和速度之间取得平衡，同时还要照顾到不同用户的使用习惯。'
$cfgWd = NewCfg 777
$planWd = @([SimIme.Engine.EngineFactory]::BuildPlan($long, $cfgWd, $table, $store))
$rpD = New-Object System.Text.StringBuilder
foreach ($s in $planWd) {
  if ($s.Kind -eq [SimIme.Engine.StepKind]::Char) { [void]$rpD.Append($s.Text) }
  elseif ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { [void]$rpD.Append($lf) }
  elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Backspace) { if ($rpD.Length -gt 0) { $rpD.Length = $rpD.Length - 1 } }
}
Check '默认配置(WordHomophonePct=2)长文本回放仍 === 原文' ($rpD.ToString() -ceq $long) ('steps=' + $planWd.Count + ' 词组错词步=' + @($planWd | Where-Object { $_.Reason -eq '词组错词' }).Count + ' match=' + ($rpD.ToString() -ceq $long))

# ================= C3 键盘邻键错拼 =================
Write-Host '=== C3 键盘邻键错拼 ==='
Check 'NeighborKeysOf(x)=zcsd / NeighborKeysOf(n)=bmhj（独立核对的键盘几何）' (([SimIme.Engine.EngineFactory]::NeighborKeysOf([char]'x') -ceq 'zcsd') -and ([SimIme.Engine.EngineFactory]::NeighborKeysOf([char]'n') -ceq 'bmhj')) ('x->' + [SimIme.Engine.EngineFactory]::NeighborKeysOf([char]'x') + ' n->' + [SimIme.Engine.EngineFactory]::NeighborKeysOf([char]'n') + ' g->' + [SimIme.Engine.EngineFactory]::NeighborKeysOf([char]'g') + ' 非字母->[' + [SimIme.Engine.EngineFactory]::NeighborKeysOf([char]'1') + ']')
$pinyins = @('xingxing', 'tianshang', 'nihaoma', 'zhongguo', 'shierge', 'pengyou', 'taiyang', 'xingxing', 'mingtian', 'gongzuo')
$slipOk = 0
$oneChanged = 0
$neighborOk = 0
$shown = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt 10; $i++) {
  $orig = $pinyins[$i]
  $slip = [SimIme.Engine.EngineFactory]::MakeKeyboardSlip($orig, ($i + 1))
  if ($slip -cne $orig) { $slipOk++ }
  $diff = 0
  $pos = -1
  for ($t = 0; $t -lt $orig.Length; $t++) { if ($orig[$t] -ne $slip[$t]) { $diff++; $pos = $t } }
  if ($diff -eq 1) {
    $oneChanged++
    if ([SimIme.Engine.EngineFactory]::NeighborKeysOf($orig[$pos]).Contains($slip[$pos])) { $neighborOk++ }
  }
  $shown.Add('' + $orig + ' -> ' + $slip + ' (第' + ($pos + 1) + '键 ' + $orig[$pos] + '->' + $(if ($pos -ge 0) { $slip[$pos] } else { '-' }) + '，邻键[' + $(if ($pos -ge 0) { [SimIme.Engine.EngineFactory]::NeighborKeysOf($orig[$pos]) } else { '' }) + '])')
}
foreach ($l in $shown) { Write-Host ('    ' + $l) }
Check '10 个实例：恰好改一个字母且改后是该键的邻键' (($slipOk -eq 10) -and ($oneChanged -eq 10) -and ($neighborOk -eq 10)) ('slips=' + $slipOk + '/10 oneCharChanged=' + $oneChanged + ' neighborVerified=' + $neighborOk)
Check '同种子可复现（同一拼音同一种子两次结果相同）' (([SimIme.Engine.EngineFactory]::MakeKeyboardSlip('xingxing', 5)) -ceq ([SimIme.Engine.EngineFactory]::MakeKeyboardSlip('xingxing', 5))) ('seed5a=' + [SimIme.Engine.EngineFactory]::MakeKeyboardSlip('xingxing', 5) + ' seed5b=' + [SimIme.Engine.EngineFactory]::MakeKeyboardSlip('xingxing', 5))
$cfgK0 = NewCfg 9; $cfgK0.Typo.KeyboardSlipPct = 0
$unchanged = 0
foreach ($p in $pinyins) { if ([SimIme.Engine.EngineFactory]::MakeKeyboardSlip($p, $cfgK0) -ceq $p) { $unchanged++ } }
$nullIn = [SimIme.Engine.EngineFactory]::MakeKeyboardSlip($null, $cfgK0)
$nullOk = ([string]::IsNullOrEmpty($nullIn)) -and ('' -eq [SimIme.Engine.EngineFactory]::MakeKeyboardSlip('', $cfgK0))
$noLetter = ([SimIme.Engine.EngineFactory]::MakeKeyboardSlip('12345', 7) -ceq '12345')
Check '无错时返回原串：pct=0 / 空串 / null / 无字母串' (($unchanged -eq 10) -and $nullOk -and $noLetter) ('pct0Unchanged=' + $unchanged + '/10 nullOk=' + $nullOk + ' digitsUnchanged=' + $noLetter)
$cfgK100 = NewCfg 9; $cfgK100.Typo.KeyboardSlipPct = 100
$slip100 = 0
foreach ($p in $pinyins) { if ([SimIme.Engine.EngineFactory]::MakeKeyboardSlip($p, $cfgK100) -cne $p) { $slip100++ } }
$slip3 = 0
foreach ($s in 1..200) { $c3 = NewCfg $s; if ([SimIme.Engine.EngineFactory]::MakeKeyboardSlip('xingxing', $c3) -cne 'xingxing') { $slip3++ } }
Check 'pct=100 必错、默认 pct=3 时约 3% 命中' (($slip100 -eq 10) -and ($slip3 -ge 1) -and ($slip3 -le 20)) ('pct100Slips=' + $slip100 + '/10 pct3SlipsOver200Seeds=' + $slip3)
$cfgPlan = NewCfg 5; $cfgPlan.Typo.Enabled = $false; $cfgPlan.Typo.KeyboardSlipPct = 100
$pp = @([SimIme.Engine.EngineFactory]::BuildPlan('星星太阳。', $cfgPlan, $table, $store))
$rpK = ''
foreach ($s in $pp) { if ($s.Kind -eq [SimIme.Engine.StepKind]::Char) { $rpK += $s.Text } }
Check '邻键错拼不改变落屏文本（BuildPlan 不含拼音层）' ($rpK -ceq '星星太阳。') ('final=' + $rpK)

Write-Host ''
Write-Host ('SUMMARY pass=' + $script:pass + ' fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
