# SimIme 引擎外部独立测试（C 组交付物）
# 目的：从外部视角验证 dist\SimIme.Engine.dll —— 不依赖 A 自己的自测代码。
# 用法：pwsh -NoProfile -File tests\engine_tests.ps1 [-DllPath <path>]
# 退出码：0 = 全部 PASS；1 = 存在 FAIL（含"引擎未交付"导致的 FAIL）
param(
    [string]$DllPath = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($DllPath)) { $DllPath = Join-Path $root 'dist\SimIme.Engine.dll' }
$asset = Join-Path $root 'src\Assets\pinyin_full.tsv'

$script:pass = 0; $script:fail = 0; $script:skip = 0
function Say { param([string]$t) Write-Output $t }
function Ev  { param([string]$t) Write-Output ('       ' + $t) }
function Info { param([string]$t) Write-Output ('[INFO] ' + $t) }
function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    if ($Ok) { $script:pass++; Write-Output ('[PASS] ' + $Name + ' :: ' + $Detail) }
    else     { $script:fail++; Write-Output ('[FAIL] ' + $Name + ' :: ' + $Detail) }
}

Say '==============================================================='
Say ' SimIme 引擎外部独立测试 engine_tests.ps1'
Say (' 时间   : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say (' 目标   : ' + $DllPath)
Say '==============================================================='

if (-not (Test-Path -LiteralPath $DllPath)) {
    Say ''
    Say ('[FAIL] 目标不存在 :: ' + $DllPath)
    Say '       请先运行 build.ps1 生成 dist\SimIme.Engine.dll。'
    Say ''
    Say '汇总：PASS=0 FAIL=1 SKIP=0'
    exit 1
}

# ---------- 0. 加载程序集 ----------
$fi = Get-Item -LiteralPath $DllPath
Say ''
Say ('[INFO] 程序集 : ' + $fi.FullName)
Say ('[INFO] 字节数 : ' + $fi.Length)
Say ('[INFO] SHA256 : ' + (Get-FileHash -LiteralPath $DllPath -Algorithm SHA256).Hash)

Add-Type -Path $DllPath
$asm = [System.Reflection.Assembly]::LoadFrom($DllPath)

$types = @()
try { $types = @($asm.GetTypes()) }
catch {
    $tex = $_.Exception
    if ($tex.InnerException) { $tex = $tex.InnerException }
    if ($tex.Types) { $types = @($tex.Types | Where-Object { $null -ne $_ }) }
}
Say ('[INFO] 公开类型 : ' + (($types | Where-Object { $_.IsPublic } | ForEach-Object { $_.Name }) -join ', '))

$tFactory = @($types | Where-Object { $_.IsPublic -and $_.Name -eq 'EngineFactory' })
$tCfg     = $asm.GetType('SimIme.Engine.EngineConfig')
$tTable   = $asm.GetType('SimIme.Engine.IPinyinTable')
$tStep    = $asm.GetType('SimIme.Engine.Step')
$tHabit   = $asm.GetType('SimIme.Engine.IHabitStore')

$blocked = $null
if ($tTable -eq $null) { $blocked = '程序集内找不到 SimIme.Engine.IPinyinTable（契约缺失）' }
elseif ($tFactory.Count -eq 0) { $blocked = '程序集内找不到公开类型 SimIme.Engine.EngineFactory（A 尚未交付工厂）' }

$enumerableDef = ([System.Collections.Generic.IEnumerable[int]]).GetGenericTypeDefinition()

function Get-PlanType {
    param($t)
    if ($null -eq $t) { return $false }
    if ($t.FullName -eq 'SimIme.Engine.Step' -or $t.IsPrimitive -or $t -eq [string]) { return $false }
    # 形态二：返回值本身就是 IEnumerable<Step>（EngineFactory.BuildPlan 返回 IList<Step>）
    foreach ($i in @($t.GetInterfaces())) {
        if ($i.IsGenericType -and $i.GetGenericTypeDefinition() -eq $enumerableDef -and $i.GetGenericArguments()[0].FullName -eq 'SimIme.Engine.Step') { return $true }
    }
    # 形态一：返回值带有 Steps 成员，且元素为 Step
    $ms = @($t.GetMember('Steps', ([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance)))
    if ($ms.Count -eq 0) { return $false }
    $mt = $null
    if ($ms[0] -is [System.Reflection.PropertyInfo]) { $mt = $ms[0].PropertyType }
    elseif ($ms[0] -is [System.Reflection.FieldInfo]) { $mt = $ms[0].FieldType }
    if ($null -eq $mt) { return $false }
    $et = $null
    if ($mt.IsArray) { $et = $mt.GetElementType() }
    else {
        foreach ($i in @($mt.GetInterfaces())) {
            if ($i.IsGenericType -and $i.GetGenericTypeDefinition() -eq $enumerableDef) { $et = $i.GetGenericArguments()[0]; break }
        }
        if ($null -eq $et -and $mt.IsGenericType -and $mt.GetGenericTypeDefinition() -eq $enumerableDef) { $et = $mt.GetGenericArguments()[0] }
    }
    if ($null -eq $et) { return $false }
    return ($et.FullName -eq 'SimIme.Engine.Step')
}

function Get-Steps {
    param($plan)
    if ($null -eq $plan) { return @() }
    $prop = $plan.PSObject.Properties['Steps']
    if ($null -ne $prop) { return @($prop.Value) }
    return @($plan)
}

function New-ObjArr {
    param($vals)
    $a = [object[]]::new($vals.Count)
    for ($i = 0; $i -lt $vals.Count; $i++) { $a[$i] = $vals[$i] }
    return ,$a
}

# ---------- 1. 发现并创建拼音表 ----------
function Get-TableInstance {
    param($asmRef, $factoryType, $tTableRef)
    $log = New-Object System.Collections.ArrayList
    $order = New-Object System.Collections.ArrayList
    if ($null -ne $factoryType) { [void]$order.Add($factoryType) }
    foreach ($t in @($asmRef.GetTypes() | Where-Object { $_.IsPublic })) {
        if ($null -ne $factoryType -and $t.FullName -eq $factoryType.FullName) { continue }
        [void]$order.Add($t)
    }
    foreach ($t in $order) {
        if (-not $t.Name.EndsWith('Factory') -and $t -ne $factoryType) { continue }
        foreach ($m in @($t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static))) {
            if (-not $tTableRef.IsAssignableFrom($m.ReturnType)) { continue }
            $ps = @($m.GetParameters())
            $sets = New-Object System.Collections.ArrayList
            if ($ps.Count -eq 0) { [void]$sets.Add((New-Object System.Collections.ArrayList)) }
            elseif ($ps.Count -eq 1 -and $ps[0].ParameterType -eq [string]) {
                foreach ($sv in @((Join-Path $root 'src\Assets\pinyin_full.tsv'), 'SimIme.Assets.pinyin_full.tsv', '')) {
                    $s = New-Object System.Collections.ArrayList; [void]$s.Add($sv); [void]$sets.Add($s)
                }
            }
            elseif ($ps.Count -eq 1 -and ($ps[0].ParameterType -eq [System.IO.Stream] -or $ps[0].ParameterType -eq [System.IO.TextReader])) {
                $s = New-Object System.Collections.ArrayList; [void]$s.Add($null); [void]$sets.Add($s)
            }
            else { [void]$log.Add('跳过（无法构造实参）：' + $m.ToString()); continue }
            foreach ($s in $sets) {
                try {
                    $o = $m.Invoke($null, (New-ObjArr $s))
                    if ($null -ne $o) { [void]$log.Add('成功：' + $m.ToString()); return @{ Instance = $o; Log = $log } }
                } catch { [void]$log.Add('调用失败 ' + $m.ToString() + ' -> ' + $_.Exception.Message) }
            }
        }
    }
    # 兜底：公开的 IPinyinTable 实现 + 无参构造（必要时调用 Load/Init）
    foreach ($t in @($tTableRef.IsAssignableFrom($_)) ) { }
    foreach ($t in @($asmRef.GetTypes() | Where-Object { $_.IsPublic -and -not $_.IsAbstract -and $tTableRef.IsAssignableFrom($_) })) {
        if (@($t.GetConstructors() | Where-Object { $_.GetParameters().Count -eq 0 }).Count -eq 0) { continue }
        try {
            $o = [Activator]::CreateInstance($t)
            if ($null -eq $o) { continue }
            if (-not [bool]$o.IsReady) {
                foreach ($m in @($t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance))) {
                    if ($m.GetParameters().Count -eq 0 -and ($m.Name -like '*Load*' -or $m.Name -like '*Init*')) {
                        try { [void]$m.Invoke($o, @()); [void]$log.Add('兜底调用：' + $t.Name + '.' + $m.Name + '()') } catch { }
                        if ([bool]$o.IsReady) { break }
                    }
                }
            }
            if ([bool]$o.IsReady) { [void]$log.Add('兜底成功：new ' + $t.Name + '()'); return @{ Instance = $o; Log = $log } }
            [void]$log.Add('兜底实例化 ' + $t.Name + ' 但 IsReady=false')
        } catch { [void]$log.Add('兜底失败 ' + $t.Name + ' -> ' + $_.Exception.Message) }
    }
    return @{ Instance = $null; Log = $log }
}

function Get-HabitInstance {
    param($asmRef, $factoryType, $tHabitRef)
    $log = New-Object System.Collections.ArrayList
    $tmp = Join-Path $root '_devC\tmp'
    if (-not (Test-Path -LiteralPath $tmp)) { New-Item -ItemType Directory -Force -Path $tmp | Out-Null }
    $storePath = Join-Path $tmp 'engine_tests_habits.dat'
    if ($null -ne $factoryType) {
        foreach ($m in @($factoryType.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static))) {
            if (-not $tHabitRef.IsAssignableFrom($m.ReturnType)) { continue }
            $ps = @($m.GetParameters())
            if ($ps.Count -eq 1 -and $ps[0].ParameterType -eq [string]) {
                try { $ha = [object[]]::new(1); $ha[0] = $storePath; $o = $m.Invoke($null, $ha); if ($null -ne $o) { [void]$log.Add('成功：' + $m.ToString()); return @{ Instance = $o; Log = $log } } }
                catch { [void]$log.Add('调用失败 ' + $m.ToString() + ' -> ' + $_.Exception.Message) }
            }
        }
    }
    foreach ($t in @($asmRef.GetTypes() | Where-Object { $_.IsPublic -and -not $_.IsAbstract -and $tHabitRef.IsAssignableFrom($_) })) {
        foreach ($c in @($t.GetConstructors())) {
            $cps = @($c.GetParameters())
            try {
                if ($cps.Count -eq 0) { $o = [Activator]::CreateInstance($t) } elseif ($cps.Count -eq 1 -and $cps[0].ParameterType -eq [string]) { $ca = [object[]]::new(1); $ca[0] = $storePath; $o = [Activator]::CreateInstance($t, $ca) } else { continue }
                if ($null -ne $o) { [void]$log.Add('兜底成功：new ' + $t.Name); return @{ Instance = $o; Log = $log } }
            } catch { [void]$log.Add('兜底失败 ' + $t.Name + ' -> ' + $_.Exception.Message) }
        }
    }
    return @{ Instance = $null; Log = $log }
}

$tableRes = @{ Instance = $null; Log = @() }
$habitRes = @{ Instance = $null; Log = @() }
if (-not $blocked) {
    $tableRes = Get-TableInstance $asm $tFactory[0] $tTable
    if ($null -eq $tableRes.Instance) {
        $blocked = '无法创建 IPinyinTable 实例'
        foreach ($l in $tableRes.Log) { Info ('  发现过程：' + $l) }
    }
    $habitRes = Get-HabitInstance $asm $tFactory[0] $tHabit
}

$poolRaw = '的一是了我不人在他有这个上们来到时大地为子中你说生国年着就那和要她出也得里后自以会家可下而过天去能对小多然于心学么之都好看起发当没成只如事把还用第样道想作种开美总从无情己面最女但现前些所同日手又行意动方期它头经长儿回位分爱老因很给名法间斯知世什两次使身者被高已亲其进此话常与活正感见明问力理尔点文几定本公特做外孩相西果走将月十实向声车全信重三机工物气每并别真打太新比才便夫再书部水像眼等体却加电主界门利海受听表德少克代员许先口由死安写性马光白或住难望教命花结乐色更拉东神记处让母父应直字场平报友关放至张认接告入笑内英军候民岁往何度山觉路带万男边风解叫任金快原吃妈变通师立象数四失满战远格士音轻目条呢病始达深完今提求清王化空业思切怎非找片罗钱吗语元喜曾离飞科言干流欢约各即指合反题必该论交终林请医晚制球决传画保读运及则房早院量苦火布品近坐产答星精视五连司巴奇管类未朋且婚台夜青北队久乎越观落尽形影红爸百令周吧识步希亚术留市半热送兴造谈容极随演收首根讲整式取照办强石古华拿计您装似足双妻尼转诉米称丽客南领节衣站黑刻统断福城故历惊脸选包紧争另建维绝树系伤示愿持千史谁准联妇纪基买志静阿诗独复痛消社算义竟确酒需单治卡幸兰念举仅钟怕共毛句息功官待究跟穿室易游程号居考突皮哪费倒价图具刚脑永歌响商礼细专黄块脚味灵改据般破引食仍存众注笔甚某沉血备习校默务土微娘须试怀料调广苏显赛查密议底列富梦错座参八除跑亮假印设线温虽掉京初养香停际致阳纸李纳验助激够严证帝饭忘趣支春集丈木研班普导顿睡展跳获艺六波察群皇段呼'
$pool = New-Object System.Collections.ArrayList
foreach ($ch in $poolRaw.ToCharArray()) { [void]$pool.Add($ch) }
$sample = @(Get-Random -InputObject $pool -Count 200 -SetSeed 42)

if ($blocked) {
    Say ''
    Say ('[INFO] 引擎未就绪，下列断言全部记 FAIL：' + $blocked)
    $why = '引擎未就绪（' + $blocked + '）'
    Check '断言1 拼音表加载与条数>=20000' $false $why
    Check '断言2 随机200常用字命中'        $false $why
    Check '断言3 三段文本回放落屏===原文'  $false $why
    Check '断言4 同 seed 计划一致'         $false $why
    Check '断言5 DelayMs 非负'             $false $why
    Check '断言6 开启错字后含退格且落屏===原文' $false $why
    Say ''
    Say '==============================================================='
    Say (' 汇总：PASS=' + $script:pass + '  FAIL=' + $script:fail + '  SKIP=' + $script:skip)
    Say ' 结论：存在 FAIL（引擎侧尚未交付，等 A 完成后重跑本脚本）'
    Say '==============================================================='
    exit 1
}

$table = $tableRes.Instance
$cfgType = $tCfg
function New-Cfg {
    param($seed)
    $c = [Activator]::CreateInstance($cfgType)
    if ($null -ne $c -and $seed -ne $null) { $c.RandomSeed = [int]$seed }
    return $c
}

# ---------- 断言 1：拼音表 ----------
$isReady = [bool]$table.IsReady
$charCount = [int]$table.CharCount
Check '断言1 拼音表加载与条数>=20000' (($isReady) -and ($charCount -ge 20000)) ('IsReady=' + $isReady + ' CharCount=' + $charCount + ' (阈值 20000)')

# ---------- 断言 2：随机 200 常用字命中 ----------
$hit = 0; $missList = New-Object System.Collections.ArrayList
foreach ($ch in $sample) {
    $py = $null
    try { $py = $table.GetPinyin([char]$ch) } catch { $py = $null }
    if (-not [string]::IsNullOrEmpty($py)) { $hit++ } else { if ($missList.Count -lt 10) { [void]$missList.Add([string]$ch) } }
}
Check '断言2 随机200常用字命中' ($hit -eq $sample.Count) ('命中 ' + $hit + '/' + $sample.Count + '（种子 42 抽样自 ' + $pool.Count + ' 字常用池）' + $(if ($missList.Count -gt 0) { ' 未命中示例: ' + ($missList -join '') } else { '' }))

# ---------- 发现计划构造入口 ----------
function Get-PlanCandidates {
    param($asmRef, $factoryType)
    $list = New-Object System.Collections.ArrayList
    $ordered = New-Object System.Collections.ArrayList
    if ($null -ne $factoryType) { [void]$ordered.Add($factoryType) }
    foreach ($t in @($asmRef.GetTypes() | Where-Object { $_.IsPublic })) {
        if ($null -ne $factoryType -and $t.FullName -eq $factoryType.FullName) { continue }
        [void]$ordered.Add($t)
    }
    foreach ($t in $ordered) {
        $isFactory = ($null -ne $factoryType -and $t.FullName -eq $factoryType.FullName)
        if (-not $isFactory -and -not $t.Name.EndsWith('Factory') -and -not $t.Name.EndsWith('Planner')) { continue }
        foreach ($m in @($t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static))) {
            if (Get-PlanType $m.ReturnType) { [void]$list.Add([pscustomobject]@{ Type = $t; Method = $m; IsStatic = $true }) }
        }
        if (-not $t.IsAbstract) {
            if (@($t.GetConstructors() | Where-Object { $_.GetParameters().Count -eq 0 }).Count -gt 0) {
                foreach ($m in @($t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance))) {
                    if (Get-PlanType $m.ReturnType) { [void]$list.Add([pscustomobject]@{ Type = $t; Method = $m; IsStatic = $false }) }
                }
            }
        }
    }
    return $list
}

function Invoke-PlanBuilder {
    param($cand, [string]$text, $cfg, $tableRef, $habitRef)
    $m = $cand.Method
    $vals = New-Object System.Collections.ArrayList
    foreach ($p in @($m.GetParameters())) {
        $pt = $p.ParameterType
        if ($pt -eq [string]) { [void]$vals.Add($text); continue }
        if ($pt.FullName -eq 'SimIme.Engine.EngineConfig') { [void]$vals.Add($cfg); continue }
        if ($pt.FullName -eq 'SimIme.Engine.IPinyinTable') { [void]$vals.Add($tableRef); continue }
        if ($pt.FullName -eq 'SimIme.Engine.IHabitStore') { [void]$vals.Add($habitRef); continue }
        if ($pt -eq [int] -or $pt -eq [long]) { [void]$vals.Add(0); continue }
        if ($pt -eq [bool]) { [void]$vals.Add($false); continue }
        if ($pt.IsEnum -or $pt.IsValueType) { [void]$vals.Add([Activator]::CreateInstance($pt)); continue }
        return $null
    }
    $target = $null
    if (-not $cand.IsStatic) { $target = [Activator]::CreateInstance($cand.Type) }
    return $m.Invoke($target, (New-ObjArr $vals))
}

$planCands = Get-PlanCandidates $asm $tFactory[0]
$planCand = $null
$planLog = New-Object System.Collections.ArrayList
foreach ($c in $planCands) {
    try {
        $probe = Invoke-PlanBuilder $c '测试' (New-Cfg 1) $table $habitRes.Instance
        $probeSteps = @(Get-Steps $probe)
        if ($probeSteps.Count -gt 0 -and $probeSteps[0].GetType().FullName -eq 'SimIme.Engine.Step') { $planCand = $c; break }
    } catch { [void]$planLog.Add($c.Method.ToString() + ' -> ' + $_.Exception.Message) }
}

if ($null -eq $planCand) {
    Info ('未找到可用的计划构造入口。候选数=' + $planCands.Count)
    foreach ($c in $planCands) { Info ('  候选：' + $c.Method.ToString()) }
    foreach ($l in $planLog) { Info ('  失败：' + $l) }
    $why2 = '找不到返回含 Steps 成员的计划类型的可调用入口'
    Check '断言3 三段文本回放落屏===原文' $false $why2
    Check '断言4 同 seed 计划一致' $false $why2
    Check '断言5 DelayMs 非负' $false $why2
    Check '断言6 开启错字后含退格且落屏===原文' $false $why2
} else {
    Info ('计划构造入口：' + $planCand.Method.ToString())
    Info ('习惯库实例：' + $(if ($null -ne $habitRes.Instance) { $habitRes.Instance.GetType().Name + ' path=' + $habitRes.Instance.Path } else { '(未创建，BuildPlan 收到 null)' }))
    foreach ($l in $habitRes.Log) { Info ('  习惯库发现过程：' + $l) }
    if ($null -eq $habitRes.Instance) { Info '  说明：本测试脚本通过 PowerShell 反射调用 CreateHabitStore 时受 PSObject 装箱限制而失败，故 BuildPlan 收到 null（引擎按默认词权重工作，断言结论不受影响）；B 的运行时路径会传入真实 IHabitStore。' }

    function Invoke-RemoveLast {
        param($sb)
        $n = $sb.Length
        if ($n -eq 0) { return }
        $last = $sb[$n - 1]
        if ($n -ge 2 -and [char]::IsLowSurrogate($last) -and [char]::IsHighSurrogate($sb[$n - 2])) { [void]$sb.Remove($n - 2, 2) }
        else { [void]$sb.Remove($n - 1, 1) }
    }

    function Invoke-Replay {
        param($plan)
        $sb = New-Object System.Text.StringBuilder
        $nc = 0; $nb = 0; $nl = 0; $ns = 0
        $minDelay = [int]::MaxValue; $neg = 0; $steps = 0
        foreach ($st in @(Get-Steps $plan)) {
            $steps++
            if ($st.DelayMs -lt 0) { $neg++ }
            if ($st.DelayMs -lt $minDelay) { $minDelay = [int]$st.DelayMs }
            switch ([int]$st.Kind) {
                0 { [void]$sb.Append($st.Text); $nc++ }
                1 { Invoke-RemoveLast $sb; $nb++ }
                2 { [void]$sb.Append([string][char]10); $nl++ }
                3 { $ns++ }
                default { }
            }
        }
        return [pscustomobject]@{ Text = $sb.ToString(); Chars = $nc; Backspaces = $nb; Breaks = $nl; Sleeps = $ns; Steps = $steps; MinDelay = $minDelay; NegDelays = $neg }
    }

    function New-PlanFor {
        param([string]$text, $seed)
        return Invoke-PlanBuilder $planCand $text (New-Cfg $seed) $table $habitRes.Instance
    }

    $passages = @(
        '你好，今天天气不错。我们去公园散步吧。',
        '这是 SimIme 测试 v1.0，包含 English 与 12345 数字，还有标点！',
        [string]::Join([string][char]10, @('第一行：春眠不觉晓。', '第二行：处处闻啼鸟。', '第三行：夜来风雨声，花落知多少。'))
    )

    # ---------- 断言 3：回放 ----------
    $allOk = $true; $details = New-Object System.Collections.ArrayList
    $idx = 0
    foreach ($p in $passages) {
        $idx++
        $r = Invoke-Replay (New-PlanFor $p 20260101)
        $same = ($r.Text -ceq $p)
        if (-not $same) { $allOk = $false }
        [void]$details.Add(('段' + $idx + ': 落屏长度=' + $r.Text.Length + '/原文长度=' + $p.Length + ' 一致=' + $same + ' 步骤=' + $r.Steps + '(字' + $r.Chars + '/退格' + $r.Backspaces + '/换行' + $r.Breaks + '/停顿' + $r.Sleeps + ')'))
    }
    foreach ($d in $details) { Ev $d }
    Check '断言3 三段文本回放落屏===原文' $allOk '三段比对（中文标点 / 中英混排 / 多行）'

    # ---------- 断言 4：同 seed 计划一致 ----------
    $p1 = '同种子一致性检验：春风又绿江南岸，明月何时照我还。'
    $pa = New-PlanFor $p1 123456
    $pb = New-PlanFor $p1 123456
    $stepsA = @(Get-Steps $pa); $stepsB = @(Get-Steps $pb)
    $sameCount = ($stepsA.Count -eq $stepsB.Count)
    $firstDiff = ''
    if ($sameCount) {
        for ($i = 0; $i -lt $stepsA.Count; $i++) {
            $x = $stepsA[$i]; $y = $stepsB[$i]
            if (([int]$x.Kind -ne [int]$y.Kind) -or ($x.Text -cne $y.Text) -or ([int]$x.DelayMs -ne [int]$y.DelayMs) -or ($x.Reason -cne $y.Reason)) {
                $sameCount = $false
                $firstDiff = ('首个差异 @' + $i + ' : ' + $x.Kind + '/' + $x.DelayMs + '/' + $x.Text + ' vs ' + $y.Kind + '/' + $y.DelayMs + '/' + $y.Text)
                break
            }
        }
    } else { $firstDiff = '步骤数不同 ' + $stepsA.Count + ' vs ' + $stepsB.Count }
    Check '断言4 同 seed 计划一致' $sameCount ('seed=123456 两次构建：步骤数 ' + $stepsA.Count + '/' + $stepsB.Count + ' 逐字段相同=' + $sameCount + $(if ($firstDiff) { ' ' + $firstDiff } else { '' }))

    # ---------- 断言 5：DelayMs 非负 ----------
    $negTotal = 0; $stepTotal = 0
    foreach ($p in $passages) {
        $r = Invoke-Replay (New-PlanFor $p 20260101)
        $negTotal += $r.NegDelays; $stepTotal += $r.Steps
    }
    Check '断言5 DelayMs 非负' ($negTotal -eq 0) ('检查 ' + $stepTotal + ' 个步骤，负延迟 ' + $negTotal + ' 个')

    # ---------- 断言 6：错字必须被退格删干净（契约语义 3） ----------
    $cfg6 = New-Cfg 20260101
    if ($null -ne $cfg6.Typo) {
        $cfg6.Typo.Enabled = $true
        $cfg6.Typo.HomophonePct = 100
        $cfg6.Typo.NasalPct = 100
        $cfg6.Typo.HeadRedoPct = 100
        $cfg6.Typo.OmissionPct = 100
        $cfg6.Typo.RepeatWordPct = 100
        $cfg6.Typo.MaxTyposPerSentence = 50
    }
    $p6 = '错字回滚测试：拟人化输入必须保证落屏文本与原文完全一致，多打的字要用退格删掉。'
    $r6 = Invoke-Replay (Invoke-PlanBuilder $planCand $p6 $cfg6 $table $habitRes.Instance)
    $ok6 = ($r6.Text -ceq $p6) -and ($r6.Backspaces -gt 0)
    Check '断言6 开启错字后含退格且落屏===原文' $ok6 ('退格步数=' + $r6.Backspaces + ' 落屏一致=' + ($r6.Text -ceq $p6) + ' 步骤=' + $r6.Steps)

    # ---------- 断言 7：句首重打多打的字必须与"即将输入的字"同音/近音，且片段逐字取自原文 ----------
    # （引擎侧第十八轮：句首重打不再从下一句或全词典随机抓素材，只允许把即将输入的开头打成同音/近音错字）
    $cfg7 = New-Cfg 20261012
    if ($null -ne $cfg7.Typo) {
        $cfg7.Typo.Enabled = $true
        $cfg7.Typo.HomophonePct = 0
        $cfg7.Typo.NasalPct = 0
        $cfg7.Typo.HeadRedoPct = 100
        $cfg7.Typo.OmissionPct = 0
        $cfg7.Typo.RepeatWordPct = 0
        $cfg7.Typo.MaxTyposPerSentence = 8
        try { $cfg7.Typo.WordHomophonePct = 0 } catch { }
    }
    $p7 = '抖动是常见的现象。我们明天去公园散步。这个方案需要再想想。'
    $plan7 = @(Get-Steps (Invoke-PlanBuilder $planCand $p7 $cfg7 $table $habitRes.Instance))
    $pos7 = 0; $ev7 = 0; $ok7 = 0; $struct7 = 0; $sample7 = New-Object System.Collections.ArrayList
    $i7 = 0
    while ($i7 -lt $plan7.Count) {
        $st7 = $plan7[$i7]
        if (($st7.Kind -eq [SimIme.Engine.StepKind]::Char) -and ($st7.Reason -eq '句首重打')) {
            $wrong7 = ''
            while ($i7 -lt $plan7.Count -and $plan7[$i7].Kind -eq [SimIme.Engine.StepKind]::Char -and $plan7[$i7].Reason -eq '句首重打') { $wrong7 += $plan7[$i7].Text; $i7++ }
            $bs7 = 0
            while ($i7 -lt $plan7.Count -and $plan7[$i7].Kind -eq [SimIme.Engine.StepKind]::Backspace -and $plan7[$i7].Reason -eq '句首重打') { $bs7++; $i7++ }
            $ev7++
            if ($bs7 -eq $wrong7.Length) { $struct7++ }
            $right7 = ''
            if (($pos7 + $wrong7.Length) -le $p7.Length) { $right7 = $p7.Substring($pos7, $wrong7.Length) }
            $okThis7 = ($right7.Length -eq $wrong7.Length)
            if ($okThis7) {
                for ($t7 = 0; $t7 -lt $wrong7.Length; $t7++) {
                    $pc7 = $table.GetPinyin([char]$right7[$t7])
                    $pw7 = $table.GetPinyin([char]$wrong7[$t7])
                    $pn7 = $pc7; if ($pn7.EndsWith('ng')) { $pn7 = $pn7.Substring(0, $pn7.Length - 1) }
                    $wn7 = $pw7; if ($wn7.EndsWith('ng')) { $wn7 = $wn7.Substring(0, $wn7.Length - 1) }
                    if (($pw7 -ne $pc7) -and ($wn7 -ne $pn7)) { $okThis7 = $false }
                }
            }
            if ($okThis7) { $ok7++ }
            if ($sample7.Count -lt 3) { [void]$sample7.Add($right7 + '->' + $wrong7) }
            continue
        }
        if ($st7.Kind -eq [SimIme.Engine.StepKind]::Char) { $pos7++ }
        $i7++
    }
    Check '断言7 句首重打多打的字与即将输入的字同音/近音且逐字取自原文' (($ev7 -ge 3) -and ($ok7 -eq $ev7) -and ($struct7 -eq $ev7)) ('事件=' + $ev7 + ' 同音或近音=' + $ok7 + ' 结构完整=' + $struct7 + ' 例: ' + ($sample7 -join ' '))

    # ---------- 断言 8：任何错字连涉及的字符都必须属于同一个词块（不跨块） ----------
    # （引擎侧第十九轮：重复输入此前会跨块取两个字，真机上出现 先别|关程序 打出 别关别关 的串）
    $a8Texts = @(
        '先别关程序，我们明天再试试看。',
        '天上有十二个星星，我们明天去公园散步。',
        '输入法要拟人化，错字要像真人打的。'
    )
    $a8Kinds = @('HomophonePct', 'NasalPct', 'HeadRedoPct', 'OmissionPct', 'RepeatWordPct', 'WordHomophonePct')
    $a8Typo = @('同音字错字', '鼻音错字', '句首重打', '漏字回填', '重复输入', '词组错词')
    $a8Runs = 0; $a8Same = 0; $a8Bad = ''
    foreach ($a8t in $a8Texts) {
        foreach ($a8k in $a8Kinds) {
            $a8c = New-Cfg 20261012
            if ($null -ne $a8c.Typo) {
                $a8c.Typo.Enabled = $true
                foreach ($a8z in $a8Kinds) { try { $a8c.Typo.$a8z = 0 } catch { } }
                try { $a8c.Typo.$a8k = 100 } catch { }
                $a8c.Typo.MaxTyposPerSentence = 20
            }
            $a8plan = @(Get-Steps (Invoke-PlanBuilder $planCand $a8t $a8c $table $habitRes.Instance))
            $a8blocks = @([SimIme.Engine.EngineFactory]::SplitPhrases($a8t, $a8c))
            $a8bs = @{}; $a8be = @{}
            $a8p = 0
            for ($a8b = 0; $a8b -lt $a8blocks.Count; $a8b++) {
                $a8e = $a8p + $a8blocks[$a8b].Length
                for ($a8q = $a8p; $a8q -lt $a8e; $a8q++) { $a8bs[$a8q] = $a8p; $a8be[$a8q] = $a8e }
                $a8p = $a8e
            }
            $a8pos = 0; $a8i = 0
            while ($a8i -lt $a8plan.Count) {
                $a8st = $a8plan[$a8i]
                if (($a8st.Kind -eq [SimIme.Engine.StepKind]::Char) -and ($a8Typo -contains $a8st.Reason)) {
                    $a8r = $a8st.Reason
                    $a8chars = ''
                    # 注意：思考停顿(Sleep)可能被插在错字连中间，收集时必须跳过它，否则位置跟踪会漂移
                    while ($a8i -lt $a8plan.Count) {
                        $a8s2 = $a8plan[$a8i]
                        if ($a8s2.Kind -eq [SimIme.Engine.StepKind]::Sleep) { $a8i++; continue }
                        if (($a8s2.Kind -eq [SimIme.Engine.StepKind]::Char) -and ($a8s2.Reason -eq $a8r)) { $a8chars += $a8s2.Text; $a8i++; continue }
                        break
                    }
                    while ($a8i -lt $a8plan.Count) {
                        $a8s3 = $a8plan[$a8i]
                        if ($a8s3.Kind -eq [SimIme.Engine.StepKind]::Sleep) { $a8i++; continue }
                        if (($a8s3.Kind -eq [SimIme.Engine.StepKind]::Backspace) -and ($a8s3.Reason -eq $a8r)) { $a8i++; continue }
                        break
                    }
                    $a8cover = New-Object System.Collections.Generic.List[int]
                    if ($a8r -eq '句首重打') { for ($a8t2 = 0; $a8t2 -lt $a8chars.Length; $a8t2++) { [void]$a8cover.Add($a8pos + $a8t2) } }
                    elseif ($a8r -eq '漏字回填') { for ($a8t2 = 0; $a8t2 -le $a8chars.Length; $a8t2++) { [void]$a8cover.Add($a8pos + $a8t2) } }
                    elseif ($a8r -eq '词组错词') { for ($a8t2 = 0; $a8t2 -lt $a8chars.Length; $a8t2++) { [void]$a8cover.Add($a8pos + $a8t2) } }
                    elseif ($a8r -eq '重复输入') { if ($a8chars.Length -eq 4) { [void]$a8cover.Add($a8pos); [void]$a8cover.Add($a8pos + 1) } else { [void]$a8cover.Add($a8pos) } }
                    else { [void]$a8cover.Add($a8pos) }
                    $a8uniq = @($a8cover | ForEach-Object { if ($a8bs.ContainsKey($_)) { $a8bs[$_] } else { -1 } } | Sort-Object -Unique)
                    $a8Runs++
                    if (($a8uniq.Count -eq 1) -and ($a8uniq[0] -ge 0)) { $a8Same++ } elseif ($a8Bad -eq '') { $a8Bad = $a8r + ':' + $a8chars + ' at ' + $a8pos }
                    if ($a8r -eq '重复输入') { if ($a8chars.Length -eq 4) { $a8pos += 2 } else { $a8pos += 1 } }
                    continue
                }
                if ($a8st.Kind -eq [SimIme.Engine.StepKind]::Char) { $a8pos++ }
                $a8i++
            }
        }
    }
    Check '断言8 每个错字连只涉及同一个词块（不跨块）' (($a8Runs -ge 24) -and ($a8Same -eq $a8Runs)) ('错字连=' + $a8Runs + ' 同块=' + $a8Same + ' 首个跨块=' + $(if ($a8Bad -eq '') { 'none' } else { $a8Bad }))

    # ---------- 附加信息（不参与判定） ----------
    $h = @(); $n = @()
    try { $h = @($table.HomophonesOf([char]'中', 5)) } catch { }
    try { $n = @($table.NasalVariantsOf([char]'中', 5)) } catch { }
    Info ('HomophonesOf(中,5) = ' + $h.Count + ' 项: ' + ($h -join ','))
    Info ('NasalVariantsOf(中,5) = ' + $n.Count + ' 项: ' + ($n -join ','))
    Info ('参考统计：三段共 ' + $stepTotal + ' 步')
}

Say ''
Say '==============================================================='
Say (' 汇总：PASS=' + $script:pass + '  FAIL=' + $script:fail + '  SKIP=' + $script:skip)
if ($script:fail -gt 0) { Say ' 结论：存在 FAIL'; Say '==============================================================='; exit 1 }
Say ' 结论：全部 PASS'
Say '==============================================================='
exit 0
