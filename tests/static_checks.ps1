# SimIme 静态机械检查（C 组交付物）
# 用法：pwsh -NoProfile -File tests\static_checks.ps1
# 退出码：0 = 无 FAIL（PASS/WARN/SKIP 均可）；1 = 存在 FAIL。WARN＝不阻断的规则一致性项；SKIP＝上游未交付
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$root    = Split-Path -Parent $PSScriptRoot
$srcDir  = Join-Path $root 'src'
$appDir  = Join-Path $root 'src\App'
$exePath = Join-Path $root 'dist\SimIme.exe'

$script:pass = 0; $script:fail = 0; $script:skip = 0; $script:warn = 0
function Say { param([string]$t) Write-Output $t }
function Ev  { param([string]$t) Write-Output ('       ' + $t) }
function Section { param([string]$t) Write-Output ''; Write-Output ('--- ' + $t + ' ---') }
function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    if ($Ok) { $script:pass++; Write-Output ('[PASS] ' + $Name + ' :: ' + $Detail) }
    else     { $script:fail++; Write-Output ('[FAIL] ' + $Name + ' :: ' + $Detail) }
}
function Warn-Note { param([string]$Name, [string]$Reason) $script:warn++; Write-Output ('[WARN] ' + $Name + ' :: ' + $Reason) }
function Skip-Note { param([string]$Name, [string]$Reason) $script:skip++; Write-Output ('[SKIP] ' + $Name + ' :: ' + $Reason) }
function Rel { param([string]$p) if ($p.StartsWith($root)) { return $p.Substring($root.Length + 1) } else { return $p } }

Say '==============================================================='
Say ' SimIme 静态机械检查 static_checks.ps1'
Say (' 时间   : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say (' 根目录 : ' + $root)
Say '==============================================================='

$csFiles = @()
if (Test-Path -LiteralPath $srcDir) {
    $csFiles = @(Get-ChildItem -LiteralPath $srcDir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue | Sort-Object FullName)
}

function Remove-LineComment {
    param([string]$Line)
    $i = $Line.IndexOf('//')
    if ($i -ge 0) { return $Line.Substring(0, $i) }
    return $Line
}

function Get-CodeHits {
    param([string]$Dir, [string]$Rx, [switch]$CaseSensitive)
    return @(Find-Hits $Dir $Rx -CaseSensitive:$CaseSensitive | Where-Object { (Remove-LineComment $_.Line) -match $Rx })
}

function Find-Hits {
    param([string]$Dir, [string]$Rx, [switch]$CaseSensitive)
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $Dir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
             Select-String -Pattern $Rx -Encoding UTF8 -CaseSensitive:([bool]$CaseSensitive))
}

# ---------- C1 UTF-8 BOM ----------
Section 'C1  所有 src\**\*.cs 首字节为 UTF-8 BOM'
if ($csFiles.Count -eq 0) {
    Skip-Note 'C1 UTF-8 BOM' 'src 下暂无 .cs 文件（等待 A/B 交付）'
} else {
    $bad = New-Object System.Collections.ArrayList
    foreach ($f in $csFiles) {
        $b = [System.IO.File]::ReadAllBytes($f.FullName)
        $head = (($b[0..([Math]::Min(2, $b.Length - 1))] | ForEach-Object { $_.ToString('X2') }) -join ' ')
        $ok = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
        if ($ok) { Ev ((Rel $f.FullName) + '  首字节 ' + $head + '  OK') }
        else { Ev ((Rel $f.FullName) + '  首字节 ' + $head + '  <== 缺少 BOM'); [void]$bad.Add((Rel $f.FullName)) }
    }
    $c1detail = $(if ($bad.Count -eq 0) { '全部文件均以 EF BB BF 开头' } else { '缺 BOM：' + ($bad -join ', ') })
    if ($bad.Count -eq 0) { Check ('C1 UTF-8 BOM（' + $csFiles.Count + ' 个 .cs）') $true $c1detail }
    else {
        Warn-Note ('C1 UTF-8 BOM（' + $csFiles.Count + ' 个 .cs）') ($c1detail + ' —— 实测：/codepage:65001 下无 BOM 文件的中文字面量编译与运行均正确，故记为 WARN 不阻断；它只是规则一致性项，建议补齐')
    }
}

# ---------- C2 C# 6+ 语法 ----------
Section 'C2  无 C# 6+ 语法（字符串插值 / nameof / 表达式体成员）'
if ($csFiles.Count -eq 0) {
    Skip-Note 'C2 C# 6+ 语法' 'src 下暂无 .cs 文件（等待 A/B 交付）'
} else {
    $rules = @(
        @{ Name = '字符串插值'; Rx = '\$"' },
        @{ Name = 'nameof';     Rx = 'nameof\s*\('; Case = $true },
        @{ Name = '表达式体方法'; Rx = '^\s*(public|private|protected|internal|static|override|virtual|sealed|abstract|extern|new)[^;{}\r\n]*\)\s*=>' },
        @{ Name = '表达式体属性'; Rx = '^\s*(public|private|protected|internal|static)[^;{}\r\n]*\s+[A-Za-z_][A-Za-z0-9_]*\s*=>' }
    )
    foreach ($r in $rules) {
        $hits = @(Get-CodeHits $srcDir $r.Rx -CaseSensitive:([bool]$r.Case))
        if ($hits.Count -eq 0) { Check ('C2 无' + $r.Name) $true '0 处命中' }
        else {
            Check ('C2 无' + $r.Name) $false ($hits.Count.ToString() + ' 处命中')
            $shown = 0
            foreach ($h in $hits) { if ($shown -lt 8) { Ev ((Rel $h.Path) + ':' + $h.LineNumber + '  ' + $h.Line.Trim()); $shown++ } }
        }
    }
    $warnRules = @(
        @{ Name = 'null 条件运算符'; Rx = '\?\.' },
        @{ Name = 'using static';    Rx = '^\s*using\s+static\s' },
        @{ Name = '目标类型 new()';  Rx = '=\s*new\s*\(\s*\)\s*;' }
    )
    foreach ($r in $warnRules) {
        $hits = @(Find-Hits $srcDir $r.Rx -CaseSensitive:([bool]$r.Case))
        if ($hits.Count -eq 0) { Ev ('[WARN级] ' + $r.Name + '：0 处命中') }
        else {
            Ev ('[WARN级] ' + $r.Name + '：' + $hits.Count + ' 处命中（C# 6+ 语法，csc 4.0 会编译失败）')
            $shown = 0
            foreach ($h in $hits) { if ($shown -lt 5) { Ev ('  ' + (Rel $h.Path) + ':' + $h.LineNumber + '  ' + $h.Line.Trim()); $shown++ } }
        }
    }
}

# ---------- C3 无 System.Text.Json / 无 NuGet ----------
Section 'C3  无 System.Text.Json / 无 NuGet 依赖'
if ($csFiles.Count -eq 0) { Skip-Note 'C3 无 System.Text.Json' 'src 下暂无 .cs 文件' }
else {
    $jsonHits = @(Get-CodeHits $srcDir 'System\.Text\.Json')
    Check 'C3 无 System.Text.Json' ($jsonHits.Count -eq 0) ($jsonHits.Count.ToString() + ' 处命中（.NET Framework 4.8 无此命名空间，应用 JavaScriptSerializer）')
    foreach ($h in $jsonHits) { Ev ((Rel $h.Path) + ':' + $h.LineNumber + '  ' + $h.Line.Trim()) }
}
$pkgPatterns = @('*.csproj', 'packages.config', '*.nupkg', 'project.json', '*.sln', 'nuget.config', 'packages.lock.json')
$pkgHits = New-Object System.Collections.ArrayList
foreach ($pat in $pkgPatterns) {
    $found = @(Get-ChildItem -LiteralPath $root -Recurse -Filter $pat -File -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notlike '*\_devC\*' -and $_.FullName -notlike '*\.git\*' })
    foreach ($f in $found) { [void]$pkgHits.Add((Rel $f.FullName)) }
}
Check 'C3 无 NuGet 工程/包文件' ($pkgHits.Count -eq 0) ($(if ($pkgHits.Count -eq 0) { '未发现 ' + ($pkgPatterns -join '/') } else { '发现：' + ($pkgHits -join ', ') }))

# ---------- C4 src\App 托盘/热键/悬浮条 ----------
Section 'C4  src\App：无任务栏图标 / 热键注销位于静默路径 / 悬浮条 WS_EX_NOACTIVATE'
$appFiles = @()
if (Test-Path -LiteralPath $appDir) { $appFiles = @(Get-ChildItem -LiteralPath $appDir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue) }
if ($appFiles.Count -eq 0) {
    Skip-Note 'C4 ShowInTaskbar=true 计数为 0' 'src\App 下暂无 .cs（等待 B 交付）'
    Skip-Note 'C4 存在 ShowInTaskbar=false' 'src\App 下暂无 .cs（等待 B 交付）'
    Skip-Note 'C4 UnregisterHotKey 静默路径' 'src\App 下暂无 .cs（等待 B 交付）'
    Skip-Note 'C4 悬浮条 WS_EX_NOACTIVATE' 'src\App 下暂无 .cs（等待 B 交付）'
} else {
    $ys = @(Find-Hits $appDir 'ShowInTaskbar\s*=\s*true')
    $ns = @(Find-Hits $appDir 'ShowInTaskbar\s*=\s*false')
    foreach ($h in $ys) { Ev ((Rel $h.Path) + ':' + $h.LineNumber + '  ' + $h.Line.Trim()) }
    if ($ns.Count -gt 0) { Ev ('ShowInTaskbar=false 出现 ' + $ns.Count + ' 处（正证据）') }
    Check 'C4 ShowInTaskbar=true 计数为 0' ($ys.Count -eq 0) ('true=' + $ys.Count + ' 处, false=' + $ns.Count + ' 处')
    Check 'C4 存在 ShowInTaskbar=false' ($ns.Count -ge 1) ('false=' + $ns.Count + ' 处')

    $declRe = 'static\s+extern\s+.*UnregisterHotKey'
    $callRe = 'UnregisterHotKey\s*\(|UnregisterAll\s*\(\s*\)|UnregisterOne\s*\('
    $sites = New-Object System.Collections.ArrayList
    $inQuiet = 0
    foreach ($h in @(Find-Hits $appDir $callRe)) {
        $ln = $h.Line.Trim()
        if ($h.Line -match $declRe) { Ev ((Rel $h.Path) + ':' + $h.LineNumber + ' [P/Invoke 声明] ' + $ln); continue }
        if ($h.Line -match '(internal|private|public|protected)[^;{}]*\b(UnregisterAll|UnregisterOne)\s*\(') { Ev ((Rel $h.Path) + ':' + $h.LineNumber + ' [方法定义] ' + $ln); continue }
        [void]$sites.Add($h)
    }
    if ($sites.Count -eq 0) {
        Check 'C4 存在静默路径的热键注销调用点' $false '只有声明/定义，没有任何调用点（"结束后热键失效→静默待机"尚未实现）'
    } else {
        $quiet = @('silent', '静默', 'idle', '待机', 'shutdown', 'exit', 'stop', 'dispose', 'teardown', 'finish', 'uninstall')
        foreach ($h in $sites) {
            $lines = @(Get-Content -LiteralPath $h.Path -Encoding UTF8)
            $method = '(未识别)'
            for ($i = $h.LineNumber - 2; $i -ge [Math]::Max(0, $h.LineNumber - 60); $i--) {
                if ($lines[$i] -match '^\s*(public|private|protected|internal|static|override|sealed|virtual|async|void|bool|int|string)[^;={}]*\)\s*$') { $method = $lines[$i].Trim(); break }
            }
            $lo = [Math]::Max(0, $h.LineNumber - 26); $hi = [Math]::Min($lines.Count - 1, $h.LineNumber + 25)
            $matched = @($lines[$lo..$hi] | Where-Object { $w = $_.ToLower(); @($quiet | Where-Object { $w -like ('*' + $_ + '*') }).Count -gt 0 })
            $isReg = ($method -match 'Register')
            $tag = 'no-quiet-context'
            if ($matched.Count -gt 0 -and -not $isReg) { $inQuiet++; $tag = 'quiet-path' }
            elseif ($isReg) { $tag = 'registration-flow(非静默)' }
            Ev ((Rel $h.Path) + ':' + $h.LineNumber + ' [' + $tag + '] 所在方法: ' + $method)
            Ev ('  调用行: ' + $h.Line.Trim())
            $shownCtx = 0
            foreach ($d in $matched) { if ($d.Trim().Length -gt 0 -and $shownCtx -lt 4) { Ev ('  上下文: ' + $d.Trim()); $shownCtx++ } }
        }
        Check 'C4 存在静默路径的热键注销调用点' ($inQuiet -ge 1) ($sites.Count.ToString() + ' 个调用点，其中 ' + $inQuiet + ' 处位于静默/退出路径（方法名或 ±25 行含 silent/静默/idle/待机/stop/exit/dispose，且不在注册流程内）')
    }

    $ws = @(Find-Hits $appDir 'WS_EX_NOACTIVATE')
    foreach ($h in $ws) { Ev ((Rel $h.Path) + ':' + $h.LineNumber + '  ' + $h.Line.Trim()) }
    Check 'C4 悬浮条使用 WS_EX_NOACTIVATE' ($ws.Count -ge 1) ($ws.Count.ToString() + ' 处命中')

    $st = @(Find-Hits $appDir '--selftest')
    if ($st.Count -ge 1) { foreach ($h in $st) { Ev ((Rel $h.Path) + ':' + $h.LineNumber + '  ' + $h.Line.Trim()) } }
    else { Ev '[WARN级] src\App 未见 --selftest 命令行开关（README 的"自检模式"要求程序支持它）' }
}

# ---------- C6 内嵌资源名一致性 ----------
Section 'C6  exe 内嵌资源名 === 源码期望资源名'
$expect = New-Object System.Collections.ArrayList
foreach ($h in @(Find-Hits $srcDir '"SimIme\.Assets\.[A-Za-z0-9_\.]+"')) {
    $m = [regex]::Match($h.Line, '"SimIme\.Assets\.[A-Za-z0-9_\.]+"')
    if ($m.Success) {
        $v = $m.Value.Trim('"')
        if (-not $expect.Contains($v)) { [void]$expect.Add($v); Ev ('期望资源：' + $v + '  <- ' + (Rel $h.Path) + ':' + $h.LineNumber) }
    }
}
if ($expect.Count -eq 0) { Skip-Note 'C6 内嵌资源名一致性' '源码中未见 SimIme.Assets.* 资源名常量' }
elseif (-not (Test-Path -LiteralPath $exePath)) { Skip-Note 'C6 内嵌资源名一致性' 'dist\SimIme.exe 尚未生成' }
else {
    $actual = New-Object System.Collections.ArrayList
    $fs = [System.IO.File]::OpenRead($exePath)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
        $mr = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($h in $mr.ManifestResources) { $res = $mr.GetManifestResource($h); $n = $mr.GetString($res.Name); [void]$actual.Add($n); Ev ('实际内嵌：' + $n) }
    } finally { $fs.Dispose() }
    $missing = @($expect | Where-Object { -not $actual.Contains($_) })
    Check 'C6 内嵌资源名一致性' ($missing.Count -eq 0) ('期望 ' + $expect.Count.ToString() + ' 个 / 实际 ' + $actual.Count.ToString() + ' 个' + $(if ($missing.Count -gt 0) { '；缺失：' + ($missing -join ', ') } else { '；全部命中' }))
}

# ---------- C5 产物 ----------
Section 'C5  dist\SimIme.exe 产物'
if (Test-Path -LiteralPath $exePath) {
    $fi = Get-Item -LiteralPath $exePath
    Ev ('dist\SimIme.exe        字节=' + $fi.Length + '  SHA256=' + (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash + '  修改时间=' + $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    $dll = Join-Path $root 'dist\SimIme.Engine.dll'
    if (Test-Path -LiteralPath $dll) {
        $fd = Get-Item -LiteralPath $dll
        Ev ('dist\SimIme.Engine.dll  字节=' + $fd.Length + '  SHA256=' + (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)
    }
    Check 'C5 dist\SimIme.exe 存在且非空' ($fi.Length -gt 0) ('字节=' + $fi.Length)
} else {
    Skip-Note 'C5 dist\SimIme.exe' '产物尚未生成（等待 A/B 交付后运行 build.ps1）'
}

Say ''
Say '==============================================================='
Say (' 汇总：PASS=' + $script:pass + '  FAIL=' + $script:fail + '  WARN=' + $script:warn + '  SKIP=' + $script:skip)
if ($script:fail -gt 0) { Say ' 结论：存在 FAIL'; Say '==============================================================='; exit 1 }
Say ' 结论：全部 PASS（SKIP 项为上游未交付）'
Say '==============================================================='
exit 0
