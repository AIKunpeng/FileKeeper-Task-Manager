# FileKeeper 远程诊断脚本（只读，不修改任何配置与数据）
# 用法：把整个文件拷到服务器任意目录，管理员 PowerShell 运行：
#       powershell -ExecutionPolicy Bypass -File .\diagnose.ps1
# 输出：控制台 + 同目录下的 FileKeeper-Diagnose-<日期>.txt，把 txt 全文发回来即可。

$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot ('FileKeeper-Diagnose-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')

function W($text) { $text | Tee-Object -FilePath $out -Append }

W "===== FileKeeper 诊断报告  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ====="
W "主机: $env:COMPUTERNAME   系统: $([System.Environment]::OSVersion.VersionString)"
W "上次开机: $((Get-CimInstance Win32_OperatingSystem).LastBootUpTime)"
W ""

W "===== 1. 服务与进程 ====="
Get-Service FileKeeperService -ErrorAction SilentlyContinue |
    Format-List Name, Status, StartType | Out-String | W
Get-Process FileKeeper.Service, FileKeeper.App -ErrorAction SilentlyContinue |
    Format-Table Name, Id, CPU, StartTime -AutoSize | Out-String | W
W ""

W "===== 2. 配置文件 ====="
$cfgPath = "$env:ProgramData\FileKeeper\config.json"
$cfg = $null
if (Test-Path $cfgPath) {
    W "配置文件: $cfgPath（修改时间 $((Get-Item $cfgPath).LastWriteTime)）"
    $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    W "日志目录: $($cfg.settings.logDirectory)"
    W "日志保留天数: $($cfg.settings.logRetentionDays)"

    W "--- 压缩任务 ---"
    foreach ($t in $cfg.compressionTasks) {
        W ("  [{0}] {1}  启用={2}  源={3}  输出={4}  格式={5}  级别={6}  密码={7}  分卷={8}  压后删源={9}  调度={10} {11}" -f `
            $t.id, $t.name, $t.enabled, $t.sourceDirectory, $t.outputDirectory, $t.format, $t.compressionLevel,`
            ($null -ne $t.password), $t.volumeSizeBytes, $t.deleteSourceAfterCompress, $t.schedule.frequency, $t.schedule.timeOfDay)
    }
    W "--- 同步任务 ---"
    foreach ($t in $cfg.syncTasks) {
        W ("  [{0}] {1}  启用={2}  本地={3}  协议={4}  主机={5}:{6}  远程目录={7}  认证={8}  传后删源={9}  调度={10} {11}" -f `
            $t.id, $t.name, $t.enabled, $t.sourceDirectory, $t.protocol, $t.credential.host, $t.credential.port,`
            $t.remoteDirectory, $t.credential.authMode, $t.deleteSourceAfterUpload, $t.schedule.frequency, $t.schedule.timeOfDay)
    }
} else {
    W "未找到配置文件: $cfgPath"
}
W ""

W "===== 3. 磁盘空间 ====="
Get-PSDrive -PSProvider FileSystem |
    Format-Table Name, @{n='已用GB';e={[math]::Round($_.Used/1GB,1)}}, @{n='剩余GB';e={[math]::Round($_.Free/1GB,1)}} -AutoSize |
    Out-String | W
W ""

W "===== 4. 最近三天日志（错误 / 警告 / 结果行） ====="
$dir = $null
if ($cfg -and $cfg.settings.logDirectory) { $dir = $cfg.settings.logDirectory }
if (-not $dir) { $dir = "$env:ProgramData\FileKeeper\logs" }
$logs = Get-ChildItem $dir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 3
if (-not $logs) { W "找不到日志目录: $dir" }
foreach ($log in $logs) {
    W "----- $($log.Name) -----"
    Get-Content $log.FullName -Encoding UTF8 |
        Where-Object { $_ -match 'ERROR|WARN|RESULT|失败|跳过|异常|开始压缩|开始同步' } |
        Select-Object -Last 150 | Out-String | W
}
W ""

W "===== 5. 最新日志全文尾部 60 行 ====="
if ($logs) {
    Get-Content $logs[0].FullName -Encoding UTF8 -Tail 60 | Out-String | W
}
W ""

W "===== 6. 同步目标连通性测试 ====="
if ($cfg) {
    foreach ($t in $cfg.syncTasks) {
        $h = $t.credential.host
        $p = [int]$t.credential.port
        if ($h) {
            W "测试: $($t.name) -> ${h}:$p"
            $r = Test-NetConnection -ComputerName $h -Port $p -WarningAction SilentlyContinue
            W ("  TCP连通 = {0}   Ping = {1}" -f $r.TcpTestSucceeded, $r.PingSucceeded)
        }
    }
}
W ""

W "===== 7. 压缩源 / 输出目录抽查 ====="
if ($cfg) {
    foreach ($t in $cfg.compressionTasks) {
        foreach ($d in @($t.sourceDirectory, $t.outputDirectory)) {
            if ($d) {
                $exists = Test-Path $d
                W "  $d   存在=$exists"
                if ($exists) {
                    $files = Get-ChildItem $d -File -ErrorAction SilentlyContinue
                    W ("    文件数={0}  总大小GB={1}" -f $files.Count, [math]::Round(($files | Measure-Object Length -Sum).Sum / 1GB, 2))
                    $files | Sort-Object LastWriteTime -Descending | Select-Object -First 5 Name, Length, LastWriteTime |
                        Format-Table -AutoSize | Out-String | W
                }
            }
        }
    }
}
W ""

W "===== 8. 压缩源文件占用检测 ====="
# 检测源目录里的文件是否被其它进程锁定（SQL Server 备份未写完时 .bak 会被独占锁定）
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class LockFinder {
    [StructLayout(LayoutKind.Sequential)] struct UP { public int pid; public System.Runtime.InteropServices.ComTypes.FILETIME t; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct PI {
        public UP Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string App;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Svc;
        public int Type; public uint Status; public uint Session; [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmStartSession(out uint h, int f, string k);
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmRegisterResources(uint h, uint nf, string[] f, uint na, UP[] a, uint ns, string[] s);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint h, out uint need, ref uint count, [In,Out] PI[] info, ref uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint h);
    public static List<string> Who(string path) {
        uint h; var r = new List<string>();
        if (RmStartSession(out h, 0, Guid.NewGuid().ToString()) != 0) { r.Add("(会话创建失败)"); return r; }
        try {
            if (RmRegisterResources(h, 1, new[]{path}, 0, null, 0, null) != 0) { r.Add("(注册失败)"); return r; }
            uint need = 0, count = 0, reasons = 0;
            if (RmGetList(h, out need, ref count, null, ref reasons) == 234) {
                var info = new PI[need]; count = need;
                if (RmGetList(h, out need, ref count, info, ref reasons) == 0)
                    for (int i = 0; i < count; i++) r.Add(info[i].Process.pid + "  " + info[i].App);
            }
        } finally { RmEndSession(h); }
        return r;
    }
}
"@
if ($cfg) {
    foreach ($t in $cfg.compressionTasks) {
        if (-not $t.sourceDirectory -or -not (Test-Path $t.sourceDirectory)) { continue }
        $latest = Get-ChildItem $t.sourceDirectory -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3
        foreach ($f in $latest) {
            W ("  {0}  ({1} MB, 修改于 {2})" -f $f.Name, [math]::Round($f.Length / 1MB, 1), $f.LastWriteTime)
            foreach ($locker in [LockFinder]::Who($f.FullName)) { W ("    占用者: " + $locker) }
        }
    }
}
W ""

W "===== 9. 程序目录与临时目录 ====="
Get-ChildItem "$env:ProgramData\FileKeeper" -ErrorAction SilentlyContinue |
    Format-Table Name, LastWriteTime -AutoSize | Out-String | W
$tempDir = "$env:ProgramData\FileKeeper\temp"
if (Test-Path $tempDir) {
    $leftover = Get-ChildItem $tempDir -Recurse -File -ErrorAction SilentlyContinue
    W ("临时目录残留文件: {0} 个, 共 {1} MB（有残留说明上次压缩异常中断）" -f $leftover.Count, [math]::Round(($leftover | Measure-Object Length -Sum).Sum / 1MB, 1))
} else {
    W "临时目录不存在: $tempDir（正常）"
}
W ""

W "===== 10. 事件日志中的相关错误（最近 3 天） ====="
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-3) } -MaxEvents 300 -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match 'FileKeeper|\.NET|Application Error' -or $_.Message -match 'FileKeeper|7z' } |
    Select-Object TimeCreated, ProviderName, Id, @{n='Message';e={$_.Message.Substring(0, [Math]::Min(300, $_.Message.Length))}} |
    Format-List | Out-String | W
W ""

W "===== 诊断结束。请把本 txt 全文发回来分析 ====="
Write-Host ""
Write-Host "报告已生成: $out" -ForegroundColor Green
