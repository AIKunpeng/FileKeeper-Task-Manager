using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FileKeeper.Core;

namespace FileKeeper.App.Services;

/// <summary>
/// 系统托盘。程序关闭到托盘后后台常驻，用户可从托盘呼出主界面。
/// 图标直接取自身可执行文件的图标，无需额外维护一份图标资源。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private NotifyIcon? _notifyIcon;

    /// <summary>双击托盘图标</summary>
    public event EventHandler? ShowRequested;

    /// <summary>右键菜单「立即执行全部任务」</summary>
    public event EventHandler? RunAllRequested;

    /// <summary>右键菜单「退出」</summary>
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = ProductInfo.Name,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.MouseClick += OnMouseClick;
    }

    /// <summary>气泡提示。用于服务状态变化与任务失败这类需要即时知晓的事件。</summary>
    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_notifyIcon is null || !_notifyIcon.Visible)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(4000, title, message, icon);
    }

    public void Dispose()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("打开主界面");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var runAllItem = new ToolStripMenuItem("立即执行全部任务");
        runAllItem.Click += (_, _) => RunAllRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(showItem);
        menu.Items.Add(runAllItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private static Icon? LoadIcon()
    {
        try
        {
            // ApplicationIcon 会把图标编进 exe，直接取出来即可
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception)
        {
            // 取不到就退回无图标，不能因为图标加载失败导致程序起不来
        }

        return SystemIcons.Application;
    }
}
