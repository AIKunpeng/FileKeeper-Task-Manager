# FileKeeper —— 任务管家

Windows 桌面程序，用于**定时把指定目录的文件打包压缩**、**定时清理过期文件**、**定时把本地目录的文件上传（同步）到 SFTP / FTP 服务器**，以及**定时执行脚本与程序**。后台以 Windows 服务形式随系统启动、静默执行；通过一个 WPF 图形界面完成配置、手动执行、日志查看与服务管理。

## 功能总览

| 功能 | 说明 |
| --- | --- |
| 定时压缩 | 目录 → ZIP / RAR，支持子目录、扩展名过滤、压缩级别、密码、分卷、压缩后删除源文件 |
| 定时清理 | 按**小时 / 天 / 周 / 月**阈值永久删除过期文件（以最后修改时间或创建时间为基准），记日志、不进回收站 |
| 定时同步 | 本地 → 远程单向上传，SFTP（密码 / 私钥）与 FTP / FTPS 显式，增量比对、保留目录结构、可选上传后删除本地源文件 |
| 定时执行程序 | 定时运行 exe / bat / cmd / ps1，支持参数与工作目录；脚本等待退出并按退出码判成败、超时强杀，图形界面程序可选「启动即完成」 |
| 调度 | 每天 / 每周（选周几）/ 每月（选几号）+ 具体时刻；错过可补跑；服务与界面共用同一套调度计算 |
| 执行控制 | 手动执行时卡片显示橙色「执行中」标记，可单个终止；底部「取消」终止整批 |
| 任务复制 / 导出 / 导入 | 卡片一键复制任务（新编号、调度重新推算）；导出为带版本号的 JSON（默认勾选当前显示的分区，可多选手动调整）；导入自动换新编号并避免重名。同步任务密码以 DPAPI 密文进出文件，跨机导入需重新输入 |
| 服务管理 | 界面内一键安装 / 卸载 / 启动 / 停止 Windows 服务，状态实时显示 |
| 托盘常驻 | 关闭窗口最小化到托盘，托盘菜单可呼出主界面、立即执行全部任务、退出 |

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1607 及以上，Windows Server 2012 及以上（.NET 8 的硬性下限）。Server 2008 / 2008 R2 不做兼容。注意 Server 2012 / 2012 R2 已于 2023 年 10 月停止安全更新，建议优先使用 Server 2016+ |
| 界面图标 | Server 2016+ 使用内置 Segoe MDL2 图标字体；Server 2012 自动回退到系统自带的 Segoe UI Symbol，个别新字形可能略有差异 |
| 运行时 | 无需安装，发布产物为自包含单文件 |
| 权限 | 安装服务需要管理员授权（UAC 按需弹出） |
| 磁盘 | 安装约占 500 MB（含 .NET 运行时与 7-Zip 组件） |

**同步是增量上传**：按「文件大小 + 最后修改时间（2 秒容差）」比对，只传新增或已变更的文件；先传临时名再改名的原子上传，中断不会在远端留下半截文件。不做下载、不做双向镜像、不删除远程多余文件。

## 技术栈

| 项目 | 目标框架 | 说明 |
| --- | --- | --- |
| `src/FileKeeper.Core` | `net8.0` | 共享内核：模型、配置、压缩引擎、同步客户端、执行器、调度计算、日志。**禁止引用任何 UI 程序集** |
| `src/FileKeeper.Service` | `net8.0-windows` | 后台执行端，注册为 Windows 服务，运行在 Session 0，无界面 |
| `src/FileKeeper.App` | `net8.0-windows` | WPF 配置端（Fluent 风格），负责任务编辑、手动执行、日志查看与服务管理 |
| `src/FileKeeper.Setup` | `net8.0-windows` | 安装程序：产出单文件 `FileKeeper-Setup.exe`，负责释放文件、注册启动服务、写卸载项与快捷方式 |
| `src/FileKeeper.SmokeTest` | `net8.0` | 冒烟测试：在临时目录真实跑通压缩 / 清理 / 同步 / 程序四条链路，不随产品发布 |

- UI：WPF + `CommunityToolkit.Mvvm`（MVVM），托盘使用内置 `System.Windows.Forms.NotifyIcon`
- 压缩：内嵌 7-Zip 原生库（`Squid-Box.SevenZipSharp` + 随包自带 `7z.dll`）；带密码压缩走随包的 `7z.exe` 命令行
- 同步：`SSH.NET`（SFTP）+ `FluentFTP`（FTP / FTPS）
- 凭据：DPAPI（`DataProtectionScope.LocalMachine`）加密后落盘，界面与 LocalSystem 服务两个上下文都能解密
- 服务：`Microsoft.Extensions.Hosting.WindowsServices`
- 配置与日志：JSON 落盘 `%ProgramData%\FileKeeper\`，原子写 + 热重载；日志按天分文件

## 架构

```
FileKeeper.App (WPF 配置端)  ──读写──>  %ProgramData%\FileKeeper\config.json
        │                                          ▲
        │ 服务安装/卸载/启停（按需 UAC 提权）          │ FileSystemWatcher 热重载
        ▼                                          │
FileKeeper.Service (Windows 服务) ──> 调度循环 ──> 压缩执行器 / 清理执行器 / 同步执行器
                                                   └──> 日志 %ProgramData%\FileKeeper\logs\
```

服务运行在 Session 0 无法显示界面，因此「执行」与「展示」拆成两个进程，仅通过共享配置文件通信。

**关于权限**：界面清单声明为 `asInvoker`，日常使用不弹 UAC。只有安装 / 卸载 / 启动 / 停止服务需要管理员权限，此时界面会以 `runas` 重新拉起 `FileKeeper.Service.exe` 并传入 `--install / --uninstall / --start / --stop` 完成操作。**不要**在界面进程里直接调用 `ServiceControl.Install()`——它会把当前进程路径注册为服务。

## 开发

```powershell
# 还原并编译
dotnet build FileKeeper.sln -c Debug

# 运行配置端
dotnet run --project src/FileKeeper.App

# 运行后台执行端（控制台模式调试）
dotnet run --project src/FileKeeper.Service

# 冒烟测试（临时目录真实执行压缩 / 清理 / 同步）
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1

# 发布（产出 dist\App 与 dist\Service，win-x64 自包含）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

## 安装包

一条命令产出可分发的安装程序：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 1.0.0
```

产出 `dist\FileKeeper-Setup.exe`（单文件，约 183 MB）：内部以 zip 载荷携带界面端与后台服务，
安装时释放到磁盘——这一步是必须的，`native\7z.dll / 7z.exe` 只有作为真实文件存在才能被引擎加载。

**安装行为**

| 环节 | 说明 |
| --- | --- |
| 安装位置 | 默认 `C:\Program Files\FileKeeper`，界面端在 `App\`、后台服务在 `Service\`；双击安装时会弹出选择窗体，可改到任意目录 |
| 服务 | 自动注册为 `FileKeeperService`（自动启动、LocalSystem），定时执行四类任务：压缩、清理、同步（SFTP / FTP / FTPS）、程序（exe / bat / cmd / ps1，超时强杀，输出进日志） |
| 快捷方式 | 桌面与开始菜单各一个，带程序图标 |
| 重新安装 | 先停止并注销旧服务→覆盖文件→重建并启动服务，同时按文件清单清理旧版残留 dll |
| 卸载 | 「添加或删除程序」里卸载，或直接运行安装目录内的 `FileKeeper-Setup.exe /uninstall` |
| 卸载定位 | 按「卸载项的 `InstallLocation` → 安装包自身所在目录 → 默认位置」反查，装到自定义目录也能干净卸掉 |
| 数据保留 | 默认**保留** `%ProgramData%\FileKeeper` 的任务配置与日志，加 `/purge` 才一并删除 |

**命令行参数**

```text
/uninstall       卸载（先停服注销，再清除文件）
/silent          静默执行，不弹窗体与结果窗口，全部输出写 stdout
/purge           卸载时一并删除 %ProgramData%\FileKeeper
/dir="路径"      指定安装根目录（给了它就不会再弹选择窗体）
/logdir="路径"   指定日志目录，省略则沿用配置，空则回到默认位置
/?               显示帮助
```

静默批量部署示例（`> log.txt` 即可拿到 UTF-8 的安装日志）：

```powershell
FileKeeper-Setup.exe /silent
FileKeeper-Setup.exe /silent /dir="D:\FileKeeper" /logdir="D:\FileKeeperLogs"
FileKeeper-Setup.exe /uninstall /purge /silent
```

**日志目录**

默认 `%ProgramData%\FileKeeper\logs`，可在安装时指定初始值，也可在「设置 → 通用 → 日志目录」随时修改（留空即回到默认位置）。
改动会立即对界面端与后台服务生效，并把旧目录里的历史日志自动搬过去，日志页仍能看到迁移之前的记录。

配置文件本身始终位于 `%ProgramData%\FileKeeper\config.json`——它必须能被 LocalSystem 与普通用户双方找到，这里只让日志位置可变。

安装器是一个独立的 `src/FileKeeper.Setup` 项目，全程**复用** `FileKeeper.Service.exe --install / --start / --stop / --uninstall`
四个既有入口，不重复实现服务注册语义。它需要管理员权限（写 Program Files、HKLM、注册服务），
这一点与界面端的 `asInvoker` 互不冲突——日常使用的界面端依然不提权。

## 重要说明：RAR 格式

RAR 是商业格式，**7-Zip 及任何开源库都只能解压 RAR，无法创建 RAR**。因此：

- **ZIP**：程序内置引擎，始终可用。
- **RAR**：创建 RAR 需要系统安装 WinRAR，程序会自动探测 `Rar.exe`；未安装时界面中「RAR」选项置灰并说明原因。
- 解压 RAR 由内置 7-Zip 引擎完成，无需额外软件。

## 已知限制

- 服务以 `LocalSystem` 运行，访问需要凭据的网络共享（UNC）目录可能失败。
- 清理任务为**永久删除**（记日志，不进回收站）；删除空目录不做。
- 同步的 SFTP 主机指纹在首次连接时自动接受并记录日志；严格指纹校验为后续计划。
