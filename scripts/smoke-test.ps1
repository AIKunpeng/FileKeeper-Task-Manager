# 冒烟测试：在临时目录里真实跑通「压缩 / 清理 / 同步」三条链路。
# 用法：  powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
# 说明：  全程使用 %TEMP% 下的独立目录，不触碰 ProgramData 与真实业务数据；
#         同步链路使用内存测试替身，不依赖外部 FTP / SFTP 服务端。

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '== 1/2 编译解决方案 ==' -ForegroundColor Cyan
dotnet build FileKeeper.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '编译失败，冒烟中止。' -ForegroundColor Red
    exit 1
}

Write-Host '== 2/2 运行冒烟测试 ==' -ForegroundColor Cyan
dotnet run --project src/FileKeeper.SmokeTest/FileKeeper.SmokeTest.csproj -c Debug --no-build
if ($LASTEXITCODE -ne 0) {
    Write-Host '冒烟未通过。' -ForegroundColor Red
    exit 1
}

Write-Host '冒烟通过。' -ForegroundColor Green
exit 0
