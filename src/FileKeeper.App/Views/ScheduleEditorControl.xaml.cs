using System.Windows.Controls;

namespace FileKeeper.App.Views;

/// <summary>
/// 调度编辑控件。压缩 / 清理 / 同步三个编辑页共用，
/// 绑定到一个 ScheduleEditViewModel 上（由宿主设置 DataContext）。
/// </summary>
public partial class ScheduleEditorControl : UserControl
{
    public ScheduleEditorControl()
    {
        InitializeComponent();
    }
}
