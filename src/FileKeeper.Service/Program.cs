using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Services;
using FileKeeper.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// 自安装 / 自卸载 / 启停：这些操作需要管理员权限，由界面端以 runas 提权拉起本程序并传入参数。
// 必须在构造宿主之前处理，否则会直接以服务方式启动而执行不到安装逻辑。
if (HasArg(args, "--install"))
{
    var (installed, installOutput) = ServiceControl.Install();
    Console.WriteLine(installOutput);
    return installed ? 0 : 1;
}

if (HasArg(args, "--uninstall"))
{
    var (uninstalled, uninstallOutput) = ServiceControl.Uninstall();
    Console.WriteLine(uninstallOutput);
    return uninstalled ? 0 : 1;
}

// 启停服务同样需要管理员权限，因此也走「提权拉起本程序」这条路，
// 保证界面端在普通权限下也能控制服务。
if (HasArg(args, "--start"))
{
    var (started, startOutput) = ServiceControl.Start();
    Console.WriteLine(startOutput);
    return started ? 0 : 1;
}

if (HasArg(args, "--stop"))
{
    var (stopped, stopOutput) = ServiceControl.Stop();
    Console.WriteLine(stopOutput);
    return stopped ? 0 : 1;
}

// 兜底：任何未捕获异常都要落到日志文件，绝不让宿主静默退出后无从排查
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    FileLogger.WriteFatal($"未处理异常：{e.ExceptionObject}", e.ExceptionObject as Exception);

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    FileLogger.WriteFatal("未观察的任务异常", e.Exception);
    e.SetObserved();
};

try
{
    // 以 LocalSystem 运行时目录未必存在，必须先创建，否则文件日志无法落盘
    AppPaths.EnsureCreated();
}
catch (Exception ex)
{
    FileLogger.WriteFatal($"初始化程序目录失败：{ex.Message}", ex);
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = ServiceControl.ServiceName);
builder.Services.AddSingleton(sp =>
{
    var store = new ConfigStore();

    // 这里就要读一次配置：日志目录可能已被安装界面或设置页改到别处，
    // 写死默认目录会让服务的日志与用户在界面里看到的不是同一处
    try
    {
        store.Load();
    }
    catch (Exception ex)
    {
        FileLogger.WriteFatal($"服务读取配置失败，将以默认配置启动：{ex.Message}", ex);
    }

    return store;
});

builder.Services.AddSingleton<IAppLogger>(sp =>
{
    var settings = sp.GetRequiredService<ConfigStore>().Current.Settings;
    return new FileLogger(
        LogPaths.Resolve(settings.LogDirectory),
        Math.Max(1, settings.LogRetentionDays));
});
builder.Services.AddHostedService(sp => new SchedulerWorker(
    sp.GetRequiredService<IAppLogger>(),
    sp.GetRequiredService<ConfigStore>()));

// 仅在真正以服务方式运行时写事件日志：注册事件源需要管理员权限，
// 控制台调试时注册会失败并影响启动。
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddEventLog();
}

await builder.Build().RunAsync();
return 0;

static bool HasArg(string[] args, string name) =>
    args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
