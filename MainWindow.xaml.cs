/*
 * ==================================================================
 * 主窗口后台逻辑 (MainWindow.xaml.cs) v2
 * ==================================================================
 * 视图导航(带过渡) / 背景图轮播与导入(含拖拽) / 日志攒批渲染 /
 * 进度平滑蠕动 / 2 秒状态轮询(含呼吸灯) / 明暗主题 / 弹窗压暗 / 退出清理。
 * 窗口按 16:9 设计 (默认 1440×810, 超出屏幕时等比缩放)。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ServerUI.Core;
using ServerUI.Services;

namespace ServerUI;

public partial class MainWindow : Window
{
    /* 首页视图钩子: 进度显示 / 背景列表变化 */
    public interface IHomeHooks
    {
        void SetProgress(double pv, bool vis);
        void OnBackgroundsChanged();
    }

    /* 视图刷新钩子: 每次切入时调用 */
    public interface IViewRefresh { void Refresh(); }

    readonly UIElement[] _cache = new UIElement[5];   // 与 NavKey 的成员数一致 (Home/Archive/Dll/Settings/Help)
    NavKey _cur = (NavKey)(-1);
    bool _navSync;
    bool _closeConfirmed;
    bool _uiReady;

    readonly DispatcherTimer _logTimer, _ptTimer, _stTimer;
    bool _progOn;
    double _pv;

    List<string> _bgFiles = new List<string>();
    System.Windows.Media.Imaging.BitmapSource _bgSrc;   // 当前背景图 (局部取色要用)
    int _bgIdx;
    bool _onA = true;

    bool _logExpanded;
    string _lastLogMsg;
    DoubleAnimation _busySpin;

    public MainWindow()
    {
        AppSys.BootMark("MainWindow 构造开始 (进入 InitializeComponent)");
        InitializeComponent();
        AppSys.BootMark("XAML 解析完成");
        BrandIcon.Source = LoadBrandIcon();
        Fit169();
        _uiReady = true;
        ThemeIcon.Data = (Geometry)FindResource(AppSys.IsDark ? "IcMoon" : "IcSun");
        Shade.Opacity = 0;   // 主页不压暗, 进入内容页时再渐入

        AppSys.StatusChanged += UpdateTitleStatus;
        AppSys.ProgressVisible += OnProgressVis;
        AppSys.RequestNavigate += k => Navigate(k);
        AppSys.OverlayChanged += () => ApplyBgMode(_cur == NavKey.Home);
        AppSys.BackgroundsChanged += () =>
        {
            ReloadBg();
            (ViewHost.Content as IHomeHooks)?.OnBackgroundsChanged();
        };

        _logTimer = Tick(30, FlushLog); _logTimer.Start();
        // 启动 1.5 秒后趁空闲预热说明页 (第一次打开那 ~290ms 的长文本排版先做掉)
        var warm = Tick(1500, () => { });
        warm.Tick += (s, e) =>
        {
            warm.Stop();
            if (_cache[(int)NavKey.Help] is Views.HelpView hv)
                hv.Prewarm(ViewHost.ActualWidth, ViewHost.ActualHeight);
            else if (_cache[(int)NavKey.Help] == null)
            {
                var h2 = (Views.HelpView)(_cache[(int)NavKey.Help] = MakePage(NavKey.Help));
                h2.Prewarm(ViewHost.ActualWidth, ViewHost.ActualHeight);
            }
        };
        warm.Start();
        _ptTimer = Tick(500, PtTick);
        _stTimer = Tick(2000, () => AppSys.RefreshStatus()); _stTimer.Start();

        StateChanged += (s, e) =>
            MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IcRestore" : "IcMax");

        Loaded += (s, e) =>
        {
            AppSys.BootMark("Loaded 进入");
            // 界面先画出来: 背景 + 首页这两步只有 ~200ms, 留在这里首帧才不是空窗
            UpdateTitleStatus();
            ReloadBg();
            AppSys.BootMark("背景就绪");
            Navigate(NavKey.Home);
            AppSys.BootMark("首页建好");

            /*
             * 首次自检 (目录定位 / 读存档 / 找 Java / 刷状态) 实测 ~1 秒,
             * 其中"跑 java -version"和 WMI 查询都是同步等待 —— 以前写在 Loaded 里
             * 直接等, 于是 Show() 要等它跑完才回来, 窗口白等多一秒。
             * 现在丢到 Background 优先级: 首帧先画, 自检随后补上 (日志里看得见)。
             */
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await Flows.AutoSetup(); }
                catch (Exception ex) { AppSys.Log("[自检] 出错: " + ex.Message, "err"); }
                AppSys.BootMark("AutoSetup 完成");
            }), DispatcherPriority.Background);
        };
        Closing += Win_Closing;
    }

    /* 窗口按 16:9 设计: 默认 1440×810, 超出工作区时等比缩放 */
    void Fit169()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            double maxW = wa.Width * 0.94, maxH = wa.Height * 0.94;
            double w = Math.Min(1440, maxW);
            double h = w * 9.0 / 16.0;
            if (h > maxH) { h = maxH; w = h * 16.0 / 9.0; }
            Width = w;
            Height = h;
        }
        catch { }
    }

    static DispatcherTimer Tick(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (s, e) => a();
        return t;
    }

    /*
     * 动画收尾 —— 这套界面的流畅性全靠它。
     *
     * WPF 里 BeginAnimation 播完之后 (默认 FillBehavior.HoldEnd) 动画时钟会【留在属性上】:
     * 属性值不再变化, 但组合循环认为"还有动画在跑", 于是窗口以 60fps 无限重绘 ——
     * 实测启动器静止不动时也烧掉约 25% 的一个核, 切页时叠加页面重建就更卡。
     *
     * 这里的做法: 先把终点写进基值, 动画设 FillBehavior.Stop, 播完立刻卸掉时钟。
     * 结果值一样, 但静止时进程 CPU 回到 ~0, 只有真正在播的那 200~480ms 才有开销。
     */
    static void Anim(DependencyObject target, DependencyProperty prop, double from, double to,
                     int ms, IEasingFunction ease = null, Action done = null)
    {
        var animatable = target as IAnimatable;
        if (animatable == null) return;
        target.SetValue(prop, to);
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        { FillBehavior = FillBehavior.Stop };
        if (ease != null) a.EasingFunction = ease;
        a.Completed += (s, e) =>
        {
            animatable.BeginAnimation(prop, null);   // 卸时钟
            target.SetValue(prop, to);
            if (done != null) done();
        };
        animatable.BeginAnimation(prop, a);
    }

    static readonly CubicEase EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.White;

    /* 标题栏品牌标记: 用 icon128.png (128x128 的同一枚图标)。
       原来是从 图标EXE.ico 里挑最接近 32px 的一帧 —— 现在 exe 图标与界面图标分开,
       界面侧只用这张 PNG: 尺寸足够清晰, 又省掉 .ico 在 exe 里被嵌两遍的浪费。 */
    static ImageSource LoadBrandIcon()
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/icon128.png", UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>背景栈的可视化对象 —— 毛玻璃面板用它做实时采样源</summary>
    public Visual BackdropVisual => BackdropHost;

    /* 弹窗打开时压暗主界面 (电影感聚焦) */
    public void SetDim(bool on)
    {
        Anim(FadeLayer, OpacityProperty, FadeLayer.Opacity, on ? 0.5 : 0.0, 200, EaseOut);
    }

    // =================================================================
    // 视图导航
    // =================================================================
    void Navigate(NavKey k)
    {
        if (k == _cur)
        {
            if (k != NavKey.Home) { _cur = (NavKey)(-1); Navigate(NavKey.Home); }
            return;
        }

        // 离开设置页时不再需要"未保存"询问 —— 设置页只剩即时生效的项
        // (目录定位 / 外观 / 目录与环境)，没有"改了要另外保存"的分区了。

        var el = _cache[(int)k] ??= MakePage(k);
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        (el as IViewRefresh)?.Refresh();
        long msRefresh = t0.ElapsedMilliseconds;
        ViewHost.Content = el;
        t0.Restart();
        // 首帧: 等第一次渲染回调, 把"页面真正画出来"的耗时记下来 (排查切页卡顿用)
        EventHandler once = null;
        once = (s, e) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= once;
            AppSys.Log(">>> 打开页面: " + k + "（刷新 " + msRefresh + "ms · 首帧 "
                     + t0.ElapsedMilliseconds + "ms）", "gray");
        };
        System.Windows.Media.CompositionTarget.Rendering += once;

        // 运行日志胶囊的位置: 首页右下有指挥台, 胶囊悬在其上方 (170 = 指挥台高 + 间距);
        // 其它页面的内容区底部只预留 64px, 胶囊悬在 170 处会压进页面内容。
        // 非首页一律降进预留条内 (20), 展开的日志面板照常向上生长。
        LogCard.Margin = k == NavKey.Home ? new Thickness(0, 0, 26, 170) : new Thickness(0, 0, 26, 20);

        el.Opacity = 0;
        var tt = new TranslateTransform(0, 16);
        el.RenderTransform = tt;
        // 切页过渡: 位图缓存 + 到点卸时钟。缓存让这 230ms 里页面只光栅化一次,
        // 之后每帧只是合成那张位图 (卡片投影/毛玻璃采样不参与逐帧重算)。
        // 过渡结束立刻清掉缓存, 静止时仍按矢量渲染, 字号不会发虚。
        el.CacheMode = new System.Windows.Media.BitmapCache();
        Anim(el, OpacityProperty, 0, 1, 230, EaseOut, () => { el.CacheMode = null; });
        Anim(tt, TranslateTransform.YProperty, 16, 0, 260, EaseOut);

        _cur = k;
        _navSync = true;
        NavHome.IsChecked = k == NavKey.Home;
        NavArc.IsChecked = k == NavKey.Archive;
        NavDll.IsChecked = k == NavKey.Dll;
        NavSet.IsChecked = k == NavKey.Settings;
        NavHelp.IsChecked = k == NavKey.Help;
        _navSync = false;
        UpdateBgArrows();
        ApplyBgMode(k == NavKey.Home);
    }

    /* 背景呈现模式:
       · 主页: 原画清晰 (无虚化 / 无压暗) — 沉浸感
       · 内容页(存档/设置/说明): 背景虚化 + 按"遮罩浓度"压暗 — 阅读优先
       两者均带 260ms 过渡, 由 Navigate 与遮罩浓度滑块驱动。 */
    void ApplyBgMode(bool home)
    {
        // 内容页背景模糊与首页面板毛玻璃共用同一个滑杆, 行为才一致
        double radius = home ? 0 : Compat.Clamp(AppSys.Settings.UiGlassBlur, 0, 40);
        double shade = home ? 0 : Math.Max(0.55, AppSys.Settings.UiOverlay);
        foreach (var layer in new[] { BgLayerA, BgLayerB })
        {
            if (layer == null) continue;
            if (!(layer.Effect is BlurEffect blur))
            {
                blur = new BlurEffect { KernelType = KernelType.Gaussian, Radius = 0, RenderingBias = RenderingBias.Performance };
                layer.Effect = blur;
            }
            // 半径动画同样要卸时钟: 整屏模糊每帧重算一次就是切页卡顿的元凶之一
            Anim(blur, BlurEffect.RadiusProperty, blur.Radius, radius, 260, EaseOut);
        }
        Anim(Shade, OpacityProperty, Shade.Opacity, shade, 260, EaseOut);
    }

    /* Win11: 窗口圆角 (Win10 及以下无效, 不影响功能) */
    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
            DwmSetWindowAttribute(h, 33, ref round, sizeof(int));
        }
        catch { }
    }

    UIElement MakePage(NavKey k)
    {
        try
        {
            switch (k)
            {
                case NavKey.Archive: return new Views.ArchiveView();
                case NavKey.Dll: return new Views.DllExtView();
                case NavKey.Settings: return new Views.SettingsView();
                case NavKey.Help: return new Views.HelpView();
                default: return new Views.HomeView();
            }
        }
        catch (Exception ex)
        {
            // 单个页面加载失败不应拖垮整个启动器: 记录完整异常并给出可见提示
            AppSys.Log("[页面] " + k + " 加载失败: " + ex, "err");
            Ui.Err("页面加载失败（" + k + "）：\n\n" + ex.Message
                + "\n\n完整异常已写入运行日志。", "页面加载失败");
            return Stub("页面加载失败", k + "：" + ex.Message);
        }
    }

    /* 兜底视图 (页面加载失败时显示) */
    static UIElement Stub(string title, string desc)
    {
        var card = new Border
        {
            Style = Application.Current.TryFindResource("GlassCard") as Style,
            Margin = new Thickness(24, 56, 24, 64),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 860
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Style = Application.Current.TryFindResource("H1") as Style, Text = title });
        sp.Children.Add(new TextBlock
        {
            Style = Application.Current.TryFindResource("Txt2") as Style,
            Text = desc,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        card.Child = sp;
        return card;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navSync || !_uiReady) return;   // 构造期(XAML 里 IsChecked)还没准备好, 交给 Loaded 里的 Navigate

        var rb = (RadioButton)sender;
        NavKey k = rb == NavHome ? NavKey.Home
                 : rb == NavArc ? NavKey.Archive
                 : rb == NavDll ? NavKey.Dll
                 : rb == NavSet ? NavKey.Settings
                 : NavKey.Help;
        Navigate(k);
    }

    void Win_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _cur != NavKey.Home) { Navigate(NavKey.Home); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.D1) Navigate(NavKey.Home);
            else if (e.Key == Key.D2) Navigate(NavKey.Archive);
            else if (e.Key == Key.D3) Navigate(NavKey.Dll);
            else if (e.Key == Key.D4) Navigate(NavKey.Settings);
            else if (e.Key == Key.D5) Navigate(NavKey.Help);
        }
    }

    // =================================================================
    // 标题栏: 拖动窗口 / 双击最大化 / 品牌回首页
    // =================================================================
    Point _capDown;
    bool _capDragged;   // 本次按下是否已经变成"拖动"(用来区分单击与拖动)

    /*
     * 拖动窗口。
     *
     * 【为什么不能只在 MouseLeftButtonDown 里直接 DragMove】
     * 一是单击（不移动）也会进一次拖动循环；二是从品牌区按下时,
     * 品牌要负责"单击回首页", 两者会互相打架。
     * 所以这里记录按下点, 在 MouseMove 里超过 4px 才真正开始拖,
     * 品牌区的单击则改成"抬键且没有拖动过"才跳首页。
     *
     * 另外: 标题栏 Grid 的 Background 必须是 Transparent(可命中) ——
     * Background="{x:Null}" 的元素不参与命中测试, 这条 48px 就既拖不动、
     * 也收不到鼠标事件（这就是"中间那片空白拖不了"的原因）。
     */
    void Caption_Down(object sender, MouseButtonEventArgs e)
    {
        _capDown = e.GetPosition(this);
        _capDragged = false;
        if (e.ClickCount == 2)
        {
            // 双击标题栏 = 最大化 / 还原 (Windows 习惯)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            _capDragged = true;          // 双击不应该再触发品牌跳转
        }
    }

    void Caption_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _capDown.X) < 4 && Math.Abs(p.Y - _capDown.Y) < 4) return;
        _capDragged = true;
        try { DragMove(); } catch { }   // 最大化状态下 WPF 会自己处理还原+跟随
    }

    void Brand_Click(object sender, MouseButtonEventArgs e)
    {
        if (_capDragged) return;         // 是拖动而不是单击 → 不回首页
        Navigate(NavKey.Home);
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        AppSys.Settings.UiTheme = AppSys.IsDark ? "light" : "dark";
        AppSys.SaveSettings();
        AppSys.ApplyTheme();   // 内部同时同步官方 Fluent 主题的明暗
        ThemeIcon.Data = (Geometry)FindResource(AppSys.IsDark ? "IcMoon" : "IcSun");
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void UpdateTitleStatus()
    {
        var srv = AppSys.Last.Srv;
        StDot.Fill = srv ? Res("Brush.Accent") : Res("Brush.Danger");
        StDotGlow.Color = srv ? Color.FromRgb(0x45, 0xC2, 0xF0) : Color.FromRgb(0xFF, 0x7A, 0x6E);
        StTxt.Text = srv ? "服务端运行中" : "服务端未运行";
        Pulse(StDot, srv);
    }

    /* 运行中时的呼吸灯 (柔和的透明度循环) */
    static void Pulse(UIElement el, bool on)
    {
        if (on)
        {
            var a = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            // 无限循环动画默认 60fps 一直改值 = 窗口一直在重绘。1.1 秒一个来回的呼吸
            // 压到 12fps 看不出来, 但重绘开销约 1/5 (首页的状态点同款处理, 见 HomeView.SetDot)。
            Timeline.SetDesiredFrameRate(a, 12);
            el.BeginAnimation(OpacityProperty, a);
        }
        else
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
        }
    }

    // =================================================================
    // 背景图轮播 / 导入
    // =================================================================
    void ReloadBg()
    {
        _bgFiles = AppSys.Settings.UiBackgrounds
            .Where(n => !string.IsNullOrWhiteSpace(n) && File.Exists(Path.Combine(AppSys.BgDir, n)))
            .ToList();
        if (_bgFiles.Count == 0)
        {
            ShowBuiltin();
            UpdateBgArrows();
            return;
        }
        _bgIdx = Math.Min(Math.Max(AppSys.Settings.UiBgIndex, 0), _bgFiles.Count - 1);
        ShowBg(_bgIdx, instant: true);
    }

    /* 出厂默认背景 — 内嵌的 DNF「起源·第五季·二·轮回」主视觉 */
    void ShowBuiltin()
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri("pack://application:,,,/BgDefault.jpg");
            bi.EndInit();
            bi.Freeze();
            _bgSrc = bi;
            BgBrushA.ImageSource = bi;
            BgLayerA.Opacity = 1;
            BgLayerB.Opacity = 0;
            _onA = true;
            AppSys.AdaptToArt(bi);   // 按原画重算界面配色
        }
        catch
        {
            BgLayerA.Opacity = 0;
            BgLayerB.Opacity = 0;
        }
    }

    void ShowBg(int idx, bool instant = false)
    {
        try
        {
            var file = Path.Combine(AppSys.BgDir, _bgFiles[idx]);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(file);
            bi.EndInit();
            bi.Freeze();
            _bgSrc = bi;
            AppSys.AdaptToArt(bi);   // 按新原画重算界面配色 (色相/明暗都可能变)

            if (_onA) { BgBrushB.ImageSource = bi; SwapBg(BgLayerB, BgLayerA, instant); _onA = false; }
            else { BgBrushA.ImageSource = bi; SwapBg(BgLayerA, BgLayerB, instant); _onA = true; }

            _bgIdx = idx;
            AppSys.Settings.UiBgIndex = idx;
            AppSys.SaveSettings();
        }
        catch (Exception ex)
        {
            AppSys.Log("[背景] 图片加载失败: " + ex.Message, "warn");
        }
        UpdateBgArrows();
    }

    void SwapBg(Border show, Border hide, bool instant)
    {
        if (instant)
        {
            show.Opacity = 1; hide.Opacity = 0;
            show.BeginAnimation(OpacityProperty, null); hide.BeginAnimation(OpacityProperty, null);
            return;
        }
        Anim(show, OpacityProperty, 0, 1, 480, EaseOut);
        Anim(hide, OpacityProperty, hide.Opacity, 0, 480, EaseOut);
    }

    void UpdateBgArrows()
    {
        var v = _bgFiles.Count > 1 && _cur == NavKey.Home ? Visibility.Visible : Visibility.Collapsed;
        BgPrev.Visibility = v;
        BgNext.Visibility = v;
    }

    void BgPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_bgFiles.Count > 1) ShowBg((_bgIdx - 1 + _bgFiles.Count) % _bgFiles.Count);
    }

    void BgNext_Click(object sender, RoutedEventArgs e)
    {
        if (_bgFiles.Count > 1) ShowBg((_bgIdx + 1) % _bgFiles.Count);
    }

    /// <summary>导入背景图片 (供设置页/拖拽调用), 返回成功张数</summary>
    public int ImportBackgrounds(IEnumerable<string> files)
    {
        int added = 0;
        try
        {
            Directory.CreateDirectory(AppSys.BgDir);
            foreach (var f in files ?? Enumerable.Empty<string>())
            {
                if (!IsImg(f) || !File.Exists(f)) continue;
                var name = Path.GetFileName(f);
                var dest = Path.Combine(AppSys.BgDir, name);
                if (File.Exists(dest))
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    dest = Path.Combine(AppSys.BgDir, stem + "_" + DateTime.Now.ToString("HHmmss") + ext);
                    name = Path.GetFileName(dest);
                }
                File.Copy(f, dest, false);
                AppSys.Settings.UiBackgrounds.Add(name);
                added++;
            }
            if (added > 0)
            {
                AppSys.Settings.UiBgIndex = AppSys.Settings.UiBackgrounds.Count - 1;
                AppSys.SaveSettings();
                ReloadBg();
                AppSys.Log("已导入 " + added + " 张背景图片（首页可用屏幕两侧箭头切换）。", "ok");
            }
        }
        catch (Exception ex)
        {
            AppSys.Log("[背景] 导入失败: " + ex.Message, "err");
        }
        return added;
    }

    // =================================================================
    // 局部取色: 把窗口里的一个元素映射到背景图的像素矩形, 再只分析那一片
    // =================================================================
    /* 背景图是 UniformToFill 绘制的 (等比铺满、居中裁切), 所以窗口坐标要先还原
       缩放与偏移才能落到图像像素上。 */
    public bool ImageRegionFor(FrameworkElement el, out Int32Rect rect)
    {
        rect = new Int32Rect(0, 0, 0, 0);
        var src = _bgSrc;
        if (src == null || el == null || el.ActualWidth < 2 || el.ActualHeight < 2) return false;
        try
        {
            var tl = el.TransformToAncestor(this).Transform(new Point(0, 0));
            double iw = src.PixelWidth, ih = src.PixelHeight;
            double winW = ActualWidth, winH = ActualHeight;
            double scale = Math.Max(winW / iw, winH / ih);
            double offX = (winW - iw * scale) / 2, offY = (winH - ih * scale) / 2;

            double x0 = (tl.X - offX) / scale, y0 = (tl.Y - offY) / scale;
            double x1 = (tl.X + el.ActualWidth - offX) / scale, y1 = (tl.Y + el.ActualHeight - offY) / scale;
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(iw, x1); y1 = Math.Min(ih, y1);
            if (x1 - x0 < 8 || y1 - y0 < 8) return false;   // 基本落在画面外
            rect = new Int32Rect((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0));
            return true;
        }
        catch { return false; }
    }

    /// <summary>元素在背景栈坐标系里的矩形 (毛玻璃采样用), inflate 用于给模糊留取样余量</summary>
    public bool VisualRectFor(FrameworkElement el, double inflate, out Rect rect)
    {
        rect = Rect.Empty;
        if (el == null || el.ActualWidth < 2 || el.ActualHeight < 2) return false;
        try
        {
            var tl = el.TransformToAncestor(this).Transform(new Point(0, 0));
            rect = new Rect(tl.X - inflate, tl.Y - inflate,
                            el.ActualWidth + inflate * 2, el.ActualHeight + inflate * 2);
            return true;
        }
        catch { return false; }
    }

    /// <summary>采样某元素正下方那片原画的配色 (失败时返回空档案, 调用方沿用全局令牌)</summary>
    public ArtProfile SampleUnder(FrameworkElement el)
    {
        try
        {
            if (_bgSrc == null || !ImageRegionFor(el, out var r)) return new ArtProfile();
            return ArtPalette.AnalyzeRegion(_bgSrc, r.X, r.Y, r.Width, r.Height);
        }
        catch { return new ArtProfile(); }
    }

    static bool IsImg(string f)
    {
        var ext = Path.GetExtension(f ?? "").ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
    }

    void Win_DragOver(object sender, DragEventArgs e)
    {
        bool img = false;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                img = ((string[])e.Data.GetData(DataFormats.FileDrop)).Any(IsImg);
        }
        catch { }
        e.Effects = img ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Win_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var imgs = files.Where(IsImg).ToArray();
            if (imgs.Length > 0) ImportBackgrounds(imgs);
        }
        catch { }
    }

    // =================================================================
    // 日志胶囊
    // =================================================================
    Brush KindBrush(string kind)
    {
        switch (kind)
        {
            case "ok": return Res("Brush.LogOk");
            case "warn": return Res("Brush.LogWarn");
            case "err": return Res("Brush.LogErr");
            case "blue": return Res("Brush.LogBlue");
            case "gold": return Res("Brush.LogGold");
            case "gray": return Res("Brush.LogGray");
            default: return Res("Brush.LogText");
        }
    }

    void FlushLog()
    {
        if (LogRt == null) return;
        bool follow = LogRt.ExtentHeight - LogRt.VerticalOffset - LogRt.ViewportHeight < 16;
        var ts = Res("Brush.LogTime");
        int n = 0;
        string last = null;
        while (n < 200 && AppSys.TryDequeueLog(out var m, out var kind))
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("[" + DateTime.Now.ToString("HH:mm:ss") + "] ") { Foreground = ts });
            p.Inlines.Add(new Run(m) { Foreground = KindBrush(kind) });
            LogRt.Document.Blocks.Add(p);
            last = m;
            n++;
        }
        if (n == 0) return;
        if (LbLogSub != null) LbLogSub.Text = _lastLogMsg ?? LbLogSub.Text;
        if (LogRt.Document.Blocks.Count > 400)
        {
            var remove = LogRt.Document.Blocks.Take(200).ToList();
            foreach (var b in remove) LogRt.Document.Blocks.Remove(b);
        }
        if (last != null) _lastLogMsg = last;
        if (LbLogSub != null && last != null) LbLogSub.Text = last;
        if (follow) LogRt.ScrollToEnd();
    }

    void LogFold_Click(object sender, RoutedEventArgs e) => ToggleLog();

    void LogHeader_Click(object sender, MouseButtonEventArgs e) => ToggleLog();

    void ToggleLog() => SetLogExpanded(!_logExpanded);

    /// <summary>展开/收起运行日志 (点开始游戏等长流程会自动展开, 见 OnProgressVis)</summary>
    public void SetLogExpanded(bool on)
    {
        if (_logExpanded == on) return;
        _logExpanded = on;
        LogBody.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LogTools.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (FoldIcon != null)
            FoldIcon.Data = (Geometry)FindResource(on ? "IcChevD" : "IcChevU");
        AppSys.FireLogExpand(on);   // 首页【闪屏修复】开关胶囊据此让位/回来
    }

    void LogCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new TextRange(LogRt.Document.ContentStart, LogRt.Document.ContentEnd).Text;
            if (string.IsNullOrWhiteSpace(text)) text = AppSys.RecentLog(500);
            if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
        }
        catch { AppSys.Log("复制失败: 剪贴板被占用，请稍后再试。", "err"); }
    }

    void LogClear_Click(object sender, RoutedEventArgs e)
    {
        LogRt.Document.Blocks.Clear();
        AppSys.ClearLog();
    }

    // =================================================================
    // 进度平滑蠕动
    // =================================================================
    void OnProgressVis(bool show)
    {
        if (show)
        {
            // 长流程一开始就把日志铺开, 让玩家看得见在跑什么;
            // 日志面板在指挥台上方展开, 不会遮住【开始游戏】等按钮。
            SetLogExpanded(true);
            _progOn = true;
            _pv = 0;
            TopBar.Value = 0;
            TopBar.Visibility = Visibility.Visible;
            PctText.Visibility = Visibility.Visible;
            _ptTimer.Start();
            if (BusyRing != null)
            {
                BusyRing.Visibility = Visibility.Visible;
                if (LogIcon != null) LogIcon.Visibility = Visibility.Collapsed;
                if (_busySpin == null)
                    _busySpin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
                    { RepeatBehavior = RepeatBehavior.Forever };
                BusySpin.BeginAnimation(RotateTransform.AngleProperty, _busySpin);
            }
        }
        else
        {
            _progOn = false;
            TopBar.Visibility = Visibility.Collapsed;
            PctText.Visibility = Visibility.Collapsed;
            _ptTimer.Stop();
            if (BusyRing != null)
            {
                BusyRing.Visibility = Visibility.Collapsed;
                if (LogIcon != null) LogIcon.Visibility = Visibility.Visible;
                BusySpin?.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            (ViewHost.Content as IHomeHooks)?.SetProgress(100, false);
        }
    }

    void PtTick()
    {
        if (!_progOn) return;
        double target = AppSys.TargetPct;
        if (_pv < target)
        {
            var gap = target - _pv;
            _pv += Math.Min(gap, Math.Max(0.35, gap * 0.16));
            if (_pv > target) _pv = target;
        }
        else if (_pv < Math.Min(target + 4, 97))
        {
            _pv += 0.03;
        }
        TopBar.Value = Math.Min(_pv, 99);
        PctText.Text = "进度 " + (int)_pv + "%";
        (ViewHost.Content as IHomeHooks)?.SetProgress(_pv, true);
    }

    // =================================================================
    // 退出清理 (等价 WinForms 版 Fc)
    // =================================================================
    bool _closing;

    async void Win_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        if (_closing) return;      // 连点两次关闭时不要弹两个确认框
        _closing = true;
        try
        {
            // 先取消本次关闭, 把"询问 + 收尾"交回消息循环再执行。
            // 直接在 Closing 事件里 ShowDialog 会踩到 WPF 的限制:
            // "在窗口关闭期间, 无法将可见性设置为可见, 也无法调用 Show/ShowDialog/Close" ——
            // 之前用户点退出确认框的按钮就会弹这个未处理异常。
            await Dispatcher.Yield(DispatcherPriority.Background);

            var r = Ui.Confirm(
                "退出启动器后，可以选择是否同时关闭正在运行的服务端。\n\n"
                + "注意：存档只有在服务端【正常停止】时才会完成最终同步与数据库完整性检查。\n"
                + "服务端是独立窗口，关掉那个窗口就等于结束这次游玩。\n\n"
                + "是否关闭服务端？\n（点【否】直接退出启动器，服务端继续在后台运行）",
                "确认退出", MessageBoxButton.YesNoCancel);
            if (r == MessageBoxResult.Cancel) return;

            // 【是】= 停掉服务端 + GM 工具; 【否】= 直接退出, 两者都留在后台。
            // 旧实现无论选什么都杀掉 GM 工具, 与弹窗上写的"点【否】直接退出、
            // 服务端继续在后台运行"矛盾 —— 玩家选了【否】却发现 GM 工具被关了。
            if (r == MessageBoxResult.Yes)
            {
                if (ServerControl.IsValidRoot(AppSys.ServerDir))
                {
                    AppSys.Log(">>> 正在停止服务端（让存档完成落盘）...", "gold");
                    try
                    {
                        var sr = await ServerControl.StopServerAsync(AppSys.ServerDir, m => AppSys.Log(m, "gray"));
                        if (!sr.Ok)
                            AppSys.Log(">>> [警告] " + sr.Error, "warn");
                        else if (!sr.WasRunning)
                            AppSys.Log(">>> 服务端本来就没在运行。", "gray");
                        else if (sr.Graceful)
                            AppSys.Log(">>> 服务端已正常停止，存档已完成最终同步。", "ok");
                        else
                            AppSys.Log(">>> 服务端已被强制结束（20 秒内没响应正常退出请求）。"
                                     + "如要备份存档，建议先启动再正常停止一次。", "warn");
                    }
                    catch (Exception ex) { AppSys.Log("[退出] 停止服务端出错: " + ex.Message, "err"); }
                }
                try
                {
                    // 桌面版与网页版都关（两者进程名不同，各查各的）。
                    var killed = ServerControl.KillGmTool(AppSys.ServerDir) + ServerControl.KillWebGm(AppSys.ServerDir);
                    if (killed > 0) AppSys.Log(">>> GM 工具已关闭。", "ok");
                }
                catch { }
            }
            else
            {
                AppSys.Log(">>> 已保留服务端与 GM 工具在后台运行。", "gray");
            }

            try { _stTimer?.Stop(); _ptTimer?.Stop(); _logTimer?.Stop(); } catch { }
            FlushLog();
            AppSys.SaveLog();
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            AppSys.Log("[退出] 收尾时出错: " + ex.Message, "err");
        }
        finally { _closing = false; }
    }
}
