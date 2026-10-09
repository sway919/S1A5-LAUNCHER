/*
 * ==================================================================
 * Ui — 对话框与小工具助手 (Core/Ui.cs)  [v2: 全部改为自制玻璃对话框]
 * ==================================================================
 * 对外签名保持不变 (返回 MessageBoxResult), 内部走 Views.DlgWindow,
 * 因此全部调用点无需改动, 风格与主界面完全统一。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerUI.Views;
using WinForms = System.Windows.Forms;

namespace ServerUI.Core;

public static class Ui
{
    static Window W => Application.Current?.MainWindow;

    // ---- 消息框 (自制玻璃弹窗) ----
    public static MessageBoxResult Confirm(string text, string title, MessageBoxButton btn = MessageBoxButton.YesNo)
        => DlgWindow.Show(W, title, text, btn, DlgWindow.Kind.Question);

    public static void Info(string text, string title = "提示")
        => DlgWindow.Show(W, title, text, MessageBoxButton.OK, DlgWindow.Kind.Info);

    public static void Warn(string text, string title = "注意")
        => DlgWindow.Show(W, title, text, MessageBoxButton.OK, DlgWindow.Kind.Warn);

    public static void Err(string text, string title = "错误")
        => DlgWindow.Show(W, title, text, MessageBoxButton.OK, DlgWindow.Kind.Error);

    // ---- 资源管理器 ----
    public static void OpenDir(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + dir + "\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ---- 文件夹选择 (系统 FolderBrowserDialog, 与 WinForms 版一致) ----
    public static string PickFolder(string description)
    {
        try
        {
            using var fbd = new WinForms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = false
            };
            if (fbd.ShowDialog() != WinForms.DialogResult.OK) return null;
            return fbd.SelectedPath;
        }
        catch { return null; }
    }

    // ---- 文件选择 ----
    public static string PickFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog(W) == true ? dlg.FileName : null;
    }

    public static string[] PickImages(string title = "导入背景图片（可多选）")
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Multiselect = true
        };
        return dlg.ShowDialog(W) == true ? dlg.FileNames : null;
    }

    // ---- 文本输入小窗 (玻璃样式, 等价 WinForms 版 InputDialog) ----
    // tip = 灰字小提示 (WinForms 版 InputDialog 的第三个参数, 迁移时漏掉了)
    public static string PromptText(string title, string label, string initial = "", string tip = null)
    {
        var win = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current?.TryFindResource("Brush.GlassStrong"),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Owner = W
        };

        var ok = false;
        var tb = new TextBox
        {
            Style = Application.Current?.TryFindResource("Input") as Style,
            Text = initial ?? "",
            Margin = new Thickness(0, 14, 0, 0)
        };

        var grid = new Grid { Margin = new Thickness(26, 0, 26, 24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { Height = 64 };
        head.Children.Add(new TextBlock
        {
            Text = title,
            Style = Application.Current?.TryFindResource("H2") as Style,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Style = Application.Current?.TryFindResource("WinBtnClose") as Style,
            Width = 36,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (s, e) => win.Close();
        head.Children.Add(close);

        var cap = new TextBlock
        {
            Text = label,
            Style = Application.Current?.TryFindResource("Txt2") as Style,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(cap, 1);
        Grid.SetRow(tb, 2);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        var btOk = new Button
        {
            Content = "确定",
            Style = Application.Current?.TryFindResource("BtnAccent") as Style,
            MinWidth = 100,
            IsDefault = true
        };
        var btNo = new Button
        {
            Content = "取消",
            Style = Application.Current?.TryFindResource("BtnGlass") as Style,
            MinWidth = 100,
            Margin = new Thickness(10, 0, 0, 0),
            IsCancel = true
        };
        btOk.Click += (s, e) => { ok = true; win.Close(); };
        btNo.Click += (s, e) => win.Close();
        row.Children.Add(btOk);
        row.Children.Add(btNo);

        grid.Children.Add(head);
        grid.Children.Add(cap);
        grid.Children.Add(tb);
        grid.Children.Add(row);
        Grid.SetRow(row, 4);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 灰字小提示 (可多行): 例如"支持中文名称，储存后可在表格里双击切换"
        if (!string.IsNullOrEmpty(tip))
        {
            var tipBlock = new TextBlock
            {
                Text = tip,
                Style = Application.Current?.TryFindResource("Txt3") as Style,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(tipBlock, 3);
            grid.Children.Add(tipBlock);
        }

        win.Content = new Border
        {
            BorderBrush = Application.Current?.TryFindResource("Brush.GlassBorder") as Brush,
            BorderThickness = new Thickness(1),
            Child = grid
        };

        win.Loaded += (s, e) =>
        {
            (Application.Current?.MainWindow as MainWindow)?.SetDim(true);
            tb.Focus();
            tb.SelectAll();
        };
        win.Closed += (s, e) => (Application.Current?.MainWindow as MainWindow)?.SetDim(false);
        win.ShowDialog();
        return ok ? tb.Text.Trim() : null;
    }

    // ---- 列表选择小窗 (玻璃样式, 等价 PromptSelectDir / DllPickForm) ----
    public static string SelectFromList(string title, string label, IList<string> items)
    {
        if (items == null || items.Count == 0) return null;
        string pick = null;

        var win = new Window
        {
            Title = title,
            Width = 620,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current?.TryFindResource("Brush.GlassStrong"),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Owner = W
        };

        var lb = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemsSource = items,
            ItemContainerStyle = Application.Current?.TryFindResource("RowItem") as Style,
            Margin = new Thickness(0, 8, 0, 0)
        };
        if (lb.ItemContainerStyle == null)
            lb.Foreground = (Brush)Application.Current?.TryFindResource("Brush.Text");

        var grid = new Grid { Margin = new Thickness(26, 0, 26, 24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { Height = 64 };
        head.Children.Add(new TextBlock
        {
            Text = title,
            Style = Application.Current?.TryFindResource("H2") as Style,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Style = Application.Current?.TryFindResource("WinBtnClose") as Style,
            Width = 36,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (s, e) => win.Close();
        head.Children.Add(close);

        var cap = new TextBlock
        {
            Text = label,
            Style = Application.Current?.TryFindResource("Txt2") as Style,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(cap, 1);
        Grid.SetRow(lb, 2);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var btOk = new Button
        {
            Content = "确定",
            Style = Application.Current?.TryFindResource("BtnAccent") as Style,
            MinWidth = 100,
            IsDefault = true
        };
        var btNo = new Button
        {
            Content = "取消",
            Style = Application.Current?.TryFindResource("BtnGlass") as Style,
            MinWidth = 100,
            Margin = new Thickness(10, 0, 0, 0),
            IsCancel = true
        };
        btOk.Click += (s, e) => { pick = lb.SelectedItem as string; win.Close(); };
        btNo.Click += (s, e) => win.Close();
        lb.MouseDoubleClick += (s, e) => { pick = lb.SelectedItem as string; win.Close(); };
        row.Children.Add(btOk);
        row.Children.Add(btNo);

        grid.Children.Add(head);
        grid.Children.Add(cap);
        grid.Children.Add(lb);
        grid.Children.Add(row);
        Grid.SetRow(row, 3);

        win.Content = new Border
        {
            BorderBrush = Application.Current?.TryFindResource("Brush.GlassBorder") as Brush,
            BorderThickness = new Thickness(1),
            Child = grid
        };

        win.Loaded += (s, e) =>
        {
            (Application.Current?.MainWindow as MainWindow)?.SetDim(true);
            if (lb.Items.Count > 0) lb.SelectedIndex = 0;
            lb.Focus();
        };
        win.Closed += (s, e) => (Application.Current?.MainWindow as MainWindow)?.SetDim(false);
        win.ShowDialog();
        return pick;
    }
}
