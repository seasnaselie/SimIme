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
$utf8 = New-Object System.Text.UTF8Encoding($false)
$T = [SimIme.Engine.EngineFactory]
$sent = '天上有十二个星星'

# ================= E0 反射 =================
Write-Host '=== E0 外部词表 API 反射 ==='
$pub = @($T.GetMethods() | Where-Object { $_.IsStatic -and $_.IsPublic })
foreach ($m in $pub) {
  $ps = @($m.GetParameters() | ForEach-Object { $_.ParameterType.Name })
  Write-Host ('    ' + $m.ReturnType.Name + ' ' + $m.Name + '(' + ($ps -join ',') + ')')
}
$sigOf = { param($n, $ps) @($T.GetMethods() | Where-Object { $_.Name -eq $n -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq $ps }).Count }
$setM = @($T.GetMethods() | Where-Object { $_.Name -eq 'SetExtraWordFile' -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq 'String' })
$stP = @($T.GetProperties() | Where-Object { $_.Name -eq 'ExtraWordStatus' })
$lsF = @($T.GetFields() | Where-Object { $_.Name -eq 'LogSink' })
Check 'SetExtraWordFile(string)->String 与 ExtraWordStatus/LogSink 存在，且既有方法不动' (($setM.Count -eq 1) -and ($setM[0].ReturnType.Name -eq 'String') -and ($stP.Count -eq 1) -and ($lsF.Count -eq 1) -and ((& $sigOf 'SplitPhrases' 'String,EngineConfig') -eq 1) -and ((& $sigOf 'SplitPhrases' 'String,EngineConfig,IHabitStore') -eq 1)) ('setExtra=' + $setM.Count + ' status=' + $stP.Count + ' logSink=' + $lsF.Count + ' publicStatic=' + $pub.Count)

# 未注册时的基线
$cfg7 = NewCfg 7
$base2 = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7))
Write-Host ('    未注册外部表: ' + ($base2 -join ' / '))

# ================= E1 叠加效果与层级 =================
Write-Host '=== E1 外部词表叠加效果 ==='
$extraPath = Join-Path $Root '_devA\extra_words.tsv'
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('天上有' + [char]9 + '5000' + [char]10)
[void]$sb.Append('天上有十' + [char]9 + '4000' + [char]10)
[void]$sb.Append('十二个' + [char]9 + '3000' + [char]10)
[void]$sb.Append('星星' + [char]9 + '2000' + [char]10)
[void]$sb.Append('我们' + [char]9 + '1000' + [char]10)
[System.IO.File]::WriteAllText($extraPath, $sb.ToString(), $utf8)
$st1 = [SimIme.Engine.EngineFactory]::SetExtraWordFile($extraPath)
Write-Host ('    注册返回: ' + $st1)
$withExtra = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7))
Write-Host ('    注册外部表后（无 habits）: ' + ($withExtra -join ' / '))
Check '外部词表参与最长匹配：出现 4 字外部词 天上有十' (($withExtra -contains '天上有十') -and ((($withExtra -join '')) -ceq $sent)) ('blocks=' + ($withExtra -join ' / '))
Check '注册后与未注册时切分不同（叠加生效）' (($withExtra -join '|') -cne ($base2 -join '|')) ('with=' + ($withExtra -join '|') + ' without=' + ($base2 -join '|'))
$store2 = New-Object SimIme.Engine.HabitStore('')
foreach ($k in 1..5) { $store2.ObserveText('天上有') }
$withBoth = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7, $store2))
Write-Host ('    外部表 + 习惯库: ' + ($withBoth -join ' / '))
Check '层级正确：习惯库用户词组 > 外部词表' (($withBoth[0] -ceq '天上有') -and ((($withBoth -join '')) -ceq $sent)) ('首个块=' + $withBoth[0] + ' 全序列=' + ($withBoth -join ' / '))
Check 'ExtraWordStatus 报告已加载条数' (([SimIme.Engine.EngineFactory]::ExtraWordStatus -match '已加载')) ([SimIme.Engine.EngineFactory]::ExtraWordStatus)

# ================= E2 三种失败情形 =================
Write-Host '=== E2 失败情形：不存在 / 损坏 / 超限 ==='
$stMissing = [SimIme.Engine.EngineFactory]::SetExtraWordFile((Join-Path $Root '_devA\no_such_words.tsv'))
$afterMissing = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7))
Check '文件不存在：状态写明且退回内置词典' (($stMissing -match '不存在') -and (($afterMissing -join '|') -ceq ($base2 -join '|'))) ($stMissing + ' | blocks=' + ($afterMissing -join ' / '))

$badPath = Join-Path $Root '_devA\extra_bad.tsv'
[System.IO.File]::WriteAllText($badPath, ('这不是词表' + [char]10 + '没有任何制表符' + [char]10 + 'ABC' + [char]10), $utf8)
$stBad = [SimIme.Engine.EngineFactory]::SetExtraWordFile($badPath)
$afterBad = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7))
Check '损坏/格式不符：状态写明且退回内置词典' (($stBad -match '没有可用') -and (($afterBad -join '|') -ceq ($base2 -join '|'))) ($stBad + ' | blocks=' + ($afterBad -join ' / '))

$emptyPath = Join-Path $Root '_devA\extra_empty.tsv'
[System.IO.File]::WriteAllText($emptyPath, '', $utf8)
$stEmpty = [SimIme.Engine.EngineFactory]::SetExtraWordFile($emptyPath)
Check '空文件：状态写明且退回内置词典' (($stEmpty -match '没有可用') -or ($stEmpty -match '不存在')) ($stEmpty)

$bigPath = Join-Path $Root '_devA\extra_big.tsv'
$freqLines = [System.IO.File]::ReadAllLines((Join-Path $Root 'src\Assets\char_freq.tsv'), $utf8)
$pool = @()
for ($i = 0; $i -lt 60; $i++) { $pool += [char]($freqLines[$i].Split([char]9)[0][0]) }
$sbBig = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt 250000; $i++) {
  $w = '' + $pool[$i % 60] + $pool[[int]([Math]::Floor($i / 60)) % 60] + $pool[[int]([Math]::Floor($i / 3600)) % 60] + $pool[[int]([Math]::Floor($i / 216000)) % 60]
  [void]$sbBig.Append($w + [char]9 + (1000000 - $i) + [char]10)
}
[System.IO.File]::WriteAllText($bigPath, $sbBig.ToString(), $utf8)
Write-Host ('    构造超限文件 bytes=' + (Get-Item $bigPath).Length + ' rows=250000')
$stBig = [SimIme.Engine.EngineFactory]::SetExtraWordFile($bigPath)
Write-Host ('    超限返回: ' + $stBig)
Check '超限文件：截断到 200000 且状态写明' (($stBig -match '截断') -and ($stBig -match '200000')) ($stBig)

# ================= E3 契约与注销 =================
Write-Host '=== E3 硬契约（挂着外部表）与注销回退 ==='
$texts = @(
  '输入法是我们每天都在使用的工具，但很少有人会去去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。',
  'SimIme v1.0 于2026年3月发布；测试 1234 次，成功率 99.9%！',
  ('第一行。' + $lf + '第二行：混排 English 与 2026。' + $lf + '第三行结束。'),
  ('第一行。' + $cr + $lf + '第二行正文。' + $cr + $lf + '第三行结束。'),
  ('甲。' + $cr + $lf + $cr + $lf + '乙。' + $lf + $lf + '丙。'),
  ('说明：' + [char]9 + 'abc-123/xyz 结束。')
)
$total = 0; $bad = 0
foreach ($tx in $texts) {
  $expected = NormEol ([string]$tx)
  for ($s = 1; $s -le 150; $s++) {
    $b = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx, (NewCfg $s)))
    $total++
    if ((($b -join '')) -cne $expected) { $bad++ }
  }
}
Check ('挂着外部表时硬契约仍成立：' + $texts.Count + ' 段 x 150 种子 = ' + $total) ($bad -eq 0) ('total=' + $total + ' failures=' + $bad)
$unreg = [SimIme.Engine.EngineFactory]::SetExtraWordFile('')
$afterUnreg = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7))
Check '注销后与未注册时逐字段一致' (($afterUnreg -join '|') -ceq ($base2 -join '|')) ('unreg=' + $unreg + ' blocks=' + ($afterUnreg -join ' / '))

Write-Host ''
Write-Host ('SUMMARY pass=' + $script:pass + ' fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
