using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ServerUI.Core;
using ServerUI.Services;

namespace ServerUI.Views;

/*
 * 设置页后台 — 目录定位 / 外观 / 目录与环境
 *
 * 三块设置都是"改完即时生效"的:
 *   · 目录定位   —— 写进启动器自己的配置文件 (LauncherSettings);
 *   · 外观       —— 立即改令牌/背景, 不落 config.cfg 那种东西;
 *   · 目录与环境 —— 账号密码即时保存, 环境信息只读。
 *
 * 【参考项目(60US)里的 画面 / 字体 / 性能 三个分区已经删掉】
 * 它们读写的是客户端 config.cfg 与 d3d8.dll 能力探测, ACT5 客户端两样都没有 ——
 * 留着就是"滑杆能动、点了保存没反应"的假控件。ACT5 的画质在游戏内的设置界面里改。
 */
public partial class SettingsView : UserControl, MainWindow.IViewRefresh
{
    bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            LoadAppearance();
            LoadJava();
            LoadAccount();
            LoadEnv();
            _loading = false;
            Nav_Changed(null, null);
        };
    }

    // =================================================================
    // 分区导航
    // =================================================================
    void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (Pn0 == null) return;
        Pn0.Visibility = RbG0.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Pn1.Visibility = RbG1.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Pn2.Visibility = RbG2.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // =================================================================
    // 目录定位
    // =================================================================
    async void LocSrv_Click(object sender, RoutedEventArgs e)
    {
        await Flows.LocateServerRoot();
        LoadEnv();
    }

    async void LocCli_Click(object sender, RoutedEventArgs e)
    {
        await Flows.LocateClientDir();
        LoadEnv();
    }

    async void AutoFind_Click(object sender, RoutedEventArgs e)
    {
        AppSys.Log(">>> 正在按 EXE 位置重新搜索服务端与客户端目录...", "blue");
        AppSys.ServerDir = ServerControl.ResolveServerDir(AppSys.ServerDir);
        AppSys.ClientDir = ServerControl.ResolveClientDir(AppSys.ServerDir);
        AppSys.Settings.ServerDir = AppSys.ServerDir;
        AppSys.Settings.ClientDir = AppSys.ClientDir;
        AppSys.SaveSettings();
        if (ServerControl.IsValidRoot(AppSys.ServerDir)) AppSys.Log(">>> 服务端目录: " + AppSys.ServerDir, "ok");
        else AppSys.Log("[提示] 没找到服务端目录，请手工指定。", "warn");
        if (ServerControl.IsValidClient(AppSys.ClientDir)) AppSys.Log(">>> 客户端目录: " + AppSys.ClientDir, "ok");
        else AppSys.Log("[提示] 没找到客户端目录，请手工指定。", "warn");
        LoadEnv();
        AppSys.RefreshStatus();
        await System.Threading.Tasks.Task.CompletedTask;
    }

    void OpenExeDir_Click(object sender, RoutedEventArgs e) => Ui.OpenDir(AppContext.BaseDirectory);
    void OpenSrv_Click(object sender, RoutedEventArgs e)
    {
        if (ServerControl.IsValidRoot(AppSys.ServerDir)) Ui.OpenDir(AppSys.ServerDir);
        else AppSys.Log("还没定位服务端目录。", "warn");
    }
    void OpenCli_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(AppSys.ClientDir) && Directory.Exists(AppSys.ClientDir)) Ui.OpenDir(AppSys.ClientDir);
        else AppSys.Log("还没定位客户端目录。", "warn");
    }
    void OpenBak_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ArchiveService.Root); } catch { }
        Ui.OpenDir(ArchiveService.Root);
    }
    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppSys.LauncherLogDir); } catch { }
        Ui.OpenDir(AppSys.LauncherLogDir);
    }

    // =================================================================
    // 外观 (即时生效)
    // =================================================================
    void LoadAppearance()
    {
        _loading = true;
        SldOverlay.Value = AppSys.Settings.UiOverlay;
        SldScrim.Value = AppSys.Settings.UiScrimOpacity;
        SldBlur.Value = AppSys.Settings.UiGlassBlur;
        SldOpac.Value = AppSys.Settings.UiGlassOpacity;
        SldFusion.Value = AppSys.Settings.UiArtFusion;
        _loading = false;
        LblOv.Text = (int)(AppSys.Settings.UiOverlay * 100) + "%";
        LblScrim.Text = (int)(AppSys.Settings.UiScrimOpacity * 100) + "%";
        LblBlur.Text = (int)AppSys.Settings.UiGlassBlur + " px";
        LblOpac.Text = (int)(AppSys.Settings.UiGlassOpacity * 100) + "%";
        LblFu.Text = (int)(AppSys.Settings.UiArtFusion * 100) + "%";
    }

    void SldOverlay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        AppSys.Settings.UiOverlay = e.NewValue;
        AppSys.SaveSettings();
        if (LblOv != null) LblOv.Text = (int)(e.NewValue * 100) + "%";
        AppSys.FireOverlay();
    }

    void SldScrim_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        AppSys.Settings.UiScrimOpacity = e.NewValue;
        AppSys.SaveSettings();
        if (LblScrim != null) LblScrim.Text = (int)(e.NewValue * 100) + "%";
        AppSys.ReAdapt();
    }

    void SldBlur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        AppSys.Settings.UiGlassBlur = e.NewValue;
        AppSys.SaveSettings();
        if (LblBlur != null) LblBlur.Text = (int)e.NewValue + " px";
        AppSys.FireArtChanged();
    }

    void SldOpac_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        AppSys.Settings.UiGlassOpacity = e.NewValue;
        AppSys.SaveSettings();
        if (LblOpac != null) LblOpac.Text = (int)(e.NewValue * 100) + "%";
        AppSys.ReAdapt();
    }

    /* 原画融合度: 只缩放"染色量", 不动 Tone 表 —— 任何档位下对比度都达标 */
    void SldFusion_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        AppSys.Settings.UiArtFusion = e.NewValue;
        AppSys.SaveSettings();
        if (LblFu != null) LblFu.Text = (int)(e.NewValue * 100) + "%";
        AppSys.ReAdapt();
    }

    void ImportBg_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickImages();
        if (files != null && files.Length > 0)
            (Window.GetWindow(this) as MainWindow)?.ImportBackgrounds(files);
    }

    void OpenBgDir_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppSys.BgDir); } catch { }
        Ui.OpenDir(AppSys.BgDir);
    }

    void ResetBg_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm("要清空导入的背景图、恢复出厂主视觉吗？\n\n（configs\\背景\\ 里的图片文件会一起删掉）",
                "恢复出厂背景") != MessageBoxResult.Yes) return;
        try
        {
            if (Directory.Exists(AppSys.BgDir))
                foreach (var f in Directory.GetFiles(AppSys.BgDir))
                    try { File.Delete(f); } catch { }
            AppSys.Settings.UiBackgrounds.Clear();
            AppSys.Settings.UiBgIndex = 0;
            AppSys.SaveSettings();
            AppSys.FireBackgrounds();
            AppSys.Log(">>> 已恢复出厂背景。", "ok");
        }
        catch (Exception ex) { AppSys.Log("[背景] 恢复失败: " + ex.Message, "err"); }
    }

    // =================================================================
    // 进游戏的账号 / 密码 (启动器自己的设置)
    // =================================================================
    void LoadAccount()
    {
        _loading = true;
        TxtAccount.Text = AppSys.Settings.Account ?? LauncherSettings.DefaultAccount;
        TxtPassword.Text = AppSys.Settings.Password ?? LauncherSettings.DefaultPassword;
        _loading = false;
        UpdateAcctTips();
    }

    void UpdateAcctTips()
    {
        if (LblAcctTip == null) return;
        var a = (TxtAccount.Text ?? "").Trim();
        var p = (TxtPassword.Text ?? "").Trim();
        LblAcctTip.Text = a.Length == 0 ? "不能为空" : "客户端命令行第 4 段";
        LblPwdTip.Text = p.Length == 0 ? "不能为空" : "客户端命令行第 5 段";
        if (BtSaveAcct != null)
            BtSaveAcct.IsEnabled = a.Length > 0 && p.Length > 0
                && !(a == (AppSys.Settings.Account ?? "") && p == (AppSys.Settings.Password ?? ""));
    }

    void Acct_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        UpdateAcctTips();
    }

    void SaveAcct_Click(object sender, RoutedEventArgs e)
    {
        var a = (TxtAccount.Text ?? "").Trim();
        var p = (TxtPassword.Text ?? "").Trim();
        if (a.Length == 0 || p.Length == 0) { Ui.Warn("账号和密码都不能留空。", "账号未填"); return; }
        AppSys.Settings.Account = a;
        AppSys.Settings.Password = p;
        AppSys.Settings.Save();
        UpdateAcctTips();
        AppSys.Log(">>> 进游戏的账号已改为 " + a + "（下次进游戏生效）", "ok");
        Ui.Info("已保存。下次点【开始游戏】就用这个账号连服务端。\n\n库里没有这个账号时，服务端会替你建一个 —— 那是全新的角色列表。",
                "账号已保存");
    }

    // =================================================================
    // Java 运行环境
    // =================================================================
    void LoadJava()
    {
        if (LblJava == null) return;
        string err;
        var j = ServerControl.FindJava(out err);
        if (!string.IsNullOrEmpty(j))
        {
            LblJava.Text = ServerControl.JavaVersionText + "　·　" + j;
            LblJava.Foreground = Res("Brush.Text");
        }
        else
        {
            LblJava.Text = "未找到 —— 服务端起不来（需要 Java ≥ 25）";
            LblJava.Foreground = Res("Brush.Warn");
        }
        var msi = ServerControl.FindJavaInstaller();
        LblJavaMsi.Text = string.IsNullOrEmpty(msi)
            ? "没找到（把 JDK 安装包放进包里，或自行安装 Java 25）"
            : msi;
        if (BtInstallJava != null)
            BtInstallJava.IsEnabled = !string.IsNullOrEmpty(msi) || string.IsNullOrEmpty(j);
    }

    System.Windows.Media.Brush Res(string key)
        => TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;

    async void InstallJava_Click(object sender, RoutedEventArgs e)
    {
        await Flows.EnsureJavaAsync();   // 缺 Java 就"找安装包 → 问一句 → 装 → 复检"
        ServerControl.InvalidateJavaInstaller();
        LoadJava();
        LoadEnv();
    }

    void RecheckJava_Click(object sender, RoutedEventArgs e)
    {
        ServerControl.InvalidateJava();
        ServerControl.InvalidateJavaInstaller();
        LoadJava();
        LoadEnv();
        AppSys.Log(">>> 已重新检测 Java：" + LblJava.Text, "ok");
    }

    // =================================================================
    // 环境信息
    // =================================================================
    void LoadEnv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("启动器版本：" + AppSys.AppName + " v" + AppSys.Ver + FrameworkLabel());
        sb.AppendLine("启动器路径：" + Compat.ExePath());
        sb.AppendLine("服务端目录：" + (ServerControl.IsValidRoot(AppSys.ServerDir) ? AppSys.ServerDir : "未定位"));
        sb.AppendLine("客户端目录：" + (ServerControl.IsValidClient(AppSys.ClientDir) ? AppSys.ClientDir : "未定位"));
        sb.AppendLine("进游戏的账号：" + (AppSys.Settings.Account ?? "") + " / " + (AppSys.Settings.Password ?? ""));
        sb.AppendLine("使用协议：" + (AppSys.Settings.AgreementAccepted
            ? "已接受"
            : "未接受（下次启动会要求逐字输入确认语；删掉本目录的配置文件可重新确认）"));
        sb.AppendLine("存档文件：" + (ServerControl.IsValidRoot(AppSys.ServerDir) ? ServerControl.DbPath(AppSys.ServerDir) : "--"));
        sb.AppendLine("存档备份目录：" + ArchiveService.Root);
        sb.AppendLine("运行数据目录：" + AppSys.RuntimeDir);
        sb.AppendLine("操作系统：" + Environment.OSVersion.VersionString);
        sb.AppendLine(".NET 运行时：" + Environment.Version);
        string jerr;
        var jp = ServerControl.FindJava(out jerr);
        sb.AppendLine("Java 25：" + (string.IsNullOrEmpty(jp) ? "未找到（服务端起不来）" : ServerControl.JavaVersionText + "　" + jp));
        var msiHint = ServerControl.FindJavaInstaller();
        sb.AppendLine("Java 安装包：" + (string.IsNullOrEmpty(msiHint) ? "包里没找到" : msiHint));
        sb.AppendLine("服务端端口：" + ServerControl.DefaultGamePort + "（游戏） / "
                    + ServerControl.DefaultGmPort + "（网页 GM）");
        var art = AppSys.Art;
        sb.AppendLine("原画配色：主色相 " + (int)art.SourceHue + "° · 彩度 " + (int)art.SourceChroma
                    + " · 融合度 " + (int)(AppSys.Settings.UiArtFusion * 100) + "%"
                    + (art.Valid ? "" : "（还没分析过背景图，当前用主题默认配色）"));
        sb.AppendLine("标题带/底部带：" + (art.Valid
            ? (art.TopIsDark ? "偏暗→浅色字" : "偏亮→深色字") + " / " + (art.BottomIsDark ? "偏暗→浅色字" : "偏亮→深色字")
            : "--"));
        LblEnv.Text = sb.ToString();

        LblSrv.Text = ServerControl.IsValidRoot(AppSys.ServerDir) ? AppSys.ServerDir : "未定位";
        LblCli.Text = ServerControl.IsValidClient(AppSys.ClientDir)
            ? AppSys.ClientDir
            : (string.IsNullOrEmpty(AppSys.ClientDir) ? "未定位" : AppSys.ClientDir + "（里面没有 DNF.exe）");
    }

    /// <summary>分发形态标注 (两种目标框架的 exe 体积与启动速度差很多, 排障时要一眼看出跑的是哪个)</summary>
    static string FrameworkLabel()
    {
#if NET48
        return "（.NET Framework 4.8 · 单文件约 1.8 MB · 首次弹窗要现场 JIT, 约 2.5s 起）";
#else
        return "（.NET 10 · 自包含单文件 · 约 1.4s 出窗口）";
#endif
    }

    public void Refresh()
    {
        LoadAppearance();
        LoadJava();
        LoadAccount();
        LoadEnv();
    }
}
