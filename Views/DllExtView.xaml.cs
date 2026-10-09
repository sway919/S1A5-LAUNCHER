using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ServerUI.Core;
using ServerUI.Services;

namespace ServerUI.Views;

/*
 * DLL 扩展后台 —— 客户端插件管理 (S1A5 / ACT5 口径)
 * 见 Services/DllExtService.cs 与 Views/DllExtView.xaml 顶部注释。
 *
 * 应用更改 = 复制进客户端目录 + 写进 dll_ext.ini（写后回读校验）；
 * 全部撤下 = 删文件 + 移除条目 + 恢复被覆盖的原文件。
 * 每行显示插件小字说明（四级回退链，见 DllExtService.ResolveMeta）、
 * 【详细】弹出完整说明；拖 ⋮ 手柄排序 = 写进 ini 的条目顺序。
 * A5.dll 那一行是必选补丁: 只检测是否在客户端目录里，不进 ini、不参与挂载。
 */
public partial class DllExtView : UserControl, MainWindow.IViewRefresh
{
    bool _loading = true;
    ObservableCollection<DllExtItem> _items;
    DllExtItem _dragItem;       // 正在拖拽的行 (由 ⋮ 手柄按下时锁定)
    Point _dragStart;           // 按下点 (超过 4px 才算拖拽, 防误触)
    bool _dragArmed;            // 手柄已按下但还没越过阈值
    bool _reordered;            // 本次拖拽是否真的动过顺序 (防误触落盘)

    public DllExtView()
    {
        InitializeComponent();
        Loaded += (s, e) => Reload();
    }

    public void Refresh() => Reload();

    void Reload()
    {
        _loading = true;
        _dragItem = null;
        _dragArmed = false;
        _reordered = false;
        _items = new ObservableCollection<DllExtItem>(DllExtService.List());
        Lst.ItemsSource = _items;
        LbCount.Text = "共 " + _items.Count + " 个";
        LbDir.Text = "扩展目录：" + DllExtService.Dir;

        // 勾选状态由模板里的 IsChecked="{Binding Enabled, Mode=TwoWay}" 自己维持 ——
        // 不再靠"遍历容器找开关"那种写法（列表被虚拟化时 ContainerFromIndex 会返回
        // null，那样会静默漏掉几行，表现为"个别开关不显示状态"）。
        _loading = false;

        var deployed = DllExtService.DeployedCount();
        LbState.Text = deployed > 0
            ? "当前有 " + deployed + " 个扩展件挂在客户端里。"
            : "当前没有扩展件挂在客户端里。";
        LbLog.Text = DllExtService.ReadLogSummary(AppSys.ClientDir);
        UpdateA5Row();
    }

    /// <summary>A5.dll 必选行 —— 只检测"在不在客户端目录"，缺失时红字警示（不拦启动）</summary>
    void UpdateA5Row()
    {
        var s = DllExtService.CheckA5(AppSys.ClientDir);
        LbA5.Text = "客户端补丁 · " + s.Text;
        LbA5.Foreground = Res(s.Warn ? "Brush.Danger" : "Brush.Text3");
        LbA5Flag.Text = s.Present ? "已部署" : "缺失";
        LbA5Flag.Foreground = Res(s.Warn ? "Brush.Danger" : "Brush.Text3");
    }

    static System.Windows.Media.Brush Res(string key)
        => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
           ?? System.Windows.Media.Brushes.Gray;

    // ── 拖拽排序：按住 ⋮ 手柄上下拖 = 实时调整顺序, 松开落盘 ──────────────
    // 顺序语义见 DllExtService.SetOrder：清单 Order 表 = 写进 dll_ext.ini 的条目顺序。
    // 用"按下锁定 + MouseMove 实时移动"而不是 DoDragDrop：行数少, 即时反馈更直观,
    // 也不需要插入指示线/AllowDrop 那一套。越过 4px 才算拖拽 (防手抖误排序),
    // 与主窗口标题栏拖动、参考实现的阈值口径一致。

    void Drag_HandleDown(object sender, MouseButtonEventArgs e)
    {
        if (_loading) return;
        var el = (FrameworkElement)sender;
        _dragItem = el.DataContext as DllExtItem ?? el.Tag as DllExtItem;
        if (_dragItem == null) return;
        _dragStart = e.GetPosition(Lst);
        _dragArmed = true;
        _reordered = false;

        // 捕获鼠标: 这样"在列表外面松开"也能收到 MouseUp。
        // 不捕获的话, 拖到列表外松手 → EndDrag 不被调用 → _dragItem 一直挂着,
        // 下一次在列表里按下鼠标就会被当成"还在拖上一行", 顺序莫名其妙地变。
        try { el.CaptureMouse(); } catch { }

        e.Handled = true;   // 手柄不吃行选中, 也别触发别的
    }

    void Lst_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Lst);
        if (_dragArmed)
        {
            if (Math.Abs(p.Y - _dragStart.Y) < 4 && Math.Abs(p.X - _dragStart.X) < 4) return;
            _dragArmed = false;
        }
        var idx = IndexUnder(p);
        if (idx < 0) return;
        var cur = _items.IndexOf(_dragItem);
        if (cur < 0 || idx == cur) return;
        _items.Move(cur, idx);
        _reordered = true;
    }

    void Lst_MouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    void Lst_MouseLeave(object sender, MouseEventArgs e)
    {
        // 捕获期间的 MouseLeave 不代表结束（光标被"借"给了拖拽）——
        // 真正的结束信号是 MouseUp, 由捕获保证一定送到。
        if (e.LeftButton != MouseButtonState.Pressed) EndDrag();
    }

    void EndDrag()
    {
        _dragArmed = false;
        if (_dragItem == null) return;
        _dragItem = null;

        // 松开捕获
        try { Mouse.Capture(null); } catch { }

        if (!_reordered) return;
        _reordered = false;
        DllExtService.SetOrder(NamesInOrder());
        AppSys.Log(">>> 扩展顺序已调整（点【应用更改】时按此顺序写进 dll_ext.ini）。", "gray");
    }

    /// <summary>当前列表顺序的文件名序列（拖拽结束后落盘用）</summary>
    List<string> NamesInOrder()
    {
        var r = new List<string>();
        foreach (var it in _items) r.Add(it.Name);
        return r;
    }

    /// <summary>光标纵向落在哪一行（按行容器中线判定）</summary>
    int IndexUnder(Point p)
    {
        for (int i = 0; i < Lst.Items.Count; i++)
        {
            if (!(Lst.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)) continue;
            var mid = Lst.PointFromScreen(c.PointToScreen(new Point(0, c.ActualHeight / 2)));
            if (p.Y < mid.Y) return i;
        }
        return Lst.Items.Count - 1;
    }

    // ── 详细：弹出完整插件说明 ────────────────────────────────────────────
    void Detail_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as Button)?.Tag as DllExtItem;
        if (item == null) return;
        var w = new DllExtDetailWindow(item) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }

    void A5Detail_Click(object sender, RoutedEventArgs e)
    {
        var s = DllExtService.CheckA5(AppSys.ClientDir);
        Ui.Info(
            "A5.dll —— 客户端补丁（必选，不可关闭）\n\n"
          + "它不是一个扩展插件，而是客户端本身必须的一份补丁：把 A5.dll 挂进游戏的加载链，"
          + "让击杀怪物等事件能上报给服务端。没有它，怪的血打空也不会死。\n\n"
          + "· 它由客户端目录里的 DumpReport.dll（扩展宿主）在启动时一起拉起，"
          + "不写进 dll_ext.ini，也不参与这里的挂载 / 撤下。\n"
          + "· 所以这一行是【显示状态用】的：只检查它在不在客户端目录里，"
          + "状态为“已部署”就是好的。\n"
          + "· 缺失时也不会在这里被拦下 —— 点【开始游戏】时会再提醒一次。\n\n"
          + "当前状态：" + s.Text, "A5.dll（必选补丁）");
    }

    void Sw_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var tb = sender as ToggleButton;
        var item = tb?.Tag as DllExtItem;
        if (item == null) return;

        // 值没变 = 不是玩家在点它, 而是模板/绑定初始化（列表被虚拟化时容器可能
        // 在 _loading 落地之后才创建, 那时会补一次 Checked/Unchecked）。
        // 这种情况下不写库、不记日志, 免得刷出一串假的"已启用/已停用"。
        var want = tb.IsChecked == true;
        if (want == item.Enabled) return;

        string err;
        if (!DllExtService.SetEnabled(item.Name, want, out err))
        {
            Ui.Err("保存启用状态失败：\n\n" + err, "失败");
            // 写失败就把开关退回去, 别让界面显示一个没存下来的状态
            _loading = true;
            tb.IsChecked = item.Enabled;
            _loading = false;
            return;
        }
        item.Enabled = want;
        AppSys.Log(">>> DLL 扩展 " + item.Name + (item.Enabled ? " 已启用" : " 已停用")
                 + "（点【应用更改】后生效）", "gray");
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要收进扩展目录的 DLL（可多选）",
            Filter = "动态链接库|*.dll|所有文件|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        int ok = 0;
        string lastErr = null;
        foreach (var f in dlg.FileNames)
        {
            string err;
            if (DllExtService.Import(f, out err)) ok++;
            else lastErr = Path.GetFileName(f) + "：" + err;
        }
        if (ok > 0) AppSys.Log(">>> 已收进扩展目录 " + ok + " 个 dll（勾选后点【应用更改】挂进客户端）。", "ok");
        if (lastErr != null) Ui.Err("有文件没能导入：\n\n" + lastErr, "导入失败");
        Reload();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as Button)?.Tag as DllExtItem;
        if (item == null) return;
        if (Ui.Confirm("要从扩展目录里删掉这个文件吗？\n\n  " + item.Name
                     + "\n\n（随件的 .plugin.json 说明与排序记录会一并清掉）", "删除扩展件")
            != MessageBoxResult.Yes) return;
        string err;
        if (!DllExtService.Delete(item.Name, AppSys.ClientDir, out err)) { Ui.Err("删除失败：\n\n" + err, "失败"); return; }
        AppSys.Log(">>> 已删除扩展件: " + item.Name, "ok");
        Reload();
    }

    /// <summary>
    /// 部署/撤下前的运行状态闸门。
    /// "确认在跑" → 拦下来；"查不出来" → 也拦（对删除/覆盖来说, 不知道就得当开着）；
    /// 只有"确认没跑"才放行。
    /// </summary>
    bool ClientNotRunningOrWarned(string action)
    {
        switch (Flows.GameClientState())
        {
            case Flows.ClientRunState.Running:
                Ui.Warn("游戏正在运行，这些 dll 被占用，" + action + "会失败或留下半个文件。\n\n请先完全退出游戏。",
                        "请先退出游戏");
                return false;
            case Flows.ClientRunState.Unverifiable:
                Ui.Warn("检测到有 DNF 进程在运行，但读不出它的位置，无法确认是不是这份客户端。\n\n"
                      + "为安全起见，" + action + "不会执行 —— 请在任务管理器里确认没有 DNF.exe 之后重试\n"
                      + "（或者用管理员身份重开启动器）。", "无法确认游戏是否在运行");
                return false;
            default:
                return true;
        }
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ClientNotRunningOrWarned("应用更改")) return;
        if (!ServerControl.IsValidClient(AppSys.ClientDir))
        {
            Ui.Warn("还没定位到客户端目录。\n\n请到【设置 → 目录定位】指定。", "无法应用");
            return;
        }
        string err;
        if (!DllExtService.Deploy(AppSys.ClientDir, m => AppSys.Log(m, "gray"), out err))
        {
            Ui.Err("应用更改失败：\n\n" + err, "失败");
            return;
        }
        AppSys.Log(">>> DLL 扩展已写进客户端（dll_ext.ini 已更新，重启游戏生效）。", "ok");
        Reload();
    }

    void Undeploy_Click(object sender, RoutedEventArgs e)
    {
        if (!ClientNotRunningOrWarned("撤下")) return;
        if (Ui.Confirm("要把已挂载的扩展件从客户端撤下吗？\n\n"
                     + "会删掉复制进去的文件、从 dll_ext.ini 移除条目，并恢复被覆盖的原文件（如有备份）。",
                       "全部撤下") != MessageBoxResult.Yes) return;
        string err;
        if (!DllExtService.Undeploy(AppSys.ClientDir, m => AppSys.Log(m, "gray"), out err))
        {
            Ui.Err("撤下失败：\n\n" + err, "失败");
            return;
        }
        AppSys.Log(">>> DLL 扩展已全部撤下（ini 条目已移除，有备份的原文件已恢复）。", "ok");
        Reload();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(DllExtService.Dir); } catch { }
        Ui.OpenDir(DllExtService.Dir);
    }

    void OpenClientDir_Click(object sender, RoutedEventArgs e)
    {
        if (!ServerControl.IsValidClient(AppSys.ClientDir))
        {
            Ui.Warn("还没定位到客户端目录。\n\n请到【设置 → 目录定位】指定。", "无法打开");
            return;
        }
        DllExtService.OpenClientDir(AppSys.ClientDir);
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (DllExtService.OpenLog(AppSys.ClientDir))
        {
            AppSys.Log(">>> 已在资源管理器中选中客户端的 " + DllExtService.LogName + "。", "gray");
            return;
        }
        if (string.IsNullOrWhiteSpace(AppSys.ClientDir) || !Directory.Exists(AppSys.ClientDir))
        {
            Ui.Warn("还没定位到客户端目录。\n\n请到【设置 → 目录定位】指定。", "打不开日志");
            return;
        }
        Ui.Info("客户端目录里还没有 " + DllExtService.LogName + "。\n\n"
              + "完整启动过一次游戏（扩展宿主跑完加载）之后，它才会出现在客户端目录里。\n\n"
              + "位置：" + DllExtService.LogPath(AppSys.ClientDir), "还没有日志");
    }
}
