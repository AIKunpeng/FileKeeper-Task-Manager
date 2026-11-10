using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FileKeeper.Core;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;

namespace FileKeeper.Setup;

/// <summary>用户在安装窗体上选择的结果</summary>
public sealed record InstallerChoice(string InstallDirectory, string? LogDirectory);

/// <summary>
/// 安装路径与日志目录选择窗体。
///
/// 用 WinForms 而不是 WPF：本产品零第三方依赖，选文件夹只有 WinForms 的
/// <see cref="FolderBrowserDialog"/> 可用（界面端的 <c>DialogService</c> 走的也是它），
/// 为了这一个对话框引入整套 WPF 运行时不值得——安装器是单文件发布，体积都算在这一个 exe 上。
///
/// 窗体自己绘制：默认外壳是浅灰控件配标题栏，与本产品深紫罗兰的界面语言放在一起像两个软件。
/// </summary>
public sealed class InstallerForm : Form
{
    private const string Accent = "#6C4DF6";
    private const string AccentHover = "#8B6BFF";
    private const string Surface = "#2A2540";
    private const string WindowBackground = "#1E1B2E";
    private const string FieldBackground = "#171430";
    private const string MutedText = "#B9B4CC";
    private const string ErrorText = "#E5484D";

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    private readonly TextBox _installBox;
    private readonly TextBox _logBox;
    private readonly Label _notice;

    private InstallerForm(string installDirectory, string? logDirectory)
    {
        Text = $"安装 {ProductInfo.Name}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        // 高度比内容多留一条：底部还有一行版权声明
        ClientSize = new Size(560, 466);
        BackColor = ColorTranslator.FromHtml(WindowBackground);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = ColorTranslator.FromHtml(WindowBackground)
        };

        // 无边框窗体没有系统拖动区，标题栏必须自己接管拖动，否则用户拖不动窗口
        titleBar.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
            }
        };

        titleBar.Controls.Add(new Label
        {
            Text = $"安装 {ProductInfo.Name}",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 16)
        });

        titleBar.Controls.Add(new Label
        {
            Text = "选择程序安装位置，并可一并指定日志存放位置。",
            ForeColor = ColorTranslator.FromHtml(MutedText),
            Font = new Font("Segoe UI", 9.5F),
            AutoSize = true,
            Location = new Point(26, 44)
        });

        var close = new Label
        {
            Text = "✕",
            Location = new Point(ClientSize.Width - 44, 14),
            Size = new Size(28, 28),
            ForeColor = ColorTranslator.FromHtml(MutedText),
            Font = new Font("Segoe UI", 11F),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;
        close.MouseEnter += (_, _) => close.ForeColor = Color.White;
        close.MouseLeave += (_, _) => close.ForeColor = ColorTranslator.FromHtml(MutedText);
        titleBar.Controls.Add(close);
        Controls.Add(titleBar);

        _installBox = MakePathBox(installDirectory);
        _logBox = MakePathBox(logDirectory ?? string.Empty);

        Controls.Add(MakePathCard(
            y: 96,
            caption: "安装目录",
            description: $"程序文件与后台服务安装到这里，默认 {InstallLayout.DefaultInstallRoot}",
            box: _installBox,
            fallback: InstallLayout.DefaultInstallRoot));

        Controls.Add(MakePathCard(
            y: 224,
            caption: "日志目录",
            description: $"留空则使用 {LogPaths.Resolve(null)}；装完后仍可在设置页修改",
            box: _logBox,
            fallback: LogPaths.Resolve(null)));

        _notice = new Label
        {
            Text = string.Empty,
            ForeColor = ColorTranslator.FromHtml(ErrorText),
            Font = new Font("Segoe UI", 9F),
            AutoSize = false,
            Size = new Size(300, 44),
            Location = new Point(24, 336)
        };
        Controls.Add(_notice);

        var install = MakeActionButton("安装", new Point(340, 380), primary: true);
        install.Click += OnInstallClick;
        Controls.Add(install);

        var cancel = MakeActionButton("取消", new Point(452, 380), primary: false);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);

        // 版权条贴底居中：安装器是用户对这个产品的第一印象，
        // 署名应该在这里就出现，而不是等装完打开界面才看到
        Controls.Add(new Label
        {
            Text = ProductInfo.Copyright,
            ForeColor = ColorTranslator.FromHtml(MutedText),
            Font = new Font("Segoe UI", 8.5F),
            AutoSize = false,
            Size = new Size(ClientSize.Width - 48, 22),
            Location = new Point(24, ClientSize.Height - 34),
            TextAlign = ContentAlignment.MiddleCenter
        });

        // Enter 确认、Esc 取消，避免用户以为只能用鼠标点那颗按钮
        AcceptButton = install;
        CancelButton = cancel;

        // 圆角要在布局稳定之后裁：Load 之前 ClientRectangle 还不是最终尺寸
        Load += (_, _) => ApplyRoundedRegion();
    }

    private string InstallDirectory => _installBox.Text.Trim();

    /// <summary>留空时返回 null，表示「用默认位置」</summary>
    private string? LogDirectory => string.IsNullOrWhiteSpace(_logBox.Text) ? null : _logBox.Text.Trim();

    /// <summary>弹出选择窗体；返回 null 表示用户取消。</summary>
    public static InstallerChoice? Prompt(string defaultInstallDirectory, string? defaultLogDirectory)
    {
        using var form = new InstallerForm(defaultInstallDirectory, defaultLogDirectory);
        return form.ShowDialog() == DialogResult.OK
            ? new InstallerChoice(form.InstallDirectory, form.LogDirectory)
            : null;
    }

    private void OnInstallClick(object? sender, EventArgs e)
    {
        // 行内提示而不是事后弹窗：用户没离开这个上下文，改起来比读完 MessageBox 再回来快得多
        var problem = ValidateInstallDirectory(InstallDirectory) ?? ValidateLogDirectory(LogDirectory);

        if (problem is not null)
        {
            _notice.Text = problem;
            return;
        }

        DialogResult = DialogResult.OK;
    }

    /// <summary>
    /// 校验安装路径。窗体与 <c>/dir=</c> 命令行入口共用同一套规则，
    /// 避免命令行绕过界面校验把程序装进磁盘根目录这类地方。
    /// </summary>
    public static string? ValidateInstallDirectory(string path)
    {
        if (path.Length == 0)
        {
            return "请指定安装目录。";
        }

        if (!Path.IsPathRooted(path))
        {
            return "请填写完整路径，例如 D:\\FileKeeper。";
        }

        if (path.Intersect(Path.GetInvalidPathChars()).Any())
        {
            return "路径包含非法字符。";
        }

        // 磁盘根目录不行：卸载时删除整个安装目录会波及用户自己的数据
        if (string.Equals(Path.GetPathRoot(path)?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return "不能安装到磁盘根目录，请选择子目录。";
        }

        if (IsInside(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path))
        {
            return "不能安装到 Windows 系统目录下。";
        }

        if (IsInside(AppPaths.RootDirectory, path) || IsInside(path, AppPaths.RootDirectory))
        {
            return $"安装目录不能与数据目录 {AppPaths.RootDirectory} 相互包含。";
        }

        return null;
    }

    /// <summary>校验日志路径；留空表示用默认位置。</summary>
    public static string? ValidateLogDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Path.IsPathRooted(path))
        {
            return "日志目录请填写完整路径，或留空使用默认位置。";
        }

        return path.Intersect(Path.GetInvalidPathChars()).Any() ? "日志目录包含非法字符。" : null;
    }

    /// <summary>candidate 是否位于 ancestor 之内（按目录段比较，避免 D:\abc 命中 D:\a）</summary>
    private static bool IsInside(string ancestor, string candidate)
    {
        var left = ancestor.TrimEnd('\\') + '\\';
        var right = candidate.TrimEnd('\\') + '\\';
        return right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private static TextBox MakePathBox(string text) => new()
    {
        Text = text,
        BackColor = ColorTranslator.FromHtml(FieldBackground),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10F),
        Dock = DockStyle.Fill
    };

    private static void Browse(TextBox target, string fallback)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        var current = target.Text.Trim();

        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }
        else if (Directory.Exists(fallback))
        {
            dialog.SelectedPath = fallback;
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private Panel MakePathCard(int y, string caption, string description, TextBox box, string fallback)
    {
        var card = new Panel
        {
            Location = new Point(24, y),
            Size = new Size(ClientSize.Width - 48, 116),
            BackColor = ColorTranslator.FromHtml(Surface)
        };

        card.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 14)
        });

        card.Controls.Add(new Label
        {
            Text = description,
            ForeColor = ColorTranslator.FromHtml(MutedText),
            Font = new Font("Segoe UI", 8.5F),
            AutoSize = false,
            Size = new Size(card.Width - 32, 32),
            Location = new Point(16, 38)
        });

        // TextBox 没有可用的边框样式，用外层容器留出 2px 描边
        var frame = new Panel
        {
            Location = new Point(16, 74),
            Size = new Size(card.Width - 132, 28),
            BackColor = ColorTranslator.FromHtml("#3A3454"),
            Padding = new Padding(2)
        };

        frame.Controls.Add(box);
        card.Controls.Add(frame);

        var browseButton = new Button
        {
            Text = "浏览",
            Location = new Point(card.Width - 108, 74),
            Size = new Size(92, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = ColorTranslator.FromHtml("#3A3454"),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };

        browseButton.FlatAppearance.BorderSize = 0;
        browseButton.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml(AccentHover);
        browseButton.Click += (_, _) => Browse(box, fallback);
        card.Controls.Add(browseButton);

        return card;
    }

    private static Button MakeActionButton(string text, Point location, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Location = location,
            Size = new Size(100, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? ColorTranslator.FromHtml(Accent) : ColorTranslator.FromHtml("#3A3454"),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };

        button.FlatAppearance.BorderSize = 0;

        // 把方块按钮画成圆角，视觉上才和界面端的卡片语言对得上
        button.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPath(new Rectangle(Point.Empty, button.Size), 10);
            e.Graphics.FillPath(new SolidBrush(button.BackColor), path);

            TextRenderer.DrawText(
                e.Graphics, button.Text, new Font("Segoe UI", 9.5F, FontStyle.Bold),
                new Rectangle(Point.Empty, button.Size), button.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        if (primary)
        {
            button.MouseEnter += (_, _) => button.BackColor = ColorTranslator.FromHtml(AccentHover);
            button.MouseLeave += (_, _) => button.BackColor = ColorTranslator.FromHtml(Accent);
        }

        return button;
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedPath(ClientRectangle, 14);
        Region = new Region(path);
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        path.StartFigure();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
