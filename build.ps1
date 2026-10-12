# SimIme 一键构建脚本（C 组交付物）
# 用法：pwsh -NoProfile -File build.ps1（在项目目录里执行）
# 退出码：0 = 成功；1 = 编译失败；2 = 环境缺失 / 源码未就绪（附清晰提示）
# 幂等：每次运行先清空 dist 再产出，连续两次运行结果一致。
# 说明：编译器为 .NET Framework 4.0 的 csc.exe（C# 5），全部源码追加 /codepage:65001。

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) { $PSNativeCommandUseErrorActionPreference = $false }
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$root    = $PSScriptRoot
$fwDir   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$cscExe  = Join-Path $fwDir 'csc.exe'
$distDir = Join-Path $root 'dist'
$objDir  = Join-Path $root '_devC\obj'
$engDir  = Join-Path $root 'src\Engine'
$appDir  = Join-Path $root 'src\App'
$asset   = Join-Path $root 'src\Assets\pinyin_full.tsv'
$assetDir = Join-Path $root 'src\Assets'
$toolsDir = Join-Path $root 'src\Tools'
$adminManifest = Join-Path $toolsDir 'SimIme-Admin.manifest'
$adminOut = Join-Path $distDir 'SimIme-Admin.exe'
$dllOut  = Join-Path $distDir 'SimIme.Engine.dll'
$stageDir  = Join-Path $objDir 'stage'                                  # 编译暂存（编译全过才动 dist）
$dllStage  = Join-Path $stageDir 'SimIme.Engine.dll'
$exeStage  = Join-Path $stageDir 'SimIme.exe'
$adminStage= Join-Path $stageDir 'SimIme-Admin.exe'
$script:buildStart = Get-Date                                        # 本轮构建开始时间（收尾断言用它判断产物是否真的被更新）
$script:stashDir = Join-Path $objDir 'userdata-stash'   # 用户数据暂存区（在 dist 之外）
$script:stashedNames = @()
$exeOut  = Join-Path $distDir 'SimIme.exe'

function Say  { param([string]$t) Write-Output $t }
function Ok   { param([string]$t) Write-Output ('[OK]   ' + $t) }
function Bad  { param([string]$t) Write-Output ('[FAIL] ' + $t) }
function Warn { param([string]$t) Write-Output ('[WARN] ' + $t) }
function Hint { param([string]$t) Write-Output ('       ' + $t) }

# 未预期异常也要把用户数据放回去，绝不因为构建失败丢数据
trap { Restore-UserData; Write-Output ('[FAIL] 未预期的错误：' + $_.Exception.Message); Write-Output '构建中止，退出码 9'; exit 9 }

function Restore-UserData {
    if ($null -ne $script:stashedNames -and $script:stashedNames.Count -gt 0) {
        $put = New-Object System.Collections.ArrayList
        foreach ($n in $script:stashedNames) {
            $from = Join-Path $script:stashDir $n
            $to = Join-Path $distDir $n
            if (-not (Test-Path -LiteralPath $from)) { continue }
            try {
                if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Force -Path $distDir | Out-Null }
                Move-Item -LiteralPath $from -Destination $to -Force
                [void]$put.Add($n)
            } catch { Warn ('用户数据放回失败：' + $n + ' -> ' + $_.Exception.Message) }
        }
        if ($put.Count -gt 0) { Ok ('用户数据已原样放回 dist：' + ($put -join ' / ')) }
        $script:stashedNames = @()
    }
    if ($null -ne $script:logKept -and $script:logKept.Count -gt 0) {
        foreach ($nm in $script:logKept) {
            $fromK = Join-Path $script:logHoldDir $nm
            if (-not (Test-Path -LiteralPath $fromK)) { continue }
            try {
                Move-Item -LiteralPath $fromK -Destination (Join-Path $distDir $nm) -Force -ErrorAction Stop
            } catch { Warn ('旧日志归档放回失败：' + $nm + ' -> ' + $_.Exception.Message) }
        }
        $script:logKept = New-Object System.Collections.ArrayList
    }
    # 日志归档：上一轮的 error.log / selftest.log 以 .old-<时间戳> 放回 dist（不参与用户数据校验），每类只保留最近 3 份
    if ($null -ne $script:logArchived -and $script:logArchived.Count -gt 0) {
        foreach ($e in @($script:logArchived)) {
            $from = Join-Path $script:logHoldDir $e.Name
            if (-not (Test-Path -LiteralPath $from)) { continue }
            if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Force -Path $distDir | Out-Null }
            $base = $e.Name + '.old-' + $e.Stamp
            $target = Join-Path $distDir $base
            $k = 2
            while (Test-Path -LiteralPath $target) { $target = Join-Path $distDir ($base + '-' + $k); $k++ }
            try {
                Move-Item -LiteralPath $from -Destination $target -Force -ErrorAction Stop
                Ok ('已归档上一轮日志：' + $e.Name + ' -> ' + (Split-Path -Leaf $target) + '（上一轮日志没被删除，可在 dist 里取证）')
            } catch { Warn ('日志归档放回失败：' + $e.Name + ' -> ' + $_.Exception.Message) }
        }
        $script:logArchived = New-Object System.Collections.ArrayList
    }
    foreach ($ln in @('error.log', 'selftest.log')) {
        $arch = @(Get-ChildItem -LiteralPath $distDir -File -Filter ($ln + '.old-*') -ErrorAction SilentlyContinue | Sort-Object Name)
        if ($arch.Count -gt 3) {
            foreach ($d in @($arch | Select-Object -First ($arch.Count - 3))) {
                try { Remove-Item -LiteralPath $d.FullName -Force -ErrorAction Stop; Warn ('已删除更早的日志归档：' + $d.Name + '（每类只保留最近 3 份）') } catch { }
            }
        }
    }
}
function Stop-Build {
    param([string]$msg, [int]$code)
    Restore-UserData
    Bad $msg
    Say ''
    Say ('构建中止，退出码 ' + $code)
    exit $code
}

function Get-CsSources {
    param([string]$dir)
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Filter *.cs -Recurse -File -ErrorAction SilentlyContinue |
             Sort-Object FullName | ForEach-Object { $_.FullName })
}

function Get-ArtifactInfo {
    param([string]$path)
    $fi = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $nl = [Environment]::NewLine
    return New-Object PSObject -Property @{
        Path = $fi.FullName
        Bytes = $fi.Length
        Hash = $hash
        Text = ('       产物   : ' + $fi.FullName + $nl + '       字节   : ' + $fi.Length + $nl + '       SHA256 : ' + $hash)
    }
}

function Show-Artifact {
    param([string]$path)
    Say (Get-ArtifactInfo $path).Text
}

Say '==============================================================='
Say ' SimIme build.ps1 - 一键构建（引擎库 + 主程序）'
Say (' 时间   : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say (' 根目录 : ' + $root)
Say '==============================================================='

# ---------- 1. 环境探测 ----------
if (-not (Test-Path -LiteralPath $cscExe)) {
    Stop-Build ('找不到 C# 编译器：' + $cscExe + '（需要 .NET Framework 4.x 运行时目录）') 2
}
$cscVer = (Get-Item -LiteralPath $cscExe).VersionInfo.FileVersion
Ok ('编译器：' + $cscExe + '  (FileVersion ' + $cscVer + '，C# 5 语法上限)')

$coreRefNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
$refs = New-Object System.Collections.ArrayList
foreach ($n in $coreRefNames) {
    $p = Join-Path $fwDir $n
    if (-not (Test-Path -LiteralPath $p)) { Stop-Build ('缺少必需引用程序集：' + $p) 2 }
    [void]$refs.Add($p)
}
Ok ('基础引用（' + $refs.Count + ' 个）：' + ($refs -join '; '))

$uiaCandidates = @(
    @{ Tag = 'Reference Assemblies .NETFramework v4.8'
       Dir    = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
       Client = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\UIAutomationClient.dll'
       Types  = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\UIAutomationTypes.dll' },
    @{ Tag = 'Framework64 WPF 目录'
       Dir    = (Join-Path $fwDir 'WPF')
       Client = (Join-Path $fwDir 'WPF\UIAutomationClient.dll')
       Types  = (Join-Path $fwDir 'WPF\UIAutomationTypes.dll') }
)
$uiaTag = $null
foreach ($c in $uiaCandidates) {
    if ((Test-Path -LiteralPath $c.Client) -and (Test-Path -LiteralPath $c.Types)) {
        [void]$refs.Add($c.Client)
        [void]$refs.Add($c.Types)
        Ok ('UIA 引用来源：' + $c.Tag)
        Say ('       选中的路径：' + $c.Client)
        Say ('       选中的路径：' + $c.Types)
        # UIA 家族其余必需程序集（任务书原清单只有 Client+Types，实测不够）：
        #   UIAutomationProvider.dll -> 命名空间 System.Windows.Automation.Provider
        #   WindowsBase.dll          -> System.Windows.Rect（TextPatternRange.GetBoundingRectangles()
        #                              与 AutomationElement.Current.BoundingRectangle 的返回类型）
        $uiaExtras = @(
            @{ Name = 'UIAutomationProvider.dll'; Why = 'System.Windows.Automation.Provider' },
            @{ Name = 'WindowsBase.dll';          Why = 'System.Windows.Rect（UIA 边界矩形返回类型）' }
        )
        foreach ($x in $uiaExtras) {
            $p = Join-Path $c.Dir $x.Name
            if (Test-Path -LiteralPath $p) { [void]$refs.Add($p); Say ('       选中的路径：' + $p + '   (' + $x.Why + ')') }
            else { Warn ('该来源缺少 ' + $x.Name + '：' + $p + '（需要 ' + $x.Why + '）') }
        }
        $uiaTag = $c.Tag
        break
    }
}
if ($null -eq $uiaTag) {
    Warn '未找到 UIAutomationClient.dll / UIAutomationTypes.dll（已依次探测 Reference Assemblies v4.8 与 Framework64\WPF）'
    Hint '引擎库仍可编译；若 src\App 使用 UIA，主程序编译会报缺失类型。'
}

# ---------- 2. 源码清点 ----------
$engSources = Get-CsSources $engDir
$appSources = Get-CsSources $appDir
Say ''
Say ('源码清点：src\Engine = ' + $engSources.Count + ' 个 .cs；src\App = ' + $appSources.Count + ' 个 .cs')
foreach ($s in $engSources) { Say ('       [Engine] ' + $s.Substring($root.Length + 1)) }
foreach ($s in $appSources) { Say ('       [App]    ' + $s.Substring($root.Length + 1)) }

if ($engSources.Count -eq 0) {
    Say ''
    Bad ('src\Engine 下没有任何 .cs 文件：' + $engDir)
    Hint '等待 A 交付引擎源码（至少含 src\Engine\EngineTypes.cs 与 EngineFactory）。'
    Hint '本脚本刻意不生成空壳产物；请等源码到位后重新运行。'
    Say ''
    Say '构建中止，退出码 2'
    exit 2
}

# ---------- 4. 编译函数 ----------
function Invoke-Csc {
    param(
        [string]   $Label,
        [string]   $Target,
        [string]   $OutFile,
        [string[]] $Sources,
        [string[]] $References,
        [string[]] $Extra
    )
    $rsp = Join-Path $objDir ($Label + '.rsp')
    $argLines = New-Object System.Collections.ArrayList
    [void]$argLines.Add('/nologo')
    [void]$argLines.Add('/codepage:65001')
    [void]$argLines.Add('/utf8output')
    [void]$argLines.Add('/warn:4')
    [void]$argLines.Add('/optimize+')
    [void]$argLines.Add('/target:' + $Target)
    [void]$argLines.Add('/out:"' + $OutFile + '"')
    foreach ($r in $References) { [void]$argLines.Add('/reference:"' + $r + '"') }
    foreach ($e in $Extra) { [void]$argLines.Add($e) }
    foreach ($s in $Sources) { [void]$argLines.Add('"' + $s + '"') }
    [System.IO.File]::WriteAllLines($rsp, $argLines.ToArray(), (New-Object System.Text.ASCIIEncoding))

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $raw = & $cscExe ('@' + $rsp) 2>&1
        $code = $LASTEXITCODE
    } catch {
        $raw = @('调用 csc.exe 抛出异常：' + $_.Exception.Message)
        $code = 1
    }
    $sw.Stop()
    $lines = @($raw | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } })
    return New-Object PSObject -Property @{
        ExitCode = $code
        Lines    = $lines
        ElapsedMs = [int]$sw.ElapsedMilliseconds
        Rsp      = $rsp
    }
}

function Show-CscResult {
    param($Result, [string]$Label)
    $errs  = @($Result.Lines | Where-Object { $_ -match ': error [A-Z]+\d+' })
    $warns = @($Result.Lines | Where-Object { $_ -match ': warning [A-Z]+\d+' })
    if ($Result.ExitCode -ne 0) {
        Say ''
        Bad ($Label + ' 编译失败（csc 退出码 ' + $Result.ExitCode + '，错误 ' + $errs.Count + ' 条，警告 ' + $warns.Count + ' 条，耗时 ' + $Result.ElapsedMs + ' ms）')
        Say '       完整编译器输出：'
        foreach ($l in $Result.Lines) { Say ('       | ' + $l) }
        Say ('       响应文件：' + $Result.Rsp)
        Say ''
        Restore-UserData
        Say '构建中止：dist 未被改动（仍是上一版产物）'
        Say '构建中止，退出码 1'
        exit 1
    }
    Say ''
    Ok ($Label + ' 编译成功（耗时 ' + $Result.ElapsedMs + ' ms，警告 ' + $warns.Count + ' 条）')
    if ($warns.Count -gt 0) {
        $shown = 0
        foreach ($w in $warns) { if ($shown -lt 20) { Say ('       [警告] ' + $w); $shown++ } }
        if ($warns.Count -gt 20) { Say ('       ... 其余 ' + ($warns.Count - 20) + ' 条警告见响应文件输出') }
    }
    Show-Artifact $Result.OutFile
}

# ---------- 5. 资源清点（引擎库与主程序内嵌同一套；资源名 SimIme.Assets.<文件名>） ----------
$resourceArgs = New-Object System.Collections.ArrayList
$assetProblem = $null
if (-not (Test-Path -LiteralPath $assetDir)) {
    $assetProblem = '缺少资源目录：' + $assetDir
} else {
    $tsvFiles = @(Get-ChildItem -LiteralPath $assetDir -Filter *.tsv -File -ErrorAction SilentlyContinue | Sort-Object Name)
    if ($tsvFiles.Count -eq 0) { $assetProblem = '资源目录里没有任何 .tsv：' + $assetDir }
    elseif (@($tsvFiles | Where-Object { $_.Name -eq 'pinyin_full.tsv' }).Count -eq 0) { $assetProblem = '缺少必需资源：' + (Join-Path $assetDir 'pinyin_full.tsv') }
    else {
        foreach ($tf in $tsvFiles) {
            $resName = 'SimIme.Assets.' + $tf.Name
            [void]$resourceArgs.Add('/resource:"' + $tf.FullName + '",' + $resName)
            Ok ('内嵌资源：' + $tf.FullName + ' -> ' + $resName + ' (' + $tf.Length + ' 字节)')
        }
    }
}
if ($null -ne $assetProblem) {
    Warn $assetProblem
    Hint '引擎库仍可编译（表为空时 IsReady=false）；主程序阶段会中止并给出指引。'
}

# ---------- 5b. 准备暂存目录（本轮编译只写这里；编译全过才允许动 dist） ----------
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Ok ('暂存目录已就绪：' + $stageDir)

# ---------- 6. 阶段一：引擎库 ----------
Say ''
Say '---------------------------------------------------------------'
Say ' 阶段一：编译引擎库 -> dist\SimIme.Engine.dll'
Say '---------------------------------------------------------------'
$r1 = Invoke-Csc -Label 'engine' -Target 'library' -OutFile $dllStage -Sources $engSources -References $refs.ToArray() -Extra $resourceArgs.ToArray()
$r1 | Add-Member -NotePropertyName OutFile -NotePropertyValue $dllStage -Force
Show-CscResult $r1 '引擎库'

# ---------- 6. 阶段二：主程序 ----------
Say ''
Say '---------------------------------------------------------------'
Say ' 阶段二：编译主程序 -> dist\SimIme.exe（单文件 / 无任务栏图标 / 托盘常驻）'
Say '---------------------------------------------------------------'

if ($appSources.Count -eq 0) {
    Bad ('src\App 下没有任何 .cs 文件：' + $appDir)
    Hint '主程序尚未就绪（等待 B 交付 src\App\**）。'
    Hint ('引擎库已产出（暂存）：' + $dllStage + ' (' + (Get-Item -LiteralPath $dllStage).Length + ' 字节)')
    Hint '本脚本不会用引擎库冒充主程序；请等 App 源码到位后重新运行。'
    Restore-UserData
    Say ''
    Say '构建中止（部分成功），退出码 2'
    exit 2
}

if ($null -ne $assetProblem) {
    Bad $assetProblem
    Hint '主程序必须把拼音表以 SimIme.Assets.* 资源名内嵌（见 src\Engine\PinyinTable.cs / CommonWordIndex.cs 的 ResourceName 常量）；请让 A 交付 src\Assets\*.tsv。'
Restore-UserData
    Say ''
    Say '构建中止（部分成功），退出码 2'
    exit 2
}

$exeSources = @($engSources) + @($appSources)
$extra = New-Object System.Collections.ArrayList
foreach ($ra in $resourceArgs) { [void]$extra.Add($ra) }
$manifest = Join-Path $appDir 'app.manifest'
if (Test-Path -LiteralPath $manifest) { [void]$extra.Add('/win32manifest:"' + $manifest + '"'); Ok ('应用清单：' + $manifest) }
$icon = Join-Path $appDir 'app.ico'
if (Test-Path -LiteralPath $icon) { [void]$extra.Add('/win32icon:"' + $icon + '"'); Ok ('应用图标：' + $icon) }
Say ('主程序源文件数：' + $exeSources.Count + '（Engine ' + $engSources.Count + ' + App ' + $appSources.Count + '）')

$r2 = Invoke-Csc -Label 'app' -Target 'winexe' -OutFile $exeStage -Sources $exeSources -References $refs.ToArray() -Extra $extra.ToArray()
$r2 | Add-Member -NotePropertyName OutFile -NotePropertyValue $exeStage -Force
Show-CscResult $r2 '主程序'

# ---------- 7. 阶段三：管理员启动器 ----------
Say ''
Say '---------------------------------------------------------------'
Say ' 阶段三：编译管理员启动器 -> dist\SimIme-Admin.exe（清单 requireAdministrator）'
Say '---------------------------------------------------------------'
$toolSources = Get-CsSources $toolsDir
if ($toolSources.Count -eq 0) {
    Bad ('src\Tools 下没有任何 .cs 文件：' + $toolsDir)
    Hint '需要 src\Tools\AdminLauncher.cs（负责以管理员身份拉起同目录的 SimIme.exe）。'
Restore-UserData
    Say ''
    Say '构建中止（部分成功），退出码 2'
    exit 2
}
if (-not (Test-Path -LiteralPath $adminManifest)) {
    Bad ('缺少启动器清单：' + $adminManifest)
    Hint '需要一份 requestedExecutionLevel=requireAdministrator 的 win32 清单，否则启动器不会触发 UAC 提权。'
Restore-UserData
    Say ''
    Say '构建中止（部分成功），退出码 2'
    exit 2
}
Ok ('启动器清单：' + $adminManifest + '  (requestedExecutionLevel=requireAdministrator)')
foreach ($s in $toolSources) { Say ('       [Tools]  ' + $s.Substring($root.Length + 1)) }

$slimRefs = New-Object System.Collections.ArrayList
foreach ($n in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) { [void]$slimRefs.Add((Join-Path $fwDir $n)) }
$r3 = Invoke-Csc -Label 'admin' -Target 'winexe' -OutFile $adminStage -Sources $toolSources -References $slimRefs.ToArray() -Extra @('/win32manifest:"' + $adminManifest + '"')
$r3 | Add-Member -NotePropertyName OutFile -NotePropertyValue $adminStage -Force
Show-CscResult $r3 '管理员启动器'

# ---------- 7a. 校验暂存产物：三个都在且非空，才允许动 dist（任何编译失败都在此之前中止） ----------
$stageBad = New-Object System.Collections.ArrayList
foreach ($pair in @(@($dllStage, 'SimIme.Engine.dll'), @($exeStage, 'SimIme.exe'), @($adminStage, 'SimIme-Admin.exe'))) {
    if (-not (Test-Path -LiteralPath $pair[0])) { [void]$stageBad.Add($pair[1] + '(缺失)'); continue }
    if ((Get-Item -LiteralPath $pair[0]).Length -le 0) { [void]$stageBad.Add($pair[1] + '(空文件)') }
}
if ($stageBad.Count -gt 0) {
    Bad ('暂存产物不完整：' + ($stageBad -join ' / '))
    Hint '编译未全部成功，已放弃本次构建。'
    Say ''
    Say '构建中止：dist 未被改动（仍是上一版产物）'
    exit 1
}
Ok ('暂存产物已就绪：' + (Split-Path -Leaf $dllStage) + ' ' + (Get-Item -LiteralPath $dllStage).Length + ' 字节 / ' + (Split-Path -Leaf $exeStage) + ' ' + (Get-Item -LiteralPath $exeStage).Length + ' 字节 / ' + (Split-Path -Leaf $adminStage) + ' ' + (Get-Item -LiteralPath $adminStage).Length + ' 字节')

# ---------- 2b. 预检：动 dist 之前先看 SimIme 是否在跑（杀不掉就提前退出，绝不先删一半） ----------
$running = @(Get-Process -Name SimIme -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Warn ('检测到 ' + $running.Count + ' 个 SimIme 进程，尝试结束它们以便重建')
    foreach ($p in $running) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Milliseconds 900
    $still = @(Get-Process -Name SimIme -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Bad '检测到 SimIme 正在运行且无法结束，已放弃构建'
        Hint '请右键托盘图标退出 SimIme（或用任务管理器结束 SimIme.exe）后重试。'
        Say '       dist 未被改动。'
        Say ''
        Say '构建中止，退出码 3'
        exit 3
    }
    Ok ('已结束 ' + $running.Count + ' 个 SimIme 进程，继续构建')
}
# ---------- 3. 保留用户数据（清理 dist 前先搬出去，构建后原样放回） ----------
$userDataNames = @('config.json', 'habits.dat', 'habits.version', 'habits_merges.tsv', 'dict_extra.tsv')
New-Item -ItemType Directory -Force -Path $objDir | Out-Null
if (Test-Path -LiteralPath $script:stashDir) { Remove-Item -LiteralPath $script:stashDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $script:stashDir | Out-Null
$script:stashedNames = @()
foreach ($n in $userDataNames) {
    $srcU = Join-Path $distDir $n
    if (-not (Test-Path -LiteralPath $srcU)) { continue }
    try {
        Move-Item -LiteralPath $srcU -Destination (Join-Path $script:stashDir $n) -Force
        $script:stashedNames += $n
    } catch {
        Warn ('用户数据搬移失败（将保持原位）：' + $n + ' -> ' + $_.Exception.Message)
    }
}
if ($script:stashedNames.Count -gt 0) {
    Ok ('已保留用户数据（构建后原样放回）：' + ($script:stashedNames -join ' / '))
} else {
    Say '       没有需要保留的用户数据（config.json / habits.dat / habits.version / habits_merges.tsv / dict_extra.tsv 均不存在，首次构建属正常）'
}

# 日志归档：把上一轮的 error.log / selftest.log 搬出 dist，构建后以 .old-<时间戳> 放回（不删日志）
$script:logArchived = New-Object System.Collections.ArrayList
$script:logHoldDir = Join-Path $objDir 'logarchive'
if (Test-Path -LiteralPath $script:logHoldDir) { Remove-Item -LiteralPath $script:logHoldDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $script:logHoldDir | Out-Null
$logStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($ln in @('error.log', 'selftest.log')) {
    $srcL = Join-Path $distDir $ln
    if (-not (Test-Path -LiteralPath $srcL)) { continue }
    try {
        Move-Item -LiteralPath $srcL -Destination (Join-Path $script:logHoldDir $ln) -Force -ErrorAction Stop
        [void]$script:logArchived.Add(@{ Name = $ln; Stamp = $logStamp })
    } catch {
        Warn ('日志搬移失败（将保持原位）：' + $ln + ' -> ' + $_.Exception.Message)
    }
}
# 已有的日志归档也搬出去（否则会被本轮清理删掉，无法积累保留 3 份）
$script:logKept = New-Object System.Collections.ArrayList
foreach ($a in @(Get-ChildItem -LiteralPath $distDir -File -Filter '*.log.old-*' -ErrorAction SilentlyContinue)) {
    try {
        Move-Item -LiteralPath $a.FullName -Destination (Join-Path $script:logHoldDir $a.Name) -Force -ErrorAction Stop
        [void]$script:logKept.Add($a.Name)
    } catch {
        Warn ('旧日志归档搬移失败（可能被删）：' + $a.Name + ' -> ' + $_.Exception.Message)
    }
}
if ($script:logArchived.Count -gt 0) {
    Ok ('已归档上一轮日志（构建后放回 dist，不删除）：' + (($script:logArchived | ForEach-Object { $_.Name }) -join ' / '))
}
# ---------- 3b. 清理 dist（幂等前提） ----------
$skipped = New-Object System.Collections.ArrayList
if (Test-Path -LiteralPath $distDir) {
    foreach ($item in @(Get-ChildItem -LiteralPath $distDir -Force -ErrorAction SilentlyContinue)) {
        try {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
        } catch {
            [void]$skipped.Add($item.Name)
            Warn ('跳过被占用的文件：' + $item.Name)
        }
    }
}
if ($skipped.Count -gt 0) {
    foreach ($name in $skipped) {
        $full = Join-Path $distDir $name
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $base = $name
        $dotOld = $name.IndexOf('.old-')
        if ($dotOld -gt 0) { $base = $name.Substring(0, $dotOld) }
        $newName = $base + '.old-' + $stamp
        $k = 2
        while (Test-Path -LiteralPath (Join-Path $distDir $newName)) { $newName = $base + '.old-' + $stamp + '-' + $k; $k++ }
        if ($newName -eq $name) { Warn ('文件仍被占用（已是最新后缀，保持原名）：' + $name); continue }        try {
            Move-Item -LiteralPath $full -Destination (Join-Path $distDir $newName) -Force -ErrorAction Stop
            Warn ('已把被占用的文件改名移开：' + $name + ' -> ' + $newName + '（下次启动前可删除）')
        } catch {
            Warn ('无法改名移开：' + $name + '（' + $_.Exception.Message + '）——它可能不会被本次构建更新')
        }
    }
}
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
New-Item -ItemType Directory -Force -Path $objDir  | Out-Null
Ok ('输出目录已就绪：' + $distDir)

# ---------- 3c. 把暂存产物搬进 dist（到这一步才真正替换交付产物） ----------
$moved = 0
foreach ($pair in @(@($dllStage, $dllOut), @($exeStage, $exeOut), @($adminStage, $adminOut))) {
    try {
        Move-Item -LiteralPath $pair[0] -Destination $pair[1] -Force -ErrorAction Stop
        $moved++
    } catch {
        Warn ('产物搬入失败：' + (Split-Path -Leaf $pair[1]) + ' -> ' + $_.Exception.Message)
    }
}
Ok ('已搬入产物：' + $moved + '/3')

# 用户数据放回（成功路径）—— 这一步必须存在，否则五件用户数据会留在暂存区
$expectedU = @($script:stashedNames)
Restore-UserData
$lost = New-Object System.Collections.ArrayList
foreach ($n in $expectedU) { if (-not (Test-Path -LiteralPath (Join-Path $distDir $n))) { [void]$lost.Add($n) } }
if ($lost.Count -gt 0) {
    Bad ('用户数据未放回：' + ($lost -join ' / '))
    Hint '它们仍在 _devC\obj\userdata-stash，可手动复制回 dist。'
    Say ''
    Say '构建中止，退出码 5'
    exit 5
}
if ($expectedU.Count -gt 0) { Ok ('用户数据校验通过：' + $expectedU.Count + ' 件都在 dist') }

# 清理 .old- 残留：只保留最近 1 份（dist 是交付目录，不该越积越多）
$olds = @(Get-ChildItem -LiteralPath $distDir -File -Filter 'SimIme.exe.old-*' -ErrorAction SilentlyContinue | Sort-Object Name)
if ($olds.Count -gt 1) {
    foreach ($d in @($olds | Select-Object -First ($olds.Count - 1))) {
        try {
            Remove-Item -LiteralPath $d.FullName -Force -ErrorAction Stop
            Warn ('已删除更早的 .old 残留：' + $d.Name + '（只保留最近 1 份）')
        } catch {
            Warn ('更早的 .old 残留仍被占用，未能删除（下次构建再试）：' + $d.Name)
        }
    }
}

# ---------- 8. 生成可双击脚本（dist） ----------
Say ''
Say '---------------------------------------------------------------'
Say ' 阶段四：生成可双击的 .cmd（dist）'
Say '---------------------------------------------------------------'
$oemCp = [System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage
Say ('       脚本文本编码：OEM 代码页 ' + $oemCp + '（与 cmd.exe 读批处理的默认代码页一致，中文提示不乱码）')

$bartestCmd = @'
@echo off
setlocal
title SimIme 悬浮条外观展示
set "EXE=%~dp0SimIme.exe"
set "LOG=%~dp0selftest.log"
if not exist "%EXE%" goto :missing
echo.
echo [1/3] 正在展示悬浮条外观：--bartest 10
echo       纯展示，不向任何窗口注入字符；屏幕上会出现一个小条，10 秒后自动结束。
echo.
if exist "%LOG%" del "%LOG%" >nul 2>nul
"%EXE%" --bartest 10
echo.
echo [2/3] 展示结束。下面把这 10 秒的详情从 selftest.log 读出来：
echo ------------------------------------------------------------
if not exist "%LOG%" goto :nolog
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:LOG -Encoding UTF8 -Tail 20"
goto :done
:nolog
echo [提示] 未找到 selftest.log -- 程序可能没有正常启动，或被安全软件拦截。
:done
echo ------------------------------------------------------------
echo [3/3] 看完按任意键关闭本窗口。
pause
exit /b 0
:missing
echo [错误] 同目录找不到 SimIme.exe
echo        请把本脚本与 SimIme.exe 放在同一个文件夹里再运行。
echo.
pause
exit /b 2
'@

$permCmd = @'
@echo off
setlocal
title SimIme 权限自检
set "EXE=%~dp0SimIme.exe"
set "LOG=%~dp0selftest.log"
if not exist "%EXE%" goto :missing
echo.
echo [1/3] 运行权限自检：--iltest
echo       只读诊断，不注入任何字符；比对本程序与前台窗口的权限级别并给出结论。
echo.
if exist "%LOG%" del "%LOG%" >nul 2>nul
"%EXE%" --iltest
echo.
echo [2/3] 自检结论，读自 selftest.log：
echo ------------------------------------------------------------
if not exist "%LOG%" goto :nolog
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:LOG -Encoding UTF8 -Tail 20"
goto :done
:nolog
echo [提示] 未找到 selftest.log -- 程序可能没有正常启动，或被安全软件拦截。
:done
echo ------------------------------------------------------------
echo [3/3] 看完按任意键关闭本窗口。
pause
exit /b 0
:missing
echo [错误] 同目录找不到 SimIme.exe
echo        请把本脚本与 SimIme.exe 放在同一个文件夹里再运行。
echo.
pause
exit /b 2
'@

$barAllCmd = @'
@echo off
setlocal
title SimIme 悬浮条排障样例
set "EXE=%~dp0SimIme.exe"
set "LOG=%~dp0selftest.log"
if not exist "%EXE%" goto :missing
echo.
echo [1/3] 正在展示悬浮条排障样例：--bartest-all 10
echo       纯展示，不向任何窗口注入字符；这批样例只有 BarVerbose=true 排障模式才会出现，
echo       默认运行不会出现（它们平时走托盘气泡 + 主界面状态区 + 日志）。
echo.
if exist "%LOG%" del "%LOG%" >nul 2>nul
"%EXE%" --bartest-all 10
echo.
echo [2/3] 展示结束。下面把这 10 秒的详情从 selftest.log 读出来：
echo ------------------------------------------------------------
if not exist "%LOG%" goto :nolog
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:LOG -Encoding UTF8 -Tail 20"
goto :done
:nolog
echo [提示] 未找到 selftest.log -- 程序可能没有正常启动，或被安全软件拦截。
:done
echo ------------------------------------------------------------
echo [3/3] 看完按任意键关闭本窗口。
pause
exit /b 0
:missing
echo [错误] 同目录找不到 SimIme.exe
echo        请把本脚本与 SimIme.exe 放在同一个文件夹里再运行。
echo.
pause
exit /b 2
'@

$verifyCmd = @'
@echo off
setlocal
title SimIme 落屏自检
set "EXE=%~dp0SimIme.exe"
set "LOG=%~dp0selftest.log"
if not exist "%EXE%" goto :missing
echo.
echo ============================================================
echo  SimIme 落屏自检（--verify）：会真的往目标窗口打字，然后回读比对
echo ============================================================
echo.
echo  请在这 6 秒内用鼠标点一下你要测试的窗口（例如记事本），
echo  让它成为最前面的窗口，并把光标停在能输入的位置。
echo.
timeout /t 6 /nobreak >nul 2>nul
if errorlevel 1 ping -n 7 127.0.0.1 >nul
if exist "%LOG%" del "%LOG%" >nul 2>nul

rem 依次尝试三个标题片段（先中文后英文），仅在「没找到窗口」时换下一个
set "RC=3"
set "USED="
call :try "记事本"
if "%RC%"=="3" call :try "Notepad"
if "%RC%"=="3" call :try "文本"
if "%RC%"=="3" goto :notfound
goto :show

:try
set "USED=%~1"
echo.
echo  尝试标题片段：%USED%
echo ------------------------------------------------------------
"%EXE%" --verify "%USED%"
set "RC=%ERRORLEVEL%"
echo  ——本次使用标题片段=%USED%，退出码=%RC%
exit /b 0

:notfound
echo.
echo  三个候选标题片段都没找到可见窗口（记事本 / Notepad / 文本）。
echo  请先打开记事本；或者按 README 4.1 手敲，换成你自己窗口的标题片段：
echo      SimIme.exe --verify "你的窗口标题片段"
echo  怎么查标题：把鼠标悬停在任务栏的窗口图标上，气泡里显示的就是窗口标题
echo  （可能是英文，例如 无标题 - Notepad）。
echo.

:show
echo ------------------------------------------------------------
echo  退出码=%RC%
echo    0 = 落屏与原文一致（通过）
echo    2 = 目标不是当前前台（脚本不会换片段重试；请重跑并在倒计时里点中目标窗口）
echo    3 = 三个候选片段都没找到可见窗口（请先打开记事本，或按 4.1 换标题片段）
echo    4 = 缺参数
echo    5 = 落屏与原文不一致（请看下面日志里的首个不同位置）
echo    6 = 完整性护栏命中（我方权限低于目标，按键会被系统丢弃；请用 SimIme-Admin.exe 运行）
echo    9 = 异常
echo.
echo  本次详情（读自 selftest.log，UTF-8）：
echo ------------------------------------------------------------
if not exist "%LOG%" goto :nolog
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:LOG -Encoding UTF8 -Tail 20"
goto :done
:nolog
echo [提示] 未找到 selftest.log -- 程序可能没有正常启动，或被安全软件拦截。
:done
echo ------------------------------------------------------------
echo  核对字符一致性请以上面这段 selftest.log 为准（控制台可能把 emoji 显示成 ????）。
echo.
echo  看完按任意键关闭本窗口。
pause
exit /b %RC%

:missing
echo [错误] 同目录找不到 SimIme.exe
echo        请把本脚本与 SimIme.exe 放在同一个文件夹里再运行。
echo.
pause
exit /b 2
'@

$updateDictCmd = @'
@echo off
setlocal
title SimIme 更新词典
if /I "%~1"=="--worker" goto :worker

set "ROOT=%~dp0.."
set "DIST=%~dp0"
echo.
echo ============================================================
echo  SimIme 更新词典：联网拉取 jieba 词表，然后重新构建 exe
echo ============================================================
echo.
echo  注意：词表是内嵌进 exe 的，所以拉完词表必须重新构建才会生效。
echo.

rem ---------- 1) 位置检查 ----------
if not exist "%ROOT%\tools\fetch_dict.ps1" goto :badplace
if not exist "%ROOT%\build.ps1" goto :badplace
if not exist "%ROOT%\src\Assets" goto :badplace
echo  [1/5] 位置检查：OK。上级目录 = %ROOT%

rem ---------- 2) pwsh 检查（后面用它拉表、构建、以及检测是否有 SimIme 在跑） ----------
where pwsh >nul 2>nul
if errorlevel 1 goto :nopwsh
echo  [2/5] 环境检查：pwsh 可用

rem ---------- 3) 是否已有 SimIme 在运行 ----------
rem 说明：不用 tasklist（在受限环境里可能枚举不到进程），改用 Get-Process，更可靠。
pwsh -NoProfile -Command "if (Get-Process -Name SimIme -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"
if errorlevel 1 goto :running
echo  [3/5] 运行检查：没有 SimIme 在运行，可以安全重建

rem ---------- 4) 因为重建会清空 dist，而本脚本就在 dist 里，先把自身复制到 %%TEMP%% 再执行 ----------
if "%TEMP%"=="" goto :notemp
echo  [4/5] 准备执行环境：把本脚本复制到 %%TEMP%% 后再重建，避免重建清空 dist 时把自己删掉
copy /Y "%~f0" "%TEMP%\SimIme-update-dict-worker.cmd" >nul
call "%TEMP%\SimIme-update-dict-worker.cmd" --worker "%ROOT%" "%DIST%"
set "RC=%ERRORLEVEL%"
del "%TEMP%\SimIme-update-dict-worker.cmd" >nul 2>nul
echo.
if not "%RC%"=="0" echo  状态说明：见上面的错误行，那里写明了词表与 exe 各自是否已被改动。
if "%RC%"=="0" echo  已生效：下次启动即使用新词典。
echo.
echo  看完按任意键关闭本窗口。
pause
exit /b %RC%

:worker
rem ---------- 以下在 %%TEMP%% 副本里执行：可以安全重建 dist ----------
set "ROOT=%~2"
set "DIST=%~3"
echo  [5/5] 拉取词表：tools\fetch_dict.ps1
echo ------------------------------------------------------------
pwsh -NoProfile -File "%ROOT%\tools\fetch_dict.ps1"
set "FRC=%ERRORLEVEL%"
if not "%FRC%"=="0" goto :fetchfail
echo ------------------------------------------------------------
echo  词表已更新，开始重建：build.ps1
echo ------------------------------------------------------------
pwsh -NoProfile -File "%ROOT%\build.ps1"
set "BRC=%ERRORLEVEL%"
if not "%BRC%"=="0" goto :buildfail
echo ------------------------------------------------------------
echo  结果：
pwsh -NoProfile -Command "$f='%ROOT%\src\Assets\common_words.tsv'; $c=(Get-Content -LiteralPath $f -ReadCount 0).Count; '   新版词表：' + $c + ' 行 / ' + (Get-Item -LiteralPath $f).Length + ' 字节'"
pwsh -NoProfile -Command "$e='%DIST%SimIme.exe'; if (Test-Path -LiteralPath $e) { $i=Get-Item -LiteralPath $e; '   新版 SimIme.exe：' + $i.Length + ' 字节  SHA256=' + (Get-FileHash -LiteralPath $e -Algorithm SHA256).Hash } else { '   未找到 SimIme.exe，构建输出异常' }"
exit /b 0

:badplace
echo.
echo [错误] 本脚本必须放在**项目目录的 dist 里**运行。
echo        要求同时存在：..\tools\fetch_dict.ps1、..\build.ps1、..\src\Assets
echo        实际检查的上级目录：%ROOT%
echo        状态说明：词表未改动、程序未重建，什么都没发生。
echo.
pause
exit /b 2

:running
echo.
echo [错误] 检测到 SimIme.exe 正在运行。
echo        请先退出 SimIme（托盘右键「退出 SimIme」，或用任务管理器结束 SimIme.exe），
echo        否则重新构建会失败（exe 被占用、dist 清不掉）。
echo        状态说明：词表未改动、程序未重建，什么都没发生。
echo.
pause
exit /b 3

:nopwsh
echo.
echo [错误] 没有找到 pwsh（PowerShell 7），本脚本用它来拉词表和构建。
echo        请安装 PowerShell 7 后重试；或手动执行：
echo          pwsh -File ..\tools\fetch_dict.ps1
echo          pwsh -File ..\build.ps1
echo        状态说明：词表未改动、程序未重建，什么都没发生。
echo.
pause
exit /b 6

:notemp
echo.
echo [错误] 环境变量 TEMP 为空，无法把脚本临时复制出去再重建。
echo        状态说明：词表未改动、程序未重建，什么都没发生。
echo.
pause
exit /b 7

:fetchfail
echo.
echo [错误] 词表未更新（原因见上），现有词表与程序未被修改。
echo        状态说明：src\Assets 里的词表还是旧的，exe 也没有重建。
echo        常见原因：HTTP 镜像不可达、网络被拦、tools\fetch_dict.ps1 自身报错。
echo.
pause
exit /b 4

:buildfail
echo.
echo [错误] 词表已更新但重建失败，请把上面的输出发给开发者。
echo        状态说明：src\Assets\common_words.tsv 已经是**新词表**，
echo                  但 dist 里的 exe 没有成功重建，可能仍是旧的、也可能已被清掉。
echo                  先解决构建报错，再重跑本脚本即可。
echo.
pause
exit /b 5
'@

$wordsCmd = @'
@echo off
setlocal
title SimIme 用户词表
set "EXE=%~dp0SimIme.exe"
set "LOG=%~dp0selftest.log"
if not exist "%EXE%" goto :missing
echo.
echo ============================================================
echo  SimIme 用户词表（自学习）查看器
echo ============================================================
echo.
echo  作用：查看程序从你的输入习惯里学到的词组。
echo        它把你每次输入的原文里 2~4 字的连续片段计数，达到阈值的词组
echo        以后会优先整块输入（例如常打「天上有」之后，不再切成 天上/有）。
echo  重置：删除 exe 同目录的 habits.dat 即可清空学习（下次运行重新累积）。
echo.
echo  正在读取：SimIme.exe --words
echo ------------------------------------------------------------
if exist "%LOG%" del "%LOG%" >nul 2>nul
"%EXE%" --words
set "RC=%ERRORLEVEL%"
echo.
echo  读取结果（读自 selftest.log，UTF-8）：
echo ------------------------------------------------------------
if not exist "%LOG%" goto :nolog
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:LOG -Encoding UTF8 -Tail 30"
goto :done
:nolog
echo [提示] 未找到 selftest.log -- 程序可能没有正常启动，或被安全软件拦截。
:done
echo ------------------------------------------------------------
echo  看完按任意键关闭本窗口。
pause
exit /b %RC%
:missing
echo [错误] 同目录找不到 SimIme.exe
echo        请把本脚本与 SimIme.exe 放在同一个文件夹里再运行。
echo.
pause
exit /b 2
'@

$cmdFiles = @(
    @{ Name = 'bartest.cmd';        Text = $bartestCmd },
    @{ Name = '看悬浮条外观.cmd';   Text = $bartestCmd },
    @{ Name = '看排障样例.cmd';     Text = $barAllCmd },
    @{ Name = '权限自检.cmd';       Text = $permCmd },
    @{ Name = '自检落屏.cmd';       Text = $verifyCmd },
    @{ Name = 'verify-ime.cmd';     Text = $verifyCmd },
    @{ Name = '看用户词表.cmd';     Text = $wordsCmd },
    @{ Name = 'words.cmd';          Text = $wordsCmd },
    @{ Name = '更新词典.cmd';       Text = $updateDictCmd },
    @{ Name = 'update-dict.cmd';    Text = $updateDictCmd }
)
foreach ($cf in $cmdFiles) {
    $p = Join-Path $distDir $cf.Name
    $body = ($cf.Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    try {
        [System.IO.File]::WriteAllBytes($p, [System.Text.Encoding]::GetEncoding($oemCp).GetBytes($body))
        Ok ('可双击脚本：' + $p + ' (' + (Get-Item -LiteralPath $p).Length + ' 字节，双击即用)')
    } catch {
        Warn ('脚本写入失败（被占用？）：' + $cf.Name + ' -> ' + $_.Exception.Message)
    }
}

# ---------- 8b. 收尾断言：dist 里必须有主程序与引擎库（兜住任何把它们弄丢的路径） ----------
$stale = New-Object System.Collections.ArrayList
foreach ($need in @(@($exeOut, 'SimIme.exe'), @($dllOut, 'SimIme.Engine.dll'), @($adminOut, 'SimIme-Admin.exe'))) {
    if (-not (Test-Path -LiteralPath $need[0] -PathType Leaf)) { [void]$stale.Add($need[1] + '(缺失)'); continue }
    if ((Get-Item -LiteralPath $need[0]).Length -le 0) { [void]$stale.Add($need[1] + '(空文件)'); continue }
    if ((Get-Item -LiteralPath $need[0]).LastWriteTime -lt $buildStart) { [void]$stale.Add($need[1] + '(未更新)') }
}
if ($stale.Count -gt 0) {
    Restore-UserData
    Bad ('收尾断言失败：dist 产物不合格 —— ' + ($stale -join ' / '))
    Hint 'dist\SimIme.exe 必须存在、非空、且被本次构建更新过；这条断言用于兜住任何把交付产物弄丢的路径。'
    Hint '紧急恢复：把上一版产物复制回 dist（本脚本的暂存目录 _devC\obj\stage 与备份 _devC\backup 都可能有）。'
    Say ''
    Say '构建中止，退出码 4'
    exit 4
}
Ok '收尾断言通过：dist 里主程序 / 引擎库 / 启动器都在，且都是本次构建的产物'


# ---------- 9. 汇总 ----------
Say ''
Say '==============================================================='
Say ' 构建成功'
Say '==============================================================='
Say ' 产物清单：'
$a1 = Get-ArtifactInfo $dllOut
$a2 = Get-ArtifactInfo $exeOut
$a3 = Get-ArtifactInfo $adminOut
Say $a1.Text
Say $a2.Text
Say $a3.Text
Say ''
Say (' 引擎库   SHA256 : ' + $a1.Hash)
Say (' 主程序   SHA256 : ' + $a2.Hash)
Say (' 管理员启动器 SHA256 : ' + $a3.Hash)
Say ''
Say ' 可双击脚本（dist 目录，双击即可）：'
foreach ($n in @('bartest.cmd', '看悬浮条外观.cmd', '看排障样例.cmd', '权限自检.cmd', '自检落屏.cmd', 'verify-ime.cmd', '看用户词表.cmd', 'words.cmd', '更新词典.cmd', 'update-dict.cmd')) {
    $cp = Join-Path $distDir $n
    if (Test-Path -LiteralPath $cp) { Say ('       ' + $cp + '  (' + (Get-Item -LiteralPath $cp).Length + ' 字节)') }
    else { Warn ('缺少可双击脚本：' + $cp) }
}
Say ' 下一步：'
Say '   pwsh -NoProfile -File tests\engine_tests.ps1     # 引擎外部断言'
Say '   pwsh -NoProfile -File tests\static_checks.ps1    # 静态机械检查'
Say '   dist\SimIme.exe --selftest                        # 程序内自检（见 README）'
Say '   dist\SimIme-Admin.exe                             # 需要注入管理员窗口时用（会弹一次 UAC）'
Say '   dist\bartest.cmd / dist\看悬浮条外观.cmd            # 双击看悬浮条外观（--bartest 10）'
Say '   dist\权限自检.cmd                                  # 双击跑权限自检（--iltest）'
Say '   dist\看排障样例.cmd                                # 双击看 BarVerbose 排障样例（--bartest-all 10）'
Say '   dist\自检落屏.cmd / dist\verify-ime.cmd             # 双击跑落屏自检（含 6 秒倒计时 + --verify "记事本"）'
Say '   dist\看用户词表.cmd / dist\words.cmd                # 双击查看自学习到的用户词组（--words）'
Say '   dist\更新词典.cmd / dist\update-dict.cmd           # 双击联网更新词表并重建（需在项目目录内、无 SimIme 在跑）'
Say '==============================================================='
exit 0
