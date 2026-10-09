using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ServerUI.Core;
using ServerUI.Services;

namespace ServerUI.Views;

/*
 * 插件详情弹窗后台 —— 只读展示 DllExtItem.Meta（四级回退链已解析好）。
 * 可选字段（版本 / 作者 / 主页 / 注意事项）为空时整行隐藏；主页可点开。
 * 窗口样式与 DlgWindow 一致：打开时由主窗口压暗背景、Esc 关闭。
 */
public partial class DllExtDetailWindow : Window
{
    public DllExtDetailWindow(DllExtItem item)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        Fill(item);

        var accent = TryFindResource("Brush.Accent") as Brush;
        IconPath.Stroke = accent;
        if (accent is SolidColorBrush sc)
            IconCircle.Background = new SolidColorBrush(sc.Color) { Opacity = 0.16 };

        Loaded += (s, e) => (Application.Current?.MainWindow as MainWindow)?.SetDim(true);
        Closed += (s, e) => (Application.Current?.MainWindow as MainWindow)?.SetDim(false);
    }

    void Fill(DllExtItem item)
    {
        var m = item.Meta;
        var title = string.IsNullOrWhiteSpace(m?.Name) ? item.Name : m.Name.Trim();
        LbTitle.Text = title;
        LbTitle.ToolTip = title;
        LbFile.Text = item.Name + "  ·  " + item.SizeText + "  ·  " + item.StateText;

        var sum = (m?.Summary ?? "").Trim();
        LbSummary.Text = sum.Length > 0 ? sum : "未提供说明（第三方插件，启用前请自行确认来源）。";

        if (m?.Notes != null && m.Notes.Count > 0)
        {
            PnNotes.Visibility = Visibility.Visible;
            LbNotes.Text = string.Join("\n", m.Notes.Select(x => "· " + x));
        }

        var ver = (m?.Version ?? "").Trim();
        if (ver.Length > 0) LbVersion.Text = ver; else HideRow(LbVersion, null);

        var author = (m?.Author ?? "").Trim();
        if (author.Length > 0) LbAuthor.Text = author; else HideRow(LbAuthorHead, LbAuthor);

        var home = (m?.Homepage ?? "").Trim();
        if (home.Length > 0)
        {
            LbHomepage.Text = home;
            LbHomepage.Foreground = TryFindResource("Brush.Accent") as Brush;
            LbHomepage.Cursor = Cursors.Hand;
            LbHomepage.ToolTip = "点一下用默认浏览器打开";
            LbHomepage.MouseLeftButtonDown += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(home) { UseShellExecute = true }); } catch { }
            };
        }
        else HideRow(LbHomeHead, LbHomepage);

        LbSource.Text = (m?.Source ?? "默认")
            + "（说明内容随插件/启动器提供，启动器只读不执行插件）";
    }

    /// <summary>可选行整行隐藏（标签 + 值；标签在第一列，值在第二列）</summary>
    static void HideRow(FrameworkElement label, FrameworkElement value)
    {
        if (label != null) label.Visibility = Visibility.Collapsed;
        if (value != null) value.Visibility = Visibility.Collapsed;
    }

    void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(DllExtService.Dir); } catch { }
        Ui.OpenDir(DllExtService.Dir);
    }

    void Win_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        base.OnKeyDown(e);
    }
}
