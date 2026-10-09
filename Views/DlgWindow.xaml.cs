using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ServerUI.Views;

/*
 * 通用玻璃对话框 — 替代系统 MessageBox (风格与主界面一致):
 *   · 图标圆底 (信息=青 / 警告=金 / 错误=红 / 询问=青)
 *   · 按钮右对齐: 主操作 accent, 次操作 glass, 取消 quiet
 *   · Esc = 取消; Enter = 默认按钮
 *   · 打开时由主窗口压暗背景 (SetDim)
 */
public partial class DlgWindow : Window
{
    public enum Kind { Info, Warn, Error, Question }

    MessageBoxResult _result = MessageBoxResult.Cancel;

    public DlgWindow(string title, string message, MessageBoxButton buttons, Kind kind)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        TitleText.Text = title;
        BodyText.Text = message;

        // 图标与配色
        string iconKey;
        string colorKey;
        switch (kind)
        {
            case Kind.Warn: iconKey = "IcDlgWarn"; colorKey = "Brush.Warn"; break;
            case Kind.Error: iconKey = "IcClear"; colorKey = "Brush.Danger"; break;
            case Kind.Question: iconKey = "IcHelp"; colorKey = "Brush.Accent"; break;
            default: iconKey = "IcDlgInfo"; colorKey = "Brush.Accent"; break;
        }
        var accent = (Brush)TryFindResource(colorKey);
        IconPath.Data = (Geometry)TryFindResource(iconKey);
        IconPath.Stroke = accent;
        IconCircle.Background = accent != null
            ? new SolidColorBrush(((SolidColorBrush)accent).Color) { Opacity = 0.16 }
            : Brushes.Transparent;

        // 按钮组
        switch (buttons)
        {
            case MessageBoxButton.OK:
                // 信息/警告/错误类弹窗给一个明确的【确定】:
                // 只有右上角 ✕ 时, 玩家看不到主操作, Enter 也没反应 ——
                // 旧 WinForms 版用的是系统 MessageBox, 一直是有【确定】的。
                AddBtn("确定", "BtnAccent", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.OKCancel:
                AddBtn("确定", "BtnAccent", MessageBoxResult.OK, true);
                AddBtn("取消", "BtnGlass", MessageBoxResult.Cancel, false);
                break;
            case MessageBoxButton.YesNo:
                AddBtn("是", "BtnAccent", MessageBoxResult.Yes, true);
                AddBtn("否", "BtnGlass", MessageBoxResult.No, false);
                break;
            default: // YesNoCancel
                AddBtn("是", "BtnAccent", MessageBoxResult.Yes, true);
                AddBtn("否", "BtnGlass", MessageBoxResult.No, false);
                AddBtn("取消", "BtnQuiet", MessageBoxResult.Cancel, false);
                break;
        }

        Loaded += (s, e) => MainWindow_SetDim(true);
        Closed += (s, e) => MainWindow_SetDim(false);
    }

    void AddBtn(string text, string styleKey, MessageBoxResult result, bool isDefault)
    {
        var b = new Button
        {
            Content = text,
            Style = TryFindResource(styleKey) as Style,
            MinWidth = 96,
            Margin = new Thickness(BtnBar.Children.Count == 0 ? 0 : 10, 0, 0, 0),
            IsDefault = isDefault
        };
        b.Click += (s, e) => { _result = result; Close(); };
        BtnBar.Children.Add(b);
    }

    void MainWindow_SetDim(bool on)
        => (Application.Current?.MainWindow as MainWindow)?.SetDim(on);

    void Win_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Cancel;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _result = MessageBoxResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    public MessageBoxResult Result => _result;

    // =================================================================
    // 静态入口
    // =================================================================
    public static MessageBoxResult Show(Window owner, string title, string message,
        MessageBoxButton buttons = MessageBoxButton.OK, Kind kind = Kind.Info)
    {
        var dlg = new DlgWindow(title, message, buttons, kind);
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        dlg.ShowDialog();
        return dlg.Result;
    }
}
