using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerUI.Core;
using ServerUI.Services;

namespace ServerUI.Views;

/*
 * 存档管理后台 — 备份 / 还原 / 重命名 / 删除 / 导入 / 重新开局
 *
 * 所有写操作都先过 ArchiveService.CanWrite: 服务端没停、或 GM 正持锁时
 * 一律拒绝, 并把原因显示在顶部提示条上 —— 宁可让玩家多点一次停止服务端,
 * 也不给一个可能损坏的存档。
 */
public partial class ArchiveView : UserControl, MainWindow.IViewRefresh
{
    ArchiveEntry _sel;
    bool _hooked;

    public ArchiveView()
    {
        InitializeComponent();
        // 订阅/退订严格配对 —— 切页面会 Unloaded→Loaded, 重复订阅会让每次状态
        // 刷新做两遍 (首页同款处理, 见 HomeView 构造函数里的注释)
        Loaded += (s, e) =>
        {
            if (!_hooked) { _hooked = true; AppSys.StatusChanged += OnStatus; }
            Reload();
        };
        Unloaded += (s, e) =>
        {
            if (!_hooked) return;
            _hooked = false;
            AppSys.StatusChanged -= OnStatus;
        };
    }

    public void Refresh() => Reload();

    void OnStatus() => UpdateBars();

    void Reload()
    {
        var list = ArchiveService.List(AppSys.ServerDir);
        Lst.ItemsSource = list;
        LbCount.Text = "共 " + list.Count + " 份";
        if (_sel != null)
        {
            // 尽量保持选中 (刷新后重建了对象)
            ArchiveEntry keep = null;
            foreach (var e in list)
                if (e.Name == _sel.Name && e.IsGmBackup == _sel.IsGmBackup) { keep = e; break; }
            Lst.SelectedItem = keep;
            _sel = keep;
        }
        UpdateBars();
        UpdateSelectionUi();
    }

    void UpdateBars()
    {
        // 当前存档读数
        if (ServerControl.IsValidRoot(AppSys.ServerDir))
        {
            var db = ServerControl.ReadDbInfo(AppSys.ServerDir);
            LbCur.Text = db.Exists
                ? db.Text + "\n" + ServerControl.DbPath(AppSys.ServerDir)
                : "还没有存档。\n\n先启动一次服务端，它会自动建库。";
        }
        else LbCur.Text = "还没定位服务端目录。";

        // 顶部提示条: 能不能写
        string why;
        var can = ArchiveService.CanWrite(AppSys.ServerDir, out why);
        if (can)
        {
            WarnTitle.Text = "现在可以备份 / 还原（服务端已停止）";
            WarnText.Text = "存档是三件套，必须一起备份：s1a5.db（主库）+ -wal（预写日志）+ -shm（共享内存索引）。"
                          + "只拷主库、或边运行边拷，会得到残缺的库 —— 下次启动直接报 “malformed database schema ... orphan index”，存档就白拷了。";
            WarnIcon.Stroke = Res("Brush.Accent");
        }
        else
        {
            WarnTitle.Text = "现在不能备份 / 还原";
            WarnText.Text = (why ?? "").Replace("\n", " ") + "   （三件套必须一起拷：主库 + -wal + -shm。）";
            WarnIcon.Stroke = Res("Brush.Warn");
        }
    }

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.White;

    void Lst_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sel = Lst.SelectedItem as ArchiveEntry;
        UpdateSelectionUi();
    }

    void UpdateSelectionUi()
    {
        var e = _sel;
        if (e == null)
        {
            LbSel.Text = "在上面的列表里选一份备份。";
            BtRestore.IsEnabled = BtRename.IsEnabled = BtDelete.IsEnabled = false;
            return;
        }
        LbSel.Text = e.Name + "\n\n"
                   + "来源：" + e.SourceText + "\n"
                   + "时间：" + e.TimeText + "\n"
                   + "大小：" + e.SizeText
                   + (string.IsNullOrEmpty(e.Note) ? "" : "\n备注：" + e.Note)
                   + (e.Valid ? "" : "\n\n⚠ 这份备份不完整，不能用。");
        BtRestore.IsEnabled = e.Valid;
        BtRename.IsEnabled = !e.IsGmBackup;
        BtDelete.IsEnabled = !e.IsGmBackup;
    }

    // =================================================================
    // 操作
    // =================================================================
    async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await Flows.BackupArchive();
        Reload();
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        await Flows.RestoreArchive(_sel);
        Reload();
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        var name = Ui.PromptText("重命名备份", "新的名字", _sel.Name,
            "名字里不能有 \\ / : * ? \" < > | 这些字符。");
        if (name == null || name == _sel.Name) return;
        string err;
        if (!ArchiveService.Rename(_sel, name, out err)) { Ui.Err("重命名失败：\n\n" + err, "失败"); return; }
        AppSys.Log(">>> 备份已重命名为: " + name, "ok");
        Reload();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        if (Ui.Confirm("要删掉这份备份吗？\n\n  " + _sel.Name + "\n  " + _sel.TimeText + " · " + _sel.SizeText
                     + "\n\n删除后不能恢复。", "删除备份") != MessageBoxResult.Yes) return;
        string err;
        if (!ArchiveService.Delete(_sel, out err)) { Ui.Err("删除失败：\n\n" + err, "失败"); return; }
        AppSys.Log(">>> 已删除备份: " + _sel.Name, "ok");
        _sel = null;
        Reload();
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.PickFile("选择要导入的存档文件（.sqlite）", "SQLite 存档|*.sqlite;*.db|所有文件|*.*");
        if (string.IsNullOrEmpty(f)) return;
        var name = Ui.PromptText("导入存档", "存进备份库时用什么名字", Path.GetFileNameWithoutExtension(f),
            "会把这份文件收进启动器的存档备份目录，之后可以在列表里还原它。");
        if (name == null) return;
        string err;
        var got = ArchiveService.Import(f, name, out err);
        if (got == null) { Ui.Err("导入失败：\n\n" + err, "失败"); return; }
        AppSys.Log(">>> 已导入存档: " + got, "ok");
        Reload();
    }

    async void Reset_Click(object sender, RoutedEventArgs e)
    {
        await Flows.ResetGame();
        Reload();
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        AppSys.Log(">>> 已刷新备份列表。", "gray");
    }

    void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ArchiveService.Root); } catch { }
        Ui.OpenDir(ArchiveService.Root);
    }
}
