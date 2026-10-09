using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ServerUI.Core;
using ServerUI.Services;

namespace ServerUI.Views;

/*
 * 首页后台 — 状态轮询刷新 / 两标签切换 / 开始游戏 / 网页 GM / 背景导入
 */
public partial class HomeView : UserControl, MainWindow.IHomeHooks, MainWindow.IViewRefresh
{
    bool _ready;
    bool _hooked;

    public HomeView()
    {
        InitializeComponent();
        /*
         * 【订阅要在每次 Loaded 都做, 不能"只做一次"】
         * 这里踩过一个很难看的坑: 原来 Loaded 开头是 `if (_ready) return;`, 于是在
         * Unloaded 里退掉的订阅【再也回不来】—— 切到别的页面再切回首页之后,
         * 首页就不再听 ArtChanged 了。表现: 换主题 / 换背景图时, 两块浮动卡的底色
         * 停在旧主题(那是 Paint() 写进去的局部值), 而文字颜色跟着令牌走了 ——
         * 浅色主题下变成"深色卡片 + 深色文字", 整块读不出来 (用户 2026-10-08 报的就是这个)。
         * 现在: 订阅/退订严格配对, 一次性初始化只做一次。
         */
        Loaded += (s, e) =>
        {
            if (!_hooked)
            {
                _hooked = true;
                AppSys.StatusChanged += OnStatus;
                AppSys.ArtChanged += ScheduleSurfaces;
                SizeChanged += (a, b) => ScheduleSurfaces();
            }
            if (!_ready)
            {
                _ready = true;
                ApplyTabs();
            }
            OnStatus();
            // 重新进页面时按"当前主题/当前背景"重画一次 (别沿用上次离开时的底色)
            ApplySurfaces();
        };
        Unloaded += (s, e) =>
        {
            if (!_hooked) return;
            _hooked = false;
            AppSys.StatusChanged -= OnStatus;
            AppSys.ArtChanged -= ScheduleSurfaces;
        };
    }

    // =================================================================
    // 毛玻璃: 面板正下方那片背景经 VisualBrush 采进来 + 高斯模糊
    // =================================================================
    System.Windows.Threading.DispatcherTimer _surfaceTimer;
    bool _glassRetry;   // 模板名字域没就绪时补一次重试 (见 Glass)

    void ScheduleSurfaces()
    {
        if (_surfaceTimer == null)
        {
            _surfaceTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(140) };
            _surfaceTimer.Tick += (a, b) => { _surfaceTimer.Stop(); ApplySurfaces(); };
        }
        _surfaceTimer.Stop();      // 拖动尺寸/拖滑杆时防抖
        _surfaceTimer.Start();
    }

    void ApplySurfaces()
    {
        try
        {
            if (Window.GetWindow(this) is not MainWindow mw) return;
            // 原画局部取色: 两块浮动面板各自按"自己正下方那片原画"上色
            Paint(CardInfo, mw.SampleUnder(CardInfo));
            Paint(CardDeck, mw.SampleUnder(CardDeck));
            Glass(CardInfo, mw);
            Glass(CardDeck, mw);
        }
        catch { }
    }

    /* 面板底色/描边按该面板正下方那片原画来 (MakeSurface 内部保证与面板的对比度) */
    void Paint(System.Windows.Controls.ContentControl card, ArtProfile p)
    {
        if (card == null || p == null || !p.Valid) return;
        var set = ArtPalette.MakeSurface(p, AppSys.IsDark, AppSys.Settings.UiArtFusion);
        card.Background = set.Fill;
        card.BorderBrush = set.Edge;
    }

    void Glass(System.Windows.Controls.ContentControl card, MainWindow mw)
    {
        if (card == null) return;
        card.ApplyTemplate();
        if (card.Template == null) return;
        var rect = card.Template.FindName("Backdrop", card) as System.Windows.Shapes.Rectangle;
        var brush = card.Resources["CardBackdrop"] as VisualBrush;
        if (rect == null || brush == null)
        {
            // 首次 Loaded 时模板的名字域可能还没就绪 (ApplyTemplate 返回 false),
            // 这一轮拿不到 Backdrop。以前就直接 return 了 —— 于是"首帧的卡片"和
            // "稳定态的卡片"长得不一样 (浅色下表现为卡片忽明忽暗)。这里补一次延迟重试。
            if (!_glassRetry)
            {
                _glassRetry = true;
                Dispatcher.BeginInvoke(new Action(ScheduleSurfaces),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            return;
        }

        double blur = AppSys.Settings.UiGlassBlur;
        if (blur <= 0.5)
        {
            rect.Fill = System.Windows.Media.Brushes.Transparent;
            rect.Effect = null;
            rect.Opacity = 1;
            return;
        }
        /*
         * 【主题决定毛玻璃"透多少原画"】
         * Backdrop 这一层画的是"面板正下方那片背景"的实时模糊采样; 玻璃层
         * (Brush.GlassGrad) 盖在它上面。深色主题下原画本身是深的, 让采样层
         * 全强度透上来才有玻璃感; 但浅色主题下这就是一个无解的坑 ——
         * 深色原画被原样铺在卡片底层, 玻璃层再白也压不住, 卡片直接变一块黑,
         * 而浅色主题的文字是深的 → 整块读不出来 (2026-10-08 实测)。
         * 所以浅色下把采样层压到 22%: 只留一点原画的色温, 卡片仍是暖瓷白。
         */
        rect.Opacity = AppSys.IsDark ? 1.0 : 0.22;
        brush.Visual = mw.BackdropVisual;
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        if (mw.VisualRectFor(card, blur * 2, out var r))
        {
            brush.ViewboxUnits = BrushMappingMode.Absolute;
            brush.Viewbox = r;
        }
        rect.Fill = brush;
        if (rect.Effect is BlurEffect be) be.Radius = blur;
        else rect.Effect = new BlurEffect
        {
            Radius = blur,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };
    }

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.White;

    // =================================================================
    // 状态刷新 (每 2 秒)
    // =================================================================
    void OnStatus()
    {
        if (!_ready) return;
        var L = AppSys.Last;

        // 1. 服务端
        SetDot(D1, L.Srv ? DotState.Ok : DotState.Bad);
        V1.Text = L.Srv ? "运行中" : "未运行";

        // 2. 服务端端口 (游戏端口 / 网页GM端口) —— 只在服务端【未运行】时显示
        if (RowPort != null)
        {
            var portVis = L.Srv ? Visibility.Collapsed : Visibility.Visible;
            RowPort.Visibility = portVis;
            RowPortRow.Height = L.Srv ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            SepPortRow.Height = L.Srv ? new GridLength(0) : new GridLength(1);
            SetDot(D7, L.Srv ? DotState.Ok : DotState.Idle);
            V7.Text = L.PortText;
        }

        // 3. 存档数据库
        var dbBad = (L.DbText ?? "").Contains("未创建") || (L.DbText ?? "").Contains("未定位");
        SetDot(D2, dbBad ? DotState.Warn : DotState.Ok);
        V2.Text = L.DbText;

        // 4. 游戏客户端
        SetDot(D3, !L.ClOk ? DotState.Bad : DotState.Ok);
        V3.Text = L.ClText;

        // 5. A5.dll 客户端补丁 (没有它杀怪不上报)
        var a5Ok = L.OverlayText == "已部署";
        SetDot(D4, a5Ok ? DotState.Ok : DotState.Warn);
        V4.Text = L.OverlayText;
        if (LblA5State != null)
        {
            LblA5State.Text = a5Ok ? "已部署 —— 击杀会上报，怪物可以正常打死"
                                   : "未部署 —— 杀怪不上报，怪物打不死";
            LblA5State.Foreground = a5Ok ? Res("Brush.Text2") : Res("Brush.Warn");
        }

        // 6. Script.pvf
        SetDot(D5, !L.ClOk ? DotState.Idle : (L.PvfOk ? DotState.Ok : DotState.Warn));
        V5.Text = L.PvfText;

        // 7. 网页 GM
        SetDot(D6, L.Gm ? DotState.Ok : DotState.Idle);
        V6.Text = L.Gm ? "运行中" : "未运行";
        if (LbGmSub != null)
        {
            LbGmSub.Text = L.Gm ? "运行中" : "未运行";
            LbGmSub.Foreground = L.Gm ? Res("Brush.Accent") : Res("Brush.Text3");
        }

        if (BtGm != null)
        {
            BtGm.ToolTip = "打开网页 GM（" + "http://127.0.0.1:" + AppSys.Settings.WebGmPort + "）\n"
                + "· 查询角色 / 道具 / 技能：随时可用\n"
                + "· 发道具 / 调等级 / 改背包（写库）：服务端运行时被拒绝 —— 先停止服务端，写完再启动\n"
                + "当前端口：" + L.PortText;
        }

        BtStopRound.Visibility = L.Srv ? Visibility.Visible : Visibility.Collapsed;
    }

    /* 状态点: 颜色 + 同色辉光 + 运行中呼吸 */
    enum DotState { Ok, Warn, Bad, Idle }

    void SetDot(System.Windows.Shapes.Ellipse dot, DotState st)
    {
        Brush b;
        switch (st)
        {
            case DotState.Ok: b = Res("Brush.Accent"); break;
            case DotState.Warn: b = Res("Brush.Warn"); break;
            case DotState.Bad: b = Res("Brush.Danger"); break;
            default: b = Res("Brush.Text3"); break;
        }
        dot.Fill = b;
        if (dot.Effect is DropShadowEffect e && b is SolidColorBrush sb) e.Color = sb.Color;

        if (st == DotState.Ok)
        {
            var a = new System.Windows.Media.Animation.DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                }
            };
            /*
             * 呼吸灯是【无限循环】动画 —— 默认它会以 60fps 一直改值, 于是首页
             * 静止时也一直在重绘。实测首页 23.7% 一个核、切到没有状态点的说明页
             * 只有 3.7% (2026-10-08 量的)。1.1 秒一个来回的呼吸根本看不出 60fps
             * 与 12fps 的差别, 所以把帧率压到 12 —— 观感不变, 重绘开销约 1/5。
             */
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(a, 12);
            dot.BeginAnimation(OpacityProperty, a);
        }
        else
        {
            dot.BeginAnimation(OpacityProperty, null);
            dot.Opacity = 1;
        }
    }

    public void Refresh() { OnStatus(); ApplySurfaces(); }

    // =================================================================
    // 标签切换
    // =================================================================
    void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyTabs();
    }

    void ApplyTabs()
    {
        PnSt.Visibility = TabSt.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PnQk.Visibility = TabQk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // =================================================================
    // 指挥台动作
    // =================================================================
    async void Start_Click(object sender, RoutedEventArgs e)
    {
        /*
         * 【开始游戏 = 服务端 → 进游戏。不碰网页 GM】(2026-10-08 按用户要求改)
         * 以前这里会先把网页 GM 拉起来再进游戏。现在不主动开 —— 它是给玩家改数据用的
         * 独立工具, 想用点【GM 工具】自己开。一键里少一个进程, 进游戏也少一步等待。
         */
        AppSys.Log(">>> 点击了开始游戏（服务端 → 进游戏）", "blue");
        await Flows.Play();
        OnStatus();
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        AppSys.Log(">>> 点击了停止服务端", "gold");
        await Flows.StopServer();
        OnStatus();
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        AppSys.Log(">>> 点击了重启服务端", "gold");
        await Flows.RestartServer();
        OnStatus();
    }

    async void Gm_Click(object sender, RoutedEventArgs e)
    {
        AppSys.Log(">>> 点击了 GM 工具", "gold");
        await Flows.OpenGmTool();
        OnStatus();
    }

    // =================================================================
    // 快捷
    // =================================================================
    void OpenServerDir_Click(object sender, RoutedEventArgs e)
    {
        var d = AppSys.ServerDir;
        if (ServerControl.IsValidRoot(d)) { Ui.OpenDir(d); AppSys.Log(">>> 已打开服务端目录: " + d, "ok"); }
        else
        {
            AppSys.Log("还没定位服务端目录。", "warn");
            Ui.Info("还没定位到服务端目录。\n\n请到【设置 → 目录定位】指定能看到 server\\s1a5-*-all.jar 的那一层。", "服务端目录");
        }
    }

    void OpenClientDir_Click(object sender, RoutedEventArgs e)
    {
        var d = AppSys.ClientDir;
        if (ServerControl.IsValidClient(d) || (Directory.Exists(d) && !string.IsNullOrEmpty(d)))
        { Ui.OpenDir(d); AppSys.Log(">>> 已打开客户端目录: " + d, "ok"); }
        else
        {
            AppSys.Log("还没定位客户端目录。", "warn");
            Ui.Info("还没定位到游戏客户端目录。\n\n客户端目录就是能看到 DNF.exe、ImagePacks2 的那一层。\n"
                  + "可以到【设置 → 目录定位】指定，或直接点【开始游戏】让它自动搜索。", "客户端目录");
        }
    }

    void OpenDbDir_Click(object sender, RoutedEventArgs e)
    {
        var d = ServerControl.DbPath(AppSys.ServerDir);
        var dir = Path.GetDirectoryName(d);
        if (ServerControl.IsValidRoot(AppSys.ServerDir) && Directory.Exists(dir))
        { Ui.OpenDir(dir); AppSys.Log(">>> 已打开存档目录: " + dir, "ok"); }
        else AppSys.Log("存档目录不存在（还没定位服务端目录）。", "warn");
    }

    void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        var d = AppSys.ServerDir;
        if (ServerControl.IsValidRoot(d))
        {
            var dir = ServerControl.LogDir(d);
            try { Directory.CreateDirectory(dir); } catch { }
            Ui.OpenDir(dir);
            AppSys.Log(">>> 已打开服务端日志目录: " + dir, "ok");
        }
        else
        {
            try { Directory.CreateDirectory(AppSys.LauncherLogDir); } catch { }
            Ui.OpenDir(AppSys.LauncherLogDir);
            AppSys.Log(">>> 已打开启动器日志目录: " + AppSys.LauncherLogDir, "ok");
        }
    }

    void OpenBackupDir_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ArchiveService.Root); } catch { }
        Ui.OpenDir(ArchiveService.Root);
        AppSys.Log(">>> 已打开备份目录: " + ArchiveService.Root, "ok");
    }

    void ImportBg_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickImages();
        if (files != null && files.Length > 0)
            (Window.GetWindow(this) as MainWindow)?.ImportBackgrounds(files);
    }

    // =================================================================
    // 主窗口钩子 (IHomeHooks)
    // =================================================================
    System.Windows.Media.Animation.DoubleAnimation _spin;

    public void SetProgress(double pv, bool vis)
    {
        if (vis)
        {
            BtStart.IsEnabled = false;
            StartTxt.Text = "处理中 " + (int)pv + "%";
            StartIcon.Visibility = Visibility.Collapsed;
            Spinner.Visibility = Visibility.Visible;
            if (_spin == null)
                _spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
            SpinRt.BeginAnimation(RotateTransform.AngleProperty, _spin);
        }
        else
        {
            BtStart.IsEnabled = true;
            StartTxt.Text = "开始游戏";
            StartIcon.Visibility = Visibility.Visible;
            Spinner.Visibility = Visibility.Collapsed;
            SpinRt.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    public void OnBackgroundsChanged() { /* 背景层由主窗口负责 */ }
}
