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
function Replay($plan, [string]$orig) {
  $sb = New-Object System.Text.StringBuilder
  $bs = 0
  $under = $false
  foreach ($s in $plan) {
    if ($s.Kind -eq [SimIme.Engine.StepKind]::Char) { [void]$sb.Append($s.Text) }
    elseif ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { [void]$sb.Append([char]10) }
    elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Backspace) { $bs++; if ($sb.Length -gt 0) { $sb.Length = $sb.Length - 1 } else { $under = $true } }
  }
  return @{ Text = $sb.ToString(); Bs = $bs; Under = $under; Match = ($sb.ToString() -ceq $orig) }
}
function Reasons($plan) {
  $r = @{}
  foreach ($s in $plan) { if ($null -ne $s.Reason) { $r[$s.Reason] = $true } }
  return $r
}

$table = [SimIme.Engine.EngineFactory]::CreatePinyinTable()
$store = New-Object SimIme.Engine.HabitStore('')
$text1 = '输入法是我们每天都在使用的工具，但很少有人会去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。一个好的输入法应当在准确率和速度之间取得平衡，同时还要照顾到不同用户的使用习惯。有人喜欢把整段话一次打完再回头修改，也有人习惯一个字一个字地确认。无论哪种方式，屏幕上最终呈现的文字才是唯一重要的事情，中间的退格与重打都不会留下痕迹。因此回放校验必须保证最终文本与原文完全一致，一个字符都不能差。'

# ================= T1 =================
Write-Host '=== T1 多音字现代读音（引擎实查，读嵌入资源）==='
$t1 = @(
  @('的','de'), @('了','le'), @('着','zhe'), @('还','hai'), @('都','dou'),
  @('什','shen'), @('么','me'), @('行','xing'), @('地','de')
)
$bad = 0
$line = New-Object System.Collections.Generic.List[string]
foreach ($pair in $t1) {
  $got = $table.GetPinyin([char]$pair[0])
  if ($got -ne $pair[1]) { $bad++ }
  $line.Add('' + $pair[0] + '=' + $got + '(expect ' + $pair[1] + ')')
}
Check 'T1 9 个多音字读音全部符合期望' ($bad -eq 0) (($line -join ' ') + ' | tableChars=' + $table.CharCount)

# ================= T2 =================
Write-Host '=== T2 高频 100 字同音错字可读性 ==='
$freq = [System.IO.File]::ReadAllLines((Join-Path $Root 'src\Assets\char_freq.tsv'), [System.Text.UTF8Encoding]::new($false))
$top100 = New-Object System.Collections.Generic.List[char]
for ($i = 0; $i -lt 100; $i++) { $top100.Add([char]$freq[$i].Split([char]9)[0]) }
$badT2 = 0
$candTotal = 0
$withCand = 0
$shown = 0
foreach ($c in $top100) {
  $p = $table.GetPinyin($c)
  $cands = @($table.HomophonesOf($c, 8))
  if ($cands.Count -eq 0) { continue }
  $withCand++
  $okThis = $true
  foreach ($cd in $cands) {
    $candTotal++
    if ($table.GetPinyin([char]$cd[0]) -ne $p) { $badT2++; $okThis = $false }
  }
  if ($shown -lt 12) {
    $shown++
    Write-Host ('    例子 ' + $c + '(' + $p + ') -> ' + (-join $cands) + '   读音一致=' + $okThis)
  }
}
Check 'T2 前 100 高频字的同音候选读音与原字完全相同' (($badT2 -eq 0) -and ($candTotal -gt 100)) ('charsWithCandidates=' + $withCand + '/100 candidatesChecked=' + $candTotal + ' mismatches=' + $badT2)
$dOf = -join @($table.HomophonesOf([char]'的', 8))
Check 'T2 的->得/地 (de) 且不出现 第(di)' (($dOf.Contains('得')) -and ($dOf.Contains('地')) -and (-not $dOf.Contains('第'))) ('的(de) -> ' + $dOf)

# ================= B1 思考停顿间隔 =================
Write-Host '=== B1 思考停顿间隔 8~20 字 ==='
$cfg = NewCfg 31337
$plan = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $cfg, $table, $store))
$lo = $cfg.Rhythm.ThinkEveryMinChars
$hi = $cfg.Rhythm.ThinkEveryMaxChars
$gap = 0
$gaps = New-Object System.Collections.Generic.List[int]
foreach ($s in $plan) {
  if ($s.Kind -eq [SimIme.Engine.StepKind]::Sleep -and $s.Reason -eq '思考停顿') { $gaps.Add($gap); $gap = 0 }
  elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Char -or $s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { $gap++ }
}
$outOfRange = @($gaps | Where-Object { $_ -lt $lo -or $_ -gt $hi }).Count
Check '每次思考停顿之间的字数都在 [8,20]' (($gaps.Count -gt 0) -and ($outOfRange -eq 0)) ('thinkPauses=' + $gaps.Count + ' gaps=' + ($gaps -join ',') + ' outOfRange=' + $outOfRange)

# ================= B2 休息间隔 =================
Write-Host '=== B2 休息间隔 2~5 句 ==='
$rlo = $cfg.Rhythm.RestEveryMinSentences
$rhi = $cfg.Rhythm.RestEveryMaxSentences
$ends = '。！？；…!?;'
$sent = 0
$rgaps = New-Object System.Collections.Generic.List[int]
foreach ($s in $plan) {
  $isEnd = $false
  if ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { $isEnd = $true }
  elseif ($s.Kind -eq [SimIme.Engine.StepKind]::Char -and $s.Text.Length -eq 1 -and $ends.Contains($s.Text)) { $isEnd = $true }
  if ($isEnd) { $sent++ }
  if ($s.Kind -eq [SimIme.Engine.StepKind]::Sleep -and $s.Reason -eq '休息') { $rgaps.Add($sent); $sent = 0 }
}
$rOut = @($rgaps | Where-Object { $_ -lt $rlo -or $_ -gt $rhi }).Count
Check '每次休息之间的句数都在 [2,5]' (($rgaps.Count -gt 0) -and ($rOut -eq 0)) ('restPauses=' + $rgaps.Count + ' sentenceGaps=' + ($rgaps -join ',') + ' outOfRange=' + $rOut)

# ================= B3 快打段 =================
Write-Host '=== B3 快打段（BurstChancePct）效果 ==='
$cfgA = NewCfg 555; $cfgA.Typo.Enabled = $false; $cfgA.Rhythm.BurstChancePct = 0
$cfgB = NewCfg 555; $cfgB.Typo.Enabled = $false; $cfgB.Rhythm.BurstChancePct = 100; $cfgB.Rhythm.SpeedVariancePct = 50
$pa = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $cfgA, $table, $store))
$pb = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $cfgB, $table, $store))
$da = @($pa | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::Char -and $null -eq $_.Reason } | ForEach-Object { $_.DelayMs })
$db = @($pb | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::Char -and $null -eq $_.Reason } | ForEach-Object { $_.DelayMs })
$ma = ($da | Measure-Object -Average).Average
$mb = ($db | Measure-Object -Average).Average
Check 'BurstChancePct=100 时平均字延时显著低于 0%' ($mb -lt ($ma - 15)) ('avgNoBurst=' + [Math]::Round($ma, 1) + ' avgBurst=' + [Math]::Round($mb, 1) + ' delta=' + [Math]::Round($ma - $mb, 1))

# ================= B4 习惯库微调节奏 =================
Write-Host '=== B4 习惯库微调节奏 ==='
$empty = New-Object SimIme.Engine.HabitStore('')
$trained = New-Object SimIme.Engine.HabitStore('')
for ($i = 0; $i -lt 30; $i++) { $trained.ObserveText($text1) }
$cfgH = NewCfg 555; $cfgH.Typo.Enabled = $false
$pEmpty = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $cfgH, $table, $empty))
$pTrained = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $cfgH, $table, $trained))
$de = @($pEmpty | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::Char -and $null -eq $_.Reason } | ForEach-Object { $_.DelayMs })
$dt = @($pTrained | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::Char -and $null -eq $_.Reason } | ForEach-Object { $_.DelayMs })
$me = ($de | Measure-Object -Average).Average
$mt = ($dt | Measure-Object -Average).Average
Check '习惯库有观察后同种子计划更快（且不改变正确性）' ($mt -lt $me) ('avgEmptyStore=' + [Math]::Round($me, 1) + ' avgTrainedStore=' + [Math]::Round($mt, 1))
$rp = Replay $pTrained $text1
Check '习惯库微调后回放仍 === 原文' ($rp.Match -and -not $rp.Under) ('match=' + $rp.Match + ' bs=' + $rp.Bs)

# ================= B5 边界用例 =================
Write-Host '=== B5 边界用例 ==='
$cases = @(
  @('空串', ''),
  @('单标点', '。'),
  @('仅换行', ([string][char]10 + [string][char]10)),
  @('单 ASCII', 'a'),
  @('单汉字', '中')
)
foreach ($cs in $cases) {
  $tx = [string]$cs[1]
  $pp = @([SimIme.Engine.EngineFactory]::BuildPlan($tx, (NewCfg 7), $table, $store))
  $rr = Replay $pp $tx
  Check ('边界：' + $cs[0]) ($rr.Match -and -not $rr.Under) ('inputLen=' + $tx.Length + ' steps=' + $pp.Count + ' match=' + $rr.Match)
}
$nullPlan = @([SimIme.Engine.EngineFactory]::BuildPlan($null, (NewCfg 7), $table, $store))
Check '边界：null 文本不抛异常且返回空计划' ($nullPlan.Count -eq 0) ('steps=' + $nullPlan.Count)

# ================= B6 MaxTyposPerSentence =================
Write-Host '=== B6 每句错字上限 ==='
$cfgCap = NewCfg 2024
$cfgCap.Typo.HomophonePct = 100
$cfgCap.Typo.NasalPct = 0
$cfgCap.Typo.HeadRedoPct = 100
$cfgCap.Typo.OmissionPct = 0
$cfgCap.Typo.RepeatWordPct = 0
$cfgCap.Typo.MaxTyposPerSentence = 2
$capText = '啊。哦。呃。嗯。哈。嘿。呀。喂。噢。哎。'
$planCap = @([SimIme.Engine.EngineFactory]::BuildPlan($capText, $cfgCap, $table, $store))
$typoReasons4 = @('同音字错字', '鼻音错字', '句首重打', '漏字回填')
$events = New-Object System.Collections.Generic.List[int]
$cur = 0; $prevT = $false; $prevR = $null
foreach ($s in $planCap) {
  $r = $s.Reason
  $isT = ($null -ne $r) -and ($typoReasons4 -contains $r)
  if ($isT) {
    if (-not ($prevT -and $r -eq $prevR)) { $cur++ }
    $prevT = $true; $prevR = $r
    if ($s.Kind -eq [SimIme.Engine.StepKind]::Char -and $s.Text.Length -eq 1 -and $ends.Contains($s.Text)) { $events.Add($cur); $cur = 0; $prevT = $false; $prevR = $null }
  } else {
    if ($s.Kind -ne [SimIme.Engine.StepKind]::Sleep) { $prevT = $false; $prevR = $null }
    if ($s.Kind -eq [SimIme.Engine.StepKind]::Char -and $s.Text.Length -eq 1 -and $ends.Contains($s.Text)) { $events.Add($cur); $cur = 0 }
    elseif ($s.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { $events.Add($cur); $cur = 0 }
  }
}
if ($cur -gt 0) { $events.Add($cur) }
$maxE = ($events | Measure-Object -Maximum).Maximum
$rrCap = Replay $planCap $capText
Check 'MaxTyposPerSentence=2 时每句错字事件 <= 2' ($maxE -le 2) ('perSentenceEvents=' + ($events -join ',') + ' max=' + $maxE + ' sentences=' + $events.Count)
# 「嗯」(拼音 n) 这类没有同音/近音字的句子按设计不再演出（宁可不错字，也绝不凭空造无关字），
# 故期望从「10 句都触发」改为「至少 9 句触发」，并额外保护「不演出也不能改坏文本」：每一句结束时的回放都等于原文对应前缀。
$firedCap = @($events | Where-Object { $_ -ge 1 }).Count
Check '上限配置下至少 9 句触发错字（无同音/近音字时可正确地不演出）' (($events.Count -eq 10) -and ($firedCap -ge 9)) ('events=' + ($events -join ',') + ' 触发句数=' + $firedCap + '/10')

$sbCap = New-Object System.Text.StringBuilder
$sentTotal = 0; $sentOk = 0; $sentBad = ''
foreach ($stCap in $planCap) {
  if ($stCap.Kind -eq [SimIme.Engine.StepKind]::Char) { [void]$sbCap.Append($stCap.Text) }
  elseif ($stCap.Kind -eq [SimIme.Engine.StepKind]::LineBreak) { [void]$sbCap.Append([char]10) }
  elseif ($stCap.Kind -eq [SimIme.Engine.StepKind]::Backspace) { if ($sbCap.Length -gt 0) { $sbCap.Length = $sbCap.Length - 1 } }
  if ($stCap.Kind -eq [SimIme.Engine.StepKind]::Char -and $stCap.Text.Length -eq 1 -and $ends.Contains($stCap.Text)) {
    $sentTotal++
    $gotCap = $sbCap.ToString()
    if (($gotCap.Length -le $capText.Length) -and ($gotCap -ceq $capText.Substring(0, $gotCap.Length))) { $sentOk++ }
    elseif ($sentBad -eq '') { $sentBad = '第' + $sentTotal + '句 got=[' + $gotCap + ']' }
  }
}
Check '每一句结束时的回放都等于原文对应前缀（不演出也不改写文本）' (($sentTotal -ge 10) -and ($sentOk -eq $sentTotal)) ('句数=' + $sentTotal + ' 逐句一致=' + $sentOk + ' 首个不符=' + $(if ($sentBad -eq '') { 'none' } else { $sentBad }))
Check '上限配置下回放仍 === 原文' ($rrCap.Match -and -not $rrCap.Under) ('match=' + $rrCap.Match + ' bs=' + $rrCap.Bs)

# ================= B7 5 类错字各自生效 =================
Write-Host '=== B7 五类错字分别单独验证 ==='
$kinds = @(
  @('同音字错字', 'HomophonePct'),
  @('鼻音错字', 'NasalPct'),
  @('句首重打', 'HeadRedoPct'),
  @('漏字回填', 'OmissionPct'),
  @('重复输入', 'RepeatWordPct')
)
foreach ($k in $kinds) {
  $c = NewCfg 12345
  $c.Typo.HomophonePct = 0
  $c.Typo.NasalPct = 0
  $c.Typo.HeadRedoPct = 0
  $c.Typo.OmissionPct = 0
  $c.Typo.RepeatWordPct = 0
  $c.Typo.MaxTyposPerSentence = 3
  $c.Typo.GetType().GetField($k[1]).SetValue($c.Typo, 100)
  $pk = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, $c, $table, $store))
  $rs = Reasons $pk
  $rr2 = Replay $pk $text1
  Check ('' + $k[0] + ' 单独 100% 时出现且回放正确') ($rs.ContainsKey($k[0]) -and $rr2.Match -and -not $rr2.Under) ('reasonPresent=' + $rs.ContainsKey($k[0]) + ' backspaces=' + $rr2.Bs + ' match=' + $rr2.Match)
}

# ================= B8 RandomSeed=0 =================
Write-Host '=== B8 RandomSeed=0 每次随机 ==='
$ser = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt 3; $i++) {
  $pz = @([SimIme.Engine.EngineFactory]::BuildPlan($text1, (NewCfg 0), $table, $store))
  $sb2 = New-Object System.Text.StringBuilder
  foreach ($s in $pz) { [void]$sb2.Append(([int]$s.Kind).ToString() + '|' + $s.DelayMs + '|' + $s.Reason + ';') }
  $ser.Add($sb2.ToString())
}
$distinct = @($ser | Sort-Object -Unique).Count
Check 'RandomSeed=0 连续三次计划互不相同' ($distinct -eq 3) ('distinct=' + $distinct + '/3')

# ================= B9 行结束符归一化 =================
Write-Host '=== B9 行结束符归一化（CRLF / CR / LF -> 恰好一个 LineBreak）==='
$cr = [char]13
$lf = [char]10
$cfgE = NewCfg 99
$cfgE.Typo.Enabled = $false
$variants = @(
  @('A+CRLF+B', ('A' + $cr + $lf + 'B')),
  @('A+CR+B',   ('A' + $cr + 'B')),
  @('A+LF+B',   ('A' + $lf + 'B'))
)
$serList = New-Object System.Collections.Generic.List[string]
foreach ($v in $variants) {
  $tx = [string]$v[1]
  $p = @([SimIme.Engine.EngineFactory]::BuildPlan($tx, $cfgE, $table, $store))
  $parts = New-Object System.Collections.Generic.List[string]
  foreach ($s in $p) {
    $hex = '-'
    if ($null -ne $s.Text -and $s.Text.Length -eq 1) { $hex = ('{0:X2}' -f [int][char]$s.Text) }
    $parts.Add('' + $s.Kind + '(hex=' + $hex + ',reason=' + $s.Reason + ',delay=' + $s.DelayMs + ')')
  }
  Write-Host ('    ' + $v[0] + ' 输入 -> ' + $parts.Count + ' 步: ' + ($parts -join ' '))
  $serList.Add(($parts -join ' '))
  $lbCount = @($p | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::LineBreak }).Count
  $crChar = @($p | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::Char -and $null -ne $_.Text -and $_.Text.Contains([char]13) }).Count
  $rr = Replay $p ('A' + $lf + 'B')
  Check ('' + $v[0] + ' 恰好一个 LineBreak 且无 Char(CR)') (($lbCount -eq 1) -and ($crChar -eq 0)) ('lineBreaks=' + $lbCount + ' charCR=' + $crChar)
  Check ('' + $v[0] + ' 回放 === 归一化文本 A / LF / B') ($rr.Match -and -not $rr.Under) ('match=' + $rr.Match + ' finalLen=' + $rr.Text.Length)
}
Check '三种行结束符产出的计划逐字段完全一致（含 DelayMs）' (($serList[0] -ceq $serList[1]) -and ($serList[1] -ceq $serList[2])) ('CRLF==CR:' + ($serList[0] -ceq $serList[1]) + ' CR==LF:' + ($serList[1] -ceq $serList[2]))

$eolCases = @(
  @('CRLF 形成空行', ('甲' + $cr + $lf + $cr + $lf + '乙'), ('甲' + $lf + $lf + '乙')),
  @('结尾裸 CR',     ('甲' + $cr),                              ('甲' + $lf)),
  @('混合 EOL',      ('甲' + $cr + $lf + '乙' + $cr + '丙' + $lf + '丁'), ('甲' + $lf + '乙' + $lf + '丙' + $lf + '丁'))
)
foreach ($c in $eolCases) {
  $tx = [string]$c[1]
  $exp = [string]$c[2]
  $p = @([SimIme.Engine.EngineFactory]::BuildPlan($tx, $cfgE, $table, $store))
  $rr = Replay $p $exp
  $crChar = @($p | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::Char -and $null -ne $_.Text -and $_.Text.Contains([char]13) }).Count
  $lb = @($p | Where-Object { $_.Kind -eq [SimIme.Engine.StepKind]::LineBreak }).Count
  $wantLb = @($exp.ToCharArray() | Where-Object { $_ -eq $lf }).Count
  Check ('EOL 用例：' + $c[0]) ($rr.Match -and ($crChar -eq 0) -and ($lb -eq $wantLb)) ('steps=' + $p.Count + ' lineBreaks=' + $lb + '(期望' + $wantLb + ') charCR=' + $crChar + ' match=' + $rr.Match)
}
$vr4 = [SimIme.Engine.EngineFactory]::VerifyReplay(('A' + $cr + $lf + 'B'), $cfgE)
Check 'VerifyReplay 对 CRLF 输入判 PASS 且标注已归一化' (($vr4 -match 'RESULT=PASS') -and ($vr4 -match 'lineEndingsNormalized=True') -and ($vr4 -match 'rawInputMatches=False')) ((($vr4.Split([char]10) | Where-Object { $_ -match 'finalMatches|lineEndingsNormalized|rawInputMatches|expectedChars' }) -join ' '))

Write-Host ''
Write-Host ('SUMMARY pass=' + $script:pass + ' fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
