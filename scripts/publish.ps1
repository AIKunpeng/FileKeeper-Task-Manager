# 发布脚本：产出 dist\App（界面端）、dist\Service（后台服务）两个自包含目录，
# 并把二者打包成单个安装程序 dist\FileKeeper-Setup.exe。
# 用法：  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Configuration Release] [-Version 1.0.0]
#
# 说明：
# - 采用 win-x64 自包含发布，目标机器无需安装 .NET 运行时；
# - 界面端不使用单文件发布：7z.dll / 7z.exe 必须以真实文件存在于 native 目录才能被引擎加载；
#   安装程序恰恰相反，它把 App / Service 作为 zip 载荷内嵌、安装时释放到磁盘，所以收敛成单文件是安全的；
# - -Version 决定安装包在「添加或删除程序」里显示的版本号，发新版本时在这里改一次即可；
# - 发布完成后建议先运行 scripts\smoke-test.ps1 验证，再分发 dist 目录。

param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
Set-Location $root

$dist = Join-Path $root 'dist'

# 任一阶段失败立刻停下：后续步骤都依赖前面的产物，
# 继续跑只会堆积成难以定位的错误，不如在失败点就报清楚是哪一个阶段。
function Stop-Pipeline([string]$stage, [string]$detail) {
    Write-Host ""
    Write-Host ("[{0}] 失败：{1}" -f $stage, $detail) -ForegroundColor Red
    exit 1
}

Write-Host "== 清理旧的发布目录 ==" -ForegroundColor Cyan
if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}

Write-Host "== 发布界面端 → dist\App ==" -ForegroundColor Cyan
dotnet publish src/FileKeeper.App/FileKeeper.App.csproj `
    -c $Configuration -r win-x64 --self-contained true -p:Version=$Version `
    -o (Join-Path $dist 'App') --nologo
if ($LASTEXITCODE -ne 0) { Stop-Pipeline '发布界面端' 'dotnet publish 未成功，详见上方输出' }

Write-Host "== 发布后台服务 → dist\Service ==" -ForegroundColor Cyan
dotnet publish src/FileKeeper.Service/FileKeeper.Service.csproj `
    -c $Configuration -r win-x64 --self-contained true -p:Version=$Version `
    -o (Join-Path $dist 'Service') --nologo
if ($LASTEXITCODE -ne 0) { Stop-Pipeline '发布后台服务' 'dotnet publish 未成功，详见上方输出' }

# ---------------- 安装包载荷 ----------------
# 把两个自包含目录与图标整理成一个 zip，供 FileKeeper.Setup 以嵌入资源打包进 exe。
# 顺序不能颠倒：payload.zip 必须先存在，Setup 项目的条件 EmbeddedResource 才会把它编进去。
Write-Host "== 打包安装包载荷 ==" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$staging = Join-Path $env:TEMP ('FileKeeperPayload_' + [guid]::NewGuid().ToString('N'))
$payloadDir = Join-Path $root 'src\FileKeeper.Setup\payload'
$payloadZip = Join-Path $payloadDir 'payload.zip'

try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $dist 'App') -Destination (Join-Path $staging 'App') -Recurse -Force
    Copy-Item -Path (Join-Path $dist 'Service') -Destination (Join-Path $staging 'Service') -Recurse -Force
    Copy-Item -Path 'src\FileKeeper.App\Assets\app.ico' -Destination (Join-Path $staging 'app.ico') -Force

    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }

    # 逐文件写入，而不是 CreateFromDirectory：后者在 Windows 上会用反斜杠做分隔符，
    # 而 zip 规范约定正斜杠；统一成 '/' 后，安装端取值才是干净的相对路径。
    $zipStream = [System.IO.File]::Create($payloadZip)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -Path $staging -Recurse -File) {
                $relative = $file.FullName.Substring($staging.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $file.FullName, $relative,
                    [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $zipStream.Dispose() }
}
finally {
    # 临时 staging 必须清掉，中间产物不该留在任何持久目录里
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

$payloadSize = [math]::Round((Get-Item $payloadZip).Length / 1MB, 1)
Write-Host ("载荷已生成：{0}（{1} MB）" -f $payloadZip, $payloadSize) -ForegroundColor Cyan

Write-Host "== 打包安装程序 → dist\FileKeeper-Setup.exe ==" -ForegroundColor Cyan
$setupOutput = Join-Path $dist '_setup'
dotnet publish src/FileKeeper.Setup/FileKeeper.Setup.csproj `
    -c $Configuration -r win-x64 --self-contained true -p:Version=$Version `
    -o $setupOutput --nologo
if ($LASTEXITCODE -ne 0) { Stop-Pipeline '打包安装程序' 'dotnet publish 未成功，详见上方输出' }

$setupExe = Join-Path $setupOutput 'FileKeeper.Setup.exe'
if (-not (Test-Path $setupExe)) { Stop-Pipeline '打包安装程序' "未找到单文件产物 $setupExe" }

# 单文件产物名跟随 AssemblyName，这里改成对外分发的名字；中间目录随之删除
Move-Item -Path $setupExe -Destination (Join-Path $dist 'FileKeeper-Setup.exe') -Force
Remove-Item $setupOutput -Recurse -Force

# 自检：压缩引擎依赖的原生库必须随包分发，缺一个发布就不完整
Write-Host "== 产物自检 ==" -ForegroundColor Cyan
$required = @(
    'dist\App\FileKeeper.App.exe',
    'dist\App\native\7z.dll',
    'dist\App\native\7z.exe',
    'dist\Service\FileKeeper.Service.exe',
    'dist\FileKeeper-Setup.exe'
)
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $root $_)) })
if ($missing.Count -gt 0) {
    Stop-Pipeline '产物自检' ("发布不完整，缺少：" + ($missing -join '、'))
}

$appSize = [math]::Round((Get-ChildItem (Join-Path $dist 'App') -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$svcSize = [math]::Round((Get-ChildItem (Join-Path $dist 'Service') -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$setupSize = [math]::Round((Get-Item (Join-Path $dist 'FileKeeper-Setup.exe')).Length / 1MB, 1)
Write-Host ("发布完成（版本 {0}）：dist\App（{1} MB）、dist\Service（{2} MB）、dist\FileKeeper-Setup.exe（{3} MB）" -f $Version, $appSize, $svcSize, $setupSize) -ForegroundColor Green

Write-Host ""
Write-Host "安装方式：" -ForegroundColor Cyan
Write-Host "  交互式：双击 dist\FileKeeper-Setup.exe（需确认一次权限提升）"
Write-Host "  静默：  FileKeeper-Setup.exe /silent"
Write-Host "  卸载：  FileKeeper-Setup.exe /uninstall [/purge]"
