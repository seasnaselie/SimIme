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

# ================= D0 反射 =================
Write-Host '=== D0 新 API 反射 ==='
$T = [SimIme.Engine.EngineFactory]
$pub = @($T.GetMethods() | Where-Object { $_.IsStatic -and $_.IsPublic })
foreach ($m in $pub) {
  $ps = @($m.GetParameters() | ForEach-Object { $_.ParameterType.Name })
  Write-Host ('    ' + $m.ReturnType.Name + ' ' + $m.Name + '(' + ($ps -join ',') + ')')
}
$sigOf = { param($n, $ps) @($T.GetMethods() | Where-Object { $_.Name -eq $n -and (@($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') -eq $ps }).Count }
Check 'SplitPhrases 两个重载并存，且既有 4 个冻结方法仍在' (((& $sigOf 'SplitPhrases' 'String,EngineConfig') -eq 1) -and ((& $sigOf 'SplitPhrases' 'String,EngineConfig,IHabitStore') -eq 1) -and ((& $sigOf 'CreatePinyinTable' '') -eq 1) -and ((& $sigOf 'BuildPlan' 'String,EngineConfig,IPinyinTable,IHabitStore') -eq 1) -and ((& $sigOf 'VerifyReplay' 'String,EngineConfig') -eq 1)) ('publicStaticMethodCount=' + $pub.Count)
$iface = [SimIme.Engine.IHabitStore]
$uw = @($iface.GetMethods() | Where-Object { $_.Name -eq 'UserWords' })
$uc = @($iface.GetMethods() | Where-Object { $_.Name -eq 'UserWordCount' })
Check 'IHabitStore 新增 UserWords(int)/UserWordCount(string)' (($uw.Count -eq 1) -and ($uc.Count -eq 1) -and ($uw[0].ReturnType.Name -eq 'IList_1' -or $uw[0].ReturnType.IsGenericType) -and ($uc[0].ReturnType.Name -eq 'Int32')) ('UserWords ret=' + $uw[0].ReturnType.FullName + ' UserWordCount ret=' + $uc[0].ReturnType.Name + ' 接口方法数=' + @($iface.GetMethods()).Count)
$hc = New-Object SimIme.Engine.EngineConfig
Check 'EngineConfig.UserWordMinCount 默认 3' ($hc.UserWordMinCount -eq 3) ('UserWordMinCount=' + $hc.UserWordMinCount)

# ================= D1 学习效果 =================
Write-Host '=== D1 学习效果（天上有十二个星星 x5）==='
$sent = '天上有十二个星星'
$store = New-Object SimIme.Engine.HabitStore('')
foreach ($k in 1..5) { $store.ObserveText($sent) }
$cnt = $store.UserWordCount('天上有')
$words = @($store.UserWords(100))
Check 'UserWords 出现「天上有」且计数=5' (($cnt -eq 5) -and ($words -contains '天上有')) ('UserWordCount(天上有)=' + $cnt + ' 在 UserWords 中=' + ($words -contains '天上有') + ' 词组总数=' + $store.PhraseKindCount)
Write-Host ('    用户词组前 8: ' + ((@($store.UserWords(8))) -join ' / '))
$cfg7 = NewCfg 7
$noH = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7))
$wiH = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7, $store))
Write-Host ('    两参（不传 habits）: ' + ($noH -join ' / '))
Write-Host ('    三参（传 habits）  : ' + ($wiH -join ' / '))
Check '三参切分把「天上有」整块切出' ($wiH -contains '天上有') ('blocks=' + ($wiH -join ' / ') + ' 含天上有=' + ($wiH -contains '天上有'))
Check '三参切分与两参不同，且两者都满足拼接契约' ((($wiH -join '|') -cne ($noH -join '|')) -and ((($wiH -join '')) -ceq $sent) -and ((($noH -join '')) -ceq $sent)) ('with=' + ($wiH -join '|') + ' without=' + ($noH -join '|'))
$cfgHi = NewCfg 7; $cfgHi.UserWordMinCount = 6
$hiH = @([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfgHi, $store))
Check '阈值不满足（UserWordMinCount=6 > 计数 5）时与两参重载一致' (($hiH -join '|') -ceq ($noH -join '|')) ('threshold6=' + ($hiH -join '|') + ' 两参=' + ($noH -join '|'))
$s2 = New-Object SimIme.Engine.HabitStore('')
$s2.ObserveText('甲乙丙丁')
$c1 = $s2.UserWordCount('甲乙丙丁')
$s2.ObserveText('甲乙丙丁')
$c2 = $s2.UserWordCount('甲乙丙丁')
Check '只出现 1 次不入表，第 2 次才入表（>=2 次约束）' (($c1 -eq 0) -and ($c2 -eq 2)) ('1次后=' + $c1 + ' 2次后=' + $c2)
$s3 = New-Object SimIme.Engine.HabitStore('')
$s3.ObserveText('今天天气很好，abc123 混合！！')
$u3 = @($s3.UserWords(20))
$bad3 = 0
foreach ($w in $u3) { foreach ($ch in $w.ToCharArray()) { if (-not ([int]$ch -ge 0x4E00 -and [int]$ch -le 0x9FA5)) { $bad3++ } } }
Check '标点/空白/ASCII 不参与组词' ($bad3 -eq 0) ('词组=' + ($u3 -join '/') + ' 非汉字字符数=' + $bad3)
Check '三参重载 habits=null 时与两参一致' (((@([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7, $null))) -join '|') -ceq ($noH -join '|')) ('nullHabits=' + ((@([SimIme.Engine.EngineFactory]::SplitPhrases($sent, $cfg7, $null))) -join '|'))

# ================= D2 硬契约回归（含 habits） =================
Write-Host '=== D2 硬契约回归（三参 + 两参基线哈希）==='
$texts = @(
  '输入法是我们每天都在使用的工具，但很少有人会去去想它是怎么工作的。从按键到文字出现在屏幕上，中间要经过编码、分词、候选排序和上屏好几个步骤。',
  'SimIme v1.0 于2026年3月发布；测试 1234 次，成功率 99.9%！',
  ('第一行。' + $lf + '第二行：混排 English 与 2026。' + $lf + '第三行结束。'),
  ('第一行。' + $cr + $lf + '第二行正文。' + $cr + $lf + '第三行结束。'),
  ('甲。' + $cr + $lf + $cr + $lf + '乙。' + $lf + $lf + '丙。'),
  ('说明：' + [char]9 + 'abc-123/xyz 结束。')
)
$learn = New-Object SimIme.Engine.HabitStore('')
foreach ($k in 1..3) { foreach ($tx in $texts) { $learn.ObserveText([string]$tx) } }
Write-Host ('    学习库词组数=' + $learn.PhraseKindCount + '（用于三参切分）')
$total = 0; $bad = 0; $diffFrom2 = 0
$sb2 = New-Object System.Text.StringBuilder
foreach ($tx in $texts) {
  $expected = NormEol ([string]$tx)
  for ($s = 1; $s -le 150; $s++) {
    $cfg = NewCfg $s
    $b3 = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx, $cfg, $learn))
    $b2 = @([SimIme.Engine.EngineFactory]::SplitPhrases([string]$tx, $cfg))
    $total++
    if ((($b3 -join '')) -cne $expected) { $bad++ }
    if ((($b3 -join '|')) -cne ($b2 -join '|')) { $diffFrom2++ }
    [void]$sb2.Append(($b2 -join '|')); [void]$sb2.Append([char]10)
  }
}
Check ('三参硬契约：' + $texts.Count + ' 段 x 150 种子 = ' + $total + ' 次，Concat===Normalize') ($bad -eq 0) ('total=' + $total + ' failures=' + $bad + ' 与两参不同的次数=' + $diffFrom2)
$baselineBytes = [System.Text.Encoding]::UTF8.GetBytes($sb2.ToString())
$sha = (([System.Security.Cryptography.SHA256]::Create().ComputeHash($baselineBytes)) | ForEach-Object { $_.ToString('x2') }) -join ''
$expectedSha = 'b139ba9388bb25cf24f521b4d7b963206d834b31ffddf3dddfdcd456ef20f6ed'
Check '两参重载稳定基线（第十七轮抖动收紧后的新基线 sha256；旧基线改由 suite6 用 PhraseJitterMaxChars=4 验证）' ($sha -ceq $expectedSha) ('sha256=' + $sha + ' 期望=' + $expectedSha + ' bytes=' + $baselineBytes.Length)
Check '用户短语确实改变了切分（并非无效）' ($diffFrom2 -gt 0) ('与两参不同的次数=' + $diffFrom2 + '/' + $total)

# ================= D3 持久化 =================
Write-Host '=== D3 持久化与上限淘汰 ==='
$hp = Join-Path $Root '_devA\habit8.dat'
if (Test-Path $hp) { Remove-Item $hp -Force }
$h1 = New-Object SimIme.Engine.HabitStore($hp)
foreach ($k in 1..4) { $h1.ObserveText($sent) }
$h1.ObserveText('但是你是太阳朋友')
$h1.Save()
$h2 = [SimIme.Engine.EngineFactory]::CreateHabitStore($hp)
$w1 = @($h1.UserWords(50)); $w2 = @($h2.UserWords(50))
$same = (($w1 -join '|') -ceq ($w2 -join '|'))
$cntSame = $true
foreach ($w in $w1) { if ($h1.UserWordCount($w) -ne $h2.UserWordCount($w)) { $cntSame = $false } }
Check 'Save -> Load 后 UserWords 与计数一致' ($same -and $cntSame -and ($w1.Count -gt 0)) ('条数=' + $w1.Count + ' 序列一致=' + $same + ' 计数一致=' + $cntSame + ' 文件字节=' + (Get-Item $hp).Length + ' 例: ' + (($w2 | Select-Object -First 6) -join '/'))

$big = New-Object SimIme.Engine.HabitStore('')
$freq = [System.IO.File]::ReadAllLines((Join-Path $Root 'src\Assets\char_freq.tsv'), (New-Object System.Text.UTF8Encoding($false)))
$pool = @()
for ($i = 0; $i -lt 60; $i++) { $pool += [char]($freq[$i].Split([char]9)[0][0]) }
$seen = @{}
for ($i = 0; $i -lt 8000; $i++) {
  $s = '' + $pool[$i % 60] + $pool[[int]([Math]::Floor($i / 60)) % 60] + $pool[[int]([Math]::Floor($i / 3600)) % 60]
  if ($seen.ContainsKey($s)) { continue }
  $seen[$s] = 1
  $big.ObserveText($s)
  $big.ObserveText($s)
}
Check '灌入 8000 条不同词组后表内条数 <= 5000（按次数淘汰）' (($big.PhraseKindCount -le 5000) -and ($big.PhraseKindCount -gt 0) -and ($big.PhraseEvictions -gt 0)) ('灌入不同词组=' + $seen.Count + ' 表内=' + $big.PhraseKindCount + ' 淘汰次数=' + $big.PhraseEvictions + ' 暂存=' + $big.PendingKindCount)

Write-Host ''
Write-Host ('SUMMARY pass=' + $script:pass + ' fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
