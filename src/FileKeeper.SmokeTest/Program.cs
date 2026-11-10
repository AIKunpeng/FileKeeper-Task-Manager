using FileKeeper.Core.Engines;
using FileKeeper.Core.Execution;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.SmokeTest;

/// <summary>
/// 冒烟测试入口：在临时目录里真实跑通「压缩 / 清理 / 同步」三条链路。
/// 全部使用 Path.GetTempPath() 下的独立目录，结束时清理，不触碰 ProgramData。
/// 退出码 0 表示全部通过，1 表示存在断言失败。
/// </summary>
public static class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        IAppLogger logger = new ConsoleLogger();
        var root = Path.Combine(Path.GetTempPath(), "FileKeeperSmoke_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        Console.WriteLine($"冒烟根目录：{root}");
        Console.WriteLine(new string('=', 72));

        try
        {
            await CompressionSmokeAsync(root, logger);
            await CleanupSmokeAsync(root, logger);
            await SyncSmokeAsync(root, logger);
            await ProgramSmokeAsync(root, logger);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
                Console.WriteLine();
                Console.WriteLine($"临时目录已清理：{root}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"（临时目录清理失败，可手动删除：{root}，原因：{ex.Message}）");
            }
        }

        Console.WriteLine(new string('=', 72));

        if (_failures > 0)
        {
            Console.WriteLine($"冒烟未通过：{_failures} 项断言失败");
            return 1;
        }

        Console.WriteLine("冒烟全部通过。");
        return 0;
    }

    // ==================== 压缩 ====================

    private static async Task CompressionSmokeAsync(string root, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("【一、压缩链路】");
        var source = Path.Combine(root, "zip-src");
        var output = Path.Combine(root, "zip-out");

        FakeSyncClient.WriteFile(Path.Combine(source, "a.txt"), "hello filekeeper");
        FakeSyncClient.WriteFile(Path.Combine(source, "sub", "b.txt"), new string('B', 4096));
        FakeSyncClient.WriteFile(Path.Combine(source, "noise.log"), "should be filtered out");

        // 场景 1：无密码 ZIP（内嵌 7-Zip 库），按扩展名过滤，保留源文件
        var runner = new CompressionRunner(logger);
        var task = new CompressionTask
        {
            Name = "冒烟-压缩-无密码",
            SourceDirectory = source,
            OutputDirectory = output,
            FileNameTemplate = "smoke_nopass",
            Format = ArchiveFormat.Zip,
            CompressionLevel = 5,
            FilterMode = FileFilterMode.IncludeExtensions,
            Extensions = { ".txt" }
        };

        var result = await runner.RunAsync(task);

        Check(result.Status == TaskRunStatus.Success, "无密码压缩成功", $"状态={result.Status}，{result.ErrorMessage}");
        Check(result.SuccessCount == 2, "按扩展名过滤后压缩 2 个文件", $"实际 {result.SuccessCount}");
        Check(File.Exists(result.OutputPath), "压缩包已生成", result.OutputPath ?? "（空）");
        Check(File.Exists(Path.Combine(source, "a.txt")), "未开启删源时源文件保留");
        Check(!Directory.EnumerateFiles(output, "*.log").Any(), "noise.log 未被压入");
        Check(result.OutputPath is not null && new FileInfo(result.OutputPath).Length > 0, "压缩包非空");

        // 场景 2：带密码 ZIP（7z.exe 命令行引擎）+ 压缩后删除源文件
        var source2 = Path.Combine(root, "zip-src2");
        FakeSyncClient.WriteFile(Path.Combine(source2, "c.txt"), "secret content");
        FakeSyncClient.WriteFile(Path.Combine(source2, "d.txt"), "another secret");

        var task2 = new CompressionTask
        {
            Name = "冒烟-压缩-带密码删源",
            SourceDirectory = source2,
            OutputDirectory = output,
            FileNameTemplate = "smoke_pass",
            Format = ArchiveFormat.Zip,
            CompressionLevel = 5,
            Password = "Smoke#123",
            DeleteSourceAfterCompress = true
        };

        var result2 = await runner.RunAsync(task2);

        Check(result2.Status == TaskRunStatus.Success, "带密码压缩成功", $"状态={result2.Status}，{result2.ErrorMessage}");
        Check(result2.SuccessCount == 2, "压缩了 2 个文件", $"实际 {result2.SuccessCount}");
        Check(!File.Exists(Path.Combine(source2, "c.txt")) && !File.Exists(Path.Combine(source2, "d.txt")),
            "压缩后源文件已删除");
        Check(File.Exists(result2.OutputPath), "带密码压缩包已生成", result2.OutputPath ?? "（空）");

        // 场景 3：RAR 创建能力（本机装了 WinRAR 才验证，未装则跳过）
        if (ArchiveEngineFactory.CanCreate(ArchiveFormat.Rar))
        {
            var task3 = new CompressionTask
            {
                Name = "冒烟-压缩-RAR",
                SourceDirectory = source,
                OutputDirectory = output,
                FileNameTemplate = "smoke_rar",
                Format = ArchiveFormat.Rar
            };

            var result3 = await runner.RunAsync(task3);
            Check(result3.Status == TaskRunStatus.Success, "RAR 压缩成功", $"状态={result3.Status}，{result3.ErrorMessage}");
        }
        else
        {
            Console.WriteLine("  （本机未检测到 WinRAR，跳过 RAR 场景）");
        }
    }

    // ==================== 清理 ====================

    private static async Task CleanupSmokeAsync(string root, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("【二、清理链路】");
        var target = Path.Combine(root, "cleanup");

        var oldFile = Path.Combine(target, "old.txt");
        var oldInSub = Path.Combine(target, "sub", "old2.txt");
        var newFile = Path.Combine(target, "new.txt");
        var futureFile = Path.Combine(target, "future.txt");

        FakeSyncClient.WriteFile(oldFile, "old");
        FakeSyncClient.WriteFile(oldInSub, "old in sub");
        FakeSyncClient.WriteFile(newFile, "new");
        FakeSyncClient.WriteFile(futureFile, "future");

        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-30));
        File.SetLastWriteTime(oldInSub, DateTime.Now.AddDays(-30));
        File.SetLastWriteTime(futureFile, DateTime.Now.AddDays(1));

        var runner = new CleanupRunner(logger);
        var task = new CleanupTask
        {
            Name = "冒烟-清理",
            TargetDirectory = target,
            IncludeSubDirectories = true,
            ThresholdValue = 7,
            ThresholdUnit = CleanupThresholdUnit.Days,
            TimeBasis = AgeTimeBasis.LastWriteTime
        };

        var result = await runner.RunAsync(task);

        Check(result.Status == TaskRunStatus.Success, "清理执行成功", $"状态={result.Status}，{result.ErrorMessage}");
        Check(result.SuccessCount == 2, "删除 2 个过期文件", $"实际 {result.SuccessCount}");
        Check(!File.Exists(oldFile) && !File.Exists(oldInSub), "过期文件（含子目录）已删除");
        Check(File.Exists(newFile), "新文件保留");
        Check(File.Exists(futureFile), "未来时间戳的文件不被删除");
        Check(result.SkippedCount == 2, "新文件与未来文件计入跳过", $"实际 {result.SkippedCount}");

        // 场景 2：小时级阈值——2 小时前的文件，按「1 小时」阈值应被删除
        var recentish = Path.Combine(target, "hours-old.txt");
        FakeSyncClient.WriteFile(recentish, "recentish");
        File.SetLastWriteTime(recentish, DateTime.Now.AddHours(-2));

        var hoursTask = new CleanupTask
        {
            Name = "冒烟-清理-小时阈值",
            TargetDirectory = target,
            ThresholdValue = 1,
            ThresholdUnit = CleanupThresholdUnit.Hours,
            TimeBasis = AgeTimeBasis.LastWriteTime
        };

        var hoursResult = await runner.RunAsync(hoursTask);

        Check(hoursResult.Status == TaskRunStatus.Success, "小时阈值清理执行成功", $"状态={hoursResult.Status}，{hoursResult.ErrorMessage}");
        Check(!File.Exists(recentish), "2 小时前的文件被 1 小时阈值删除");
    }

    // ==================== 同步 ====================

    private static async Task SyncSmokeAsync(string root, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("【三、同步链路（内存测试替身）】");
        var local = Path.Combine(root, "sync-src");
        var client = new FakeSyncClient();
        var runner = new SyncRunner(logger, _ => client);

        SyncTask NewTask(bool deleteAfterUpload = false) => new()
        {
            Name = "冒烟-同步",
            LocalDirectory = local,
            Protocol = SyncProtocol.Ftp,
            Host = "smoke.test",
            Username = "smoke",
            Credential = { Password = "smoke-pass" },
            RemoteDirectory = "/backup",
            IncrementalSync = true,
            PreserveDirectoryStructure = true,
            DeleteLocalAfterUpload = deleteAfterUpload,
            RetryCount = 0,
            FilterMode = FileFilterMode.IncludeExtensions,
            Extensions = { ".txt" }
        };

        // —— 1. 首次全量上传 ——
        FakeSyncClient.WriteFile(Path.Combine(local, "a.txt"), "alpha");
        FakeSyncClient.WriteFile(Path.Combine(local, "sub", "b.txt"), "beta");
        FakeSyncClient.WriteFile(Path.Combine(local, "skip.log"), "filtered");

        var first = await runner.RunAsync(NewTask());

        Check(first.Status == TaskRunStatus.Success, "首次同步成功", $"状态={first.Status}，{first.ErrorMessage}");
        Check(first.SuccessCount == 2, "首次上传 2 个文件（.log 被过滤）", $"实际 {first.SuccessCount}");
        Check(client.Store.ContainsKey("/backup/a.txt"), "远端存在 /backup/a.txt");
        Check(client.Store.ContainsKey("/backup/sub/b.txt"), "远端保留目录结构 /backup/sub/b.txt");

        // —— 2. 二次执行：无变更应全部跳过 ——
        var second = await runner.RunAsync(NewTask());

        Check(second.Status == TaskRunStatus.Success, "二次同步成功");
        Check(second.SuccessCount == 0, "无变更时不重传", $"实际上传 {second.SuccessCount}");
        Check(second.SkippedCount == 2, "未变更文件计入跳过", $"实际 {second.SkippedCount}");
        Check(client.UploadCount == 2, "累计真实上传仍为 2 次", $"实际 {client.UploadCount}");

        // —— 3. 修改后重新上传（含重试路径验证）——
        await Task.Delay(50);
        FakeSyncClient.WriteFile(Path.Combine(local, "a.txt"), "alpha-v2");
        File.SetLastWriteTimeUtc(Path.Combine(local, "a.txt"), DateTime.UtcNow.AddSeconds(10));
        client.FailNextUploads = 1; // 第一次上传失败，验证重试后成功

        var taskWithRetry = NewTask();
        taskWithRetry.RetryCount = 3;
        var third = await runner.RunAsync(taskWithRetry);

        Check(third.Status == TaskRunStatus.Success, "修改后同步成功（含 1 次重试）",
            $"状态={third.Status}，{third.ErrorMessage}");
        Check(third.SuccessCount == 1, "仅重传变更的 1 个文件", $"实际 {third.SuccessCount}");
        var stored = client.Store["/backup/a.txt"];
        Check(FakeSyncClient.Hash(stored.Content) == FakeSyncClient.Hash("alpha-v2"u8.ToArray()),
            "远端内容已更新为最新版本");

        // —— 4. 上传成功后删除本地源文件 ——
        FakeSyncClient.WriteFile(Path.Combine(local, "sub", "c.txt"), "gamma");
        var fourth = await runner.RunAsync(NewTask(deleteAfterUpload: true));

        Check(fourth.Status == TaskRunStatus.Success, "删源模式同步成功", $"状态={fourth.Status}，{fourth.ErrorMessage}");
        Check(fourth.SuccessCount == 1, "删源模式只上传新文件", $"实际 {fourth.SuccessCount}");
        Check(!File.Exists(Path.Combine(local, "sub", "c.txt")), "上传成功后本地源文件已删除");
        Check(File.Exists(Path.Combine(local, "a.txt")), "未上传的文件不受删源影响");
    }

    // ==================== 断言 ====================

    // ==================== 程序 ====================

    private static async Task ProgramSmokeAsync(string root, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("【四、程序任务链路】");
        var runner = new ProgramRunner(logger);

        // 场景 1：cmd 批处理成功，退出码 0
        var script = Path.Combine(root, "ok.cmd");
        File.WriteAllText(script, "@echo off\r\necho smoke-ok\r\nexit /b 0\r\n");

        var result = await runner.RunAsync(new ProgramTask
        {
            Name = "冒烟-程序-成功",
            FilePath = script,
            TimeoutSeconds = 60
        });

        Check(result.Status == TaskRunStatus.Success, "批处理执行成功", $"状态={result.Status}，{result.ErrorMessage}");
        Check(result.ExitCode == 0, "退出码 0", $"实际 {result.ExitCode}");
        Check(result.Details.Any(line => line.Contains("smoke-ok")), "输出被捕获进明细");

        // 场景 2：退出码非 0 判定失败
        var failScript = Path.Combine(root, "fail.cmd");
        File.WriteAllText(failScript, "@echo off\r\necho boom\r\nexit /b 3\r\n");

        var result2 = await runner.RunAsync(new ProgramTask
        {
            Name = "冒烟-程序-非零退出码",
            FilePath = failScript,
            TimeoutSeconds = 60
        });

        Check(result2.Status == TaskRunStatus.Failed, "退出码非 0 判定失败", $"状态={result2.Status}");
        Check(result2.ExitCode == 3, "退出码 3 被带回结果", $"实际 {result2.ExitCode}");
        Check(result2.ErrorMessage is not null && result2.ErrorMessage.Contains("3"), "错误信息包含退出码");

        // 场景 3：超时强杀
        var hangScript = Path.Combine(root, "hang.cmd");
        File.WriteAllText(hangScript, "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n");

        var result3 = await runner.RunAsync(new ProgramTask
        {
            Name = "冒烟-程序-超时",
            FilePath = hangScript,
            TimeoutSeconds = 3
        });

        Check(result3.Status == TaskRunStatus.Failed, "超时任务判定失败", $"状态={result3.Status}，{result3.ErrorMessage}");
        Check(result3.ErrorMessage is not null && result3.ErrorMessage.Contains("超时"), "错误信息说明超时强杀");

        // 场景 4：文件不存在
        var result4 = await runner.RunAsync(new ProgramTask
        {
            Name = "冒烟-程序-文件缺失",
            FilePath = Path.Combine(root, "no-such-script.cmd"),
            TimeoutSeconds = 60
        });

        Check(result4.Status == TaskRunStatus.Failed, "目标文件缺失判定失败", $"状态={result4.Status}，{result4.ErrorMessage}");

        // 场景 5：启动即完成（GUI 模式）——长驻程序不等待、判定成功
        var result5 = await runner.RunAsync(new ProgramTask
        {
            Name = "冒烟-程序-启动即完成",
            FilePath = hangScript,
            WaitForExit = false,
            TimeoutSeconds = 60
        });

        Check(result5.Status == TaskRunStatus.Success, "启动即完成模式不等待长驻程序", $"状态={result5.Status}，{result5.ErrorMessage}");
        Check(result5.ExitCode is null, "未等待退出所以没有退出码", $"实际 {result5.ExitCode}");

        // 场景 6：启动即完成模式下秒退且退出码非 0，仍要判失败
        var result6 = await runner.RunAsync(new ProgramTask
        {
            Name = "冒烟-程序-启动即完成秒退",
            FilePath = failScript,
            WaitForExit = false,
            TimeoutSeconds = 60
        });

        Check(result6.Status == TaskRunStatus.Failed, "秒退且退出码非 0 仍判定失败", $"状态={result6.Status}，{result6.ErrorMessage}");
    }

    private static void Check(bool condition, string message, string? detail = null)
    {
        if (condition)
        {
            Console.WriteLine($"  [通过] {message}{(detail is null ? string.Empty : $"（{detail}）")}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  [失败] {message}{(detail is null ? string.Empty : $"（{detail}）")}");
        }
    }
}
