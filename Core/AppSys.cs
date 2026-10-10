/*
 * ==================================================================
 * AppSys — 全局服务中枢 (Core/AppSys.cs)
 * ==================================================================
 * 持有服务实例 (Services\*)、提供日志队列 / 进度目标 / 状态快照 /
 * 主题与背景目录 等跨视图设施。视图 (Views\*) 只与 AppSys/Flows 打交道,
 * 不互相引用。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ServerUI.Services;

namespace ServerUI.Core;

/* 视图导航键 */
// Dll = 【DLL 扩展】页 (客户端插件管理, 见 Services\DllExtService.cs)。
// 位置与 60US starter 一致地排在 Archive 与 Settings 之间。
public enum NavKey { Home, Archive, Dll, Settings, Help }

public static class AppSys
{
    public const string Ver = "1.0";
    public const string AppName = "S1A5 一键启动器";

    // ===== 业务服务 =====
    public static readonly LauncherSettings Settings = LauncherSettings.Load();

    // =================================================================
    // 启动计时 — 排查"双击到看见窗口"的延迟用 (常驻, 只写日志)
    // =================================================================
    public static readonly System.Diagnostics.Stopwatch Boot = System.Diagnostics.Stopwatch.StartNew();
    static DateTime _procStart = DateTime.MinValue;

    /// <summary>把"相对 OnStartup / 相对进程启动"的耗时打进日志</summary>
    public static void BootMark(string what)
    {
        try
        {
            if (_procStart == DateTime.MinValue)
            {
                try { _procStart = System.Diagnostics.Process.GetCurrentProcess().StartTime; } catch { }
            }
            var sinceProc = _procStart == DateTime.MinValue
                ? -1 : (int)(DateTime.Now - _procStart).TotalMilliseconds;
            var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + what
                     + " · 逻辑 +" + (int)Boot.Elapsed.TotalMilliseconds + "ms"
                     + (sinceProc >= 0 ? " / 进程 +" + sinceProc + "ms" : "") + "\r\n";
            // 独立小文件、追加写、每行立即落盘 —— 不依赖攒批日志的节流, 排查"多慢"时不会被吞。
            // 每次启动都会追加一段, 所以超过 64KB 就重来一次, 不让它无限长。
            try
            {
                Directory.CreateDirectory(LauncherLogDir);
                var trace = Path.Combine(LauncherLogDir, "boot-trace.txt");
                var fi = new FileInfo(trace);
                if (fi.Exists && fi.Length > 64 * 1024) fi.Delete();
                File.AppendAllText(trace, line, new UTF8Encoding(false));
            }
            catch { }
            Log("[启动] " + what + " · 逻辑 +" + (int)Boot.Elapsed.TotalMilliseconds + "ms"
                + (sinceProc >= 0 ? " / 进程 +" + sinceProc + "ms" : ""), "gray");
        }
        catch { }
    }

    // 已解析的两个目录。
    // 【初值取设置里记住的那份】为空时仍由 Flows.Startup 自动定位。
    public static string ServerDir = Settings.ServerDir ?? "";
    public static string ClientDir = Settings.ClientDir ?? "";

    // 长流程防重入
    public static bool Busy;

    public static void SaveSettings() => Settings.Save();

    /// <summary>启动器自己的运行数据目录 (存档备份 / 日志)</summary>
    public static string RuntimeDir => Path.Combine(AppContext.BaseDirectory, "runtime");
    public static string LauncherLogDir => Path.Combine(RuntimeDir, "logs");

    // =================================================================
    // 日志 — 队列攒批渲染 + 全文缓存 (退出落盘)
    // kind: info=正文白 gray=次要 ok=绿 warn=橙 err=红 blue=蓝 gold=金
    // =================================================================
    static readonly object _logLock = new object();
    static readonly Queue<KeyValuePair<string, string>> _logQueue = new Queue<KeyValuePair<string, string>>();
    static readonly StringBuilder _logBuilder = new StringBuilder();

    public static event Action<string, string> LogPosted;

    public static void Log(string m, string kind = "info")
    {
        lock (_logLock) _logQueue.Enqueue(new KeyValuePair<string, string>(m, kind));
        lock (_logLock) _logBuilder.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m);
        var h = LogPosted; if (h != null) h(m, kind);
        FlushLogSoon();
    }

    public static bool TryDequeueLog(out string m, out string kind)
    {
        lock (_logLock)
        {
            if (_logQueue.Count == 0) { m = null; kind = null; return false; }
            var kv = _logQueue.Dequeue(); m = kv.Key; kind = kv.Value; return true;
        }
    }

    public static void ClearLog()
    {
        lock (_logLock) { _logQueue.Clear(); _logBuilder.Clear(); }
    }

    public static string RecentLog(int maxLines)
    {
        lock (_logLock)
        {
            var all = _logBuilder.ToString();
            if (string.IsNullOrEmpty(all)) return "";
            var lines = all.Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= maxLines) return all;
            return string.Join("\n", lines, lines.Length - maxLines, maxLines);
        }
    }

    public static void SaveLog()
    {
        try
        {
            Directory.CreateDirectory(LauncherLogDir);
            lock (_logLock)
                File.WriteAllText(Path.Combine(LauncherLogDir, "launcher-运行日志.txt"),
                    _logBuilder.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    /* 边跑边落盘: 退出时写一份虽然够用, 但被强杀 / 崩溃 / 自动化脚本 terminate 掉就什么都没有了
       (排查问题最需要的恰恰是那种时候的日志)。这里在写日志时顺手限流刷盘 (最多 2 秒一次),
       代价可以忽略, 换来"日志随时是最新的"。 */
    static DateTime _logFlushedAt = DateTime.MinValue;
    static void FlushLogSoon()
    {
        var now = DateTime.UtcNow;
        if ((now - _logFlushedAt).TotalSeconds < 2) return;
        _logFlushedAt = now;
        SaveLog();
    }

    // =================================================================
    // 进度 — 只暴露"目标百分比", 平滑蠕动由主窗口的计时器完成
    // =================================================================
    public static double TargetPct;
    public static event Action<bool> ProgressVisible;

    public static void ShowProgress() { TargetPct = 3; var h = ProgressVisible; if (h != null) h(true); }
    public static void SetStep(int pct, string text = null)
    {
        if (pct > TargetPct) TargetPct = pct;
        if (!string.IsNullOrEmpty(text)) Log(">>> " + text, "blue");
    }
    public static void FinishProgress() { TargetPct = 100; var h = ProgressVisible; if (h != null) h(false); }

    // =================================================================
    // 状态快照 — 主窗口每 2 秒调用 RefreshStatus, 各视图读 Last/订阅事件
    // (S1A5 口径: 服务端 java 进程 / 网页 GM / s1a5.db / 客户端 / A5.dll / Script.pvf)
    // =================================================================
    public class Snap
    {
        public bool Srv;                     // 服务端 (java -jar) 运行中
        public bool Gm;                      // 网页 GM (WebGmMain) 运行中
        public bool ClOk;                    // 客户端目录可用 (DNF.exe + Script.pvf)
        public bool PvfOk;                   // 客户端 Script.pvf 存在
        public string DbText = "未定位";      // s1a5.db 状态
        public string ClText = "未配置";      // 客户端文本
        public string PvfText = "未检查";     // Script.pvf 状态文本
        public string OverlayText = "未部署"; // A5.dll 客户端补丁状态
        public string PortText = "-- / --";  // 游戏端口 / 网页GM端口
    }

    public static readonly Snap Last = new Snap();
    public static event Action StatusChanged;

    public static void FireStatus()
    {
        var h = StatusChanged; if (h != null) h();
    }

    public static void RefreshStatus()
    {
        try
        {
            var root = ServerDir;
            if (!ServerControl.IsValidRoot(root))
            {
                // 还没定位/定位失效: 重新找一次 (玩家可能刚把目录挪了)
                var found = ServerControl.ResolveServerDir(root);
                if (ServerControl.IsValidRoot(found)) root = ServerDir = found;
            }
            Last.Srv = ServerControl.IsValidRoot(root) && ServerControl.IsServerRunning(root);
            Last.Gm = ServerControl.IsValidRoot(root) && ServerControl.IsWebGmRunning(root);

            // 存档
            if (!ServerControl.IsValidRoot(root)) Last.DbText = "未定位";
            else Last.DbText = ServerControl.ReadDbInfo(root).Text;

            // 客户端
            if (!ServerControl.IsValidClient(ClientDir))
            {
                var found = ServerControl.ResolveClientDir(root);
                if (ServerControl.IsValidClient(found))
                {
                    ClientDir = found;
                    Settings.ClientDir = found;
                    SaveSettings();
                }
            }
            Last.ClOk = ServerControl.IsValidClient(ClientDir);
            var name = "";
            try { name = Path.GetFileName((ClientDir ?? "").TrimEnd(Path.DirectorySeparatorChar)); } catch { }
            Last.ClText = Last.ClOk
                ? (string.IsNullOrEmpty(name) ? ClientDir : name)
                : "未配置";

            // Script.pvf (只看客户端目录里有没有, 不比对别处)
            if (Last.ClOk)
            {
                var pvf = ServerControl.ScriptPath(ClientDir);
                if (File.Exists(pvf))
                {
                    Last.PvfOk = true;
                    var mb = new FileInfo(pvf).Length / 1048576.0;
                    Last.PvfText = "已就绪 · " + mb.ToString("0") + " MB";
                }
                else { Last.PvfOk = false; Last.PvfText = "缺失"; }
            }
            else { Last.PvfOk = false; Last.PvfText = "未检查"; }

            // A5.dll 客户端补丁 (没有它杀怪不上报)
            Last.OverlayText = Last.ClOk
                ? (File.Exists(Path.Combine(ClientDir, "A5.dll")) ? "已部署" : "未部署")
                : "未部署";

            // 端口读数: 游戏端口 / 网页GM端口 (服务端在跑时读实际监听端口)
            Last.PortText = ReadPorts(root, Last.Srv, Last.Gm);
        }
        catch { }
        FireStatus();
    }

    static string ReadPorts(string root, bool srvRunning, bool gmRunning)
    {
        try
        {
            int game = Settings.GamePort, gm = Settings.WebGmPort;
            if (srvRunning)
            {
                var pid = ServerControl.ServerPid(root);
                if (pid > 0)
                {
                    var ports = PortInfo.ListeningPorts(pid);
                    if (!ports.Contains(game) && ports.Count > 0) game = ports[0];
                }
            }
            if (gmRunning)
            {
                var pid = ServerControl.WebGmPid();
                if (pid > 0)
                {
                    var ports = PortInfo.ListeningPorts(pid);
                    if (!ports.Contains(gm) && ports.Count > 0) gm = ports[0];
                }
            }
            return game + " / " + gm;
        }
        catch { return Settings.GamePort + " / " + Settings.WebGmPort; }
    }

    // =================================================================
    // 跨视图请求 (Flows → 主窗口)
    // =================================================================
    public static event Action<NavKey> RequestNavigate;
    public static void Go(NavKey k) { var h = RequestNavigate; if (h != null) h(k); }

    public static event Action OverlayChanged;     // 背景遮罩浓度变化
    public static void FireOverlay() { var h = OverlayChanged; if (h != null) h(); }

    // 运行日志面板展开/收起 (MainWindow.SetLogExpanded 维护)。
    // 首页的【闪屏修复】开关胶囊悬在日志胶囊正上方, 日志面板展开时会压住它 ——
    // 它订阅这个事件在展开期间让位 (HomeView, 订阅与退订严格配对)。
    public static bool LogExpanded;
    public static event Action<bool> LogExpandChanged;
    public static void FireLogExpand(bool expanded)
    { LogExpanded = expanded; var h = LogExpandChanged; if (h != null) h(expanded); }

    public static event Action BackgroundsChanged; // 背景列表变化
    public static void FireBackgrounds() { var h = BackgroundsChanged; if (h != null) h(); }

    /// <summary>配色重算完毕 (换图/换主题) —— 需要局部取色的视图据此刷新</summary>
    public static event Action ArtChanged;
    public static void FireArtChanged() { var h = ArtChanged; if (h != null) h(); }

    // =================================================================
    // 主题与背景
    // =================================================================
    /*
     * 四份主题字典的安装 (只装一次)。
     *
     * 【为什么不写在 App.xaml 里】
     * 那四份字典合计 ~1300 行 XAML, 首次解析约 0.9 秒 —— 而 App.xaml 是在
     * 生成的 Main() 里 (app.InitializeComponent()) 解析的, 也就是 OnStartup
     * 之前。为了能在那之前把启动落地窗弹出来, 字典挪到这里, 由 App 在落地窗
     * 显示之后再调。顺序沿用 App.xaml 时代的那一套: Fluent → Brushes →
     * Controls → Icons (Controls.xaml 里有 31 处 StaticResource, 虽然指向的都是
     * 自己文件内定义的模板键, 保持原顺序仍然最稳)。
     */
    static bool _themeDictsInstalled;
    public static void EnsureThemeDictionaries()
    {
        if (_themeDictsInstalled) return;
        var app = System.Windows.Application.Current;
        if (app == null) return;
        var md = app.Resources.MergedDictionaries;
        foreach (var f in new[] { "Themes/Fluent.Dark.xaml", "Themes/Brushes.Dark.xaml",
                                  "Themes/Controls.xaml",    "Themes/Icons.xaml" })
        {
            bool already = false;
            foreach (var d in md)
                if (string.Equals(d?.Source?.OriginalString, f, StringComparison.OrdinalIgnoreCase))
                { already = true; break; }
            if (!already)
                md.Add(new System.Windows.ResourceDictionary { Source = new Uri(f, UriKind.Relative) });
        }
        _themeDictsInstalled = true;
    }

    public static void ApplyTheme()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            var md = app.Resources.MergedDictionaries;
            var mood = Settings.UiTheme == "light" ? "Light" : "Dark";

            /*
             * 按 Source 定位我们自己的两份字典后整体替换, 不用固定索引:
             * 官方 Fluent 主题会把它的字典插到索引 0, 固定索引替换会打到框架字典上。
             * 只动 Themes/ 开头的自家字典, 并追加到末尾, 保证我们的令牌排在框架字典之后生效。
             */
            for (int i = md.Count - 1; i >= 0; i--)
            {
                var s = md[i]?.Source?.OriginalString ?? "";
                if (s.StartsWith("Themes/Brushes.", StringComparison.OrdinalIgnoreCase)
                 || s.StartsWith("Themes/Fluent.", StringComparison.OrdinalIgnoreCase))
                    md.RemoveAt(i);
            }
            md.Add(new System.Windows.ResourceDictionary
            { Source = new Uri("Themes/Fluent." + mood + ".xaml", UriKind.Relative) });
            md.Add(new System.Windows.ResourceDictionary
            { Source = new Uri("Themes/Brushes." + mood + ".xaml", UriKind.Relative) });

            // 原画配色自适应 —— 界面吸取背景原画的色相/彩度,
            // 让按钮与强调色跟着主图走 (玩家换任何背景图都不会撞色)。
            ArtPalette.Apply(Art, IsDark, Settings.UiArtFusion, Settings.UiGlassOpacity);
            // 环境适配: 标题带/底部带的明暗方向 + 渐隐带浓度 (不做染色)
            ArtPalette.ApplyEnvironment(Art, IsDark, Settings.UiScrimOpacity);
            FireArtChanged();
        }
        catch (Exception ex)
        {
            Log("[主题] 外观主题切换失败，已保留当前配色: " + ex.Message, "err");
        }
    }

    // =================================================================
    // 原画自适应配色 — 见 Core/ArtPalette.cs
    // 换背景图 / 换主题 / 改融合度 都会重算一次: 强调色与面板色调跟着主图走,
    // 标题带的明暗方向由该带的亮度决定 (保证文字可读)。
    // =================================================================
    public static ArtProfile Art { get; private set; } = new ArtProfile();

    public static void ReAdapt()
    {
        try
        {
            ArtPalette.Apply(Art, IsDark, Settings.UiArtFusion, Settings.UiGlassOpacity);
            ArtPalette.ApplyEnvironment(Art, IsDark, Settings.UiScrimOpacity);
        }
        catch { }
        FireArtChanged();
    }

    public static void AdaptToArt(System.Windows.Media.Imaging.BitmapSource src)
    {
        try
        {
            Art = ArtPalette.Analyze(src);
            ArtPalette.Apply(Art, IsDark, Settings.UiArtFusion, Settings.UiGlassOpacity);
            ArtPalette.ApplyEnvironment(Art, IsDark, Settings.UiScrimOpacity);
            FireArtChanged();
            if (Art.Valid)
                Log(">>> 已按背景原画自适应配色：主色相 " + (int)Art.SourceHue + "° / 彩度 "
                  + (int)Art.SourceChroma + "；融合度 " + (int)(Settings.UiArtFusion * 100) + "%"
                  + "（设置 → 外观 可调）；标题带" + (Art.TopIsDark ? "偏暗→浅色字" : "偏亮→深色字")
                  + "，底部带" + (Art.BottomIsDark ? "偏暗→浅色字" : "偏亮→深色字"), "gray");
        }
        catch { }
    }

    public static bool IsDark => Settings.UiTheme != "light";

    /* 渲染环境自检 (排查"卡"用): WPF 层级 2 = 显卡加速, 0 = 纯软件渲染 (远程桌面/虚拟机常见) ——
       软件渲染下毛玻璃与投影会明显变重, 这时把【设置 → 外观 → 玻璃模糊】调小最有效。 */
    public static void LogRenderEnv()
    {
        try
        {
            int tier = System.Windows.Media.RenderCapability.Tier >> 16;
            string dpi = "1.0";
            var app = System.Windows.Application.Current;
            if (app != null && app.MainWindow != null)
            {
                var d = System.Windows.Media.VisualTreeHelper.GetDpi(app.MainWindow);
                dpi = d.DpiScaleX.ToString("0.##");
            }
            Log(">>> 渲染: WPF 层级 " + tier + (tier >= 2 ? "（显卡加速）" : "（纯软件渲染，界面特效会偏重）")
              + " · 缩放 " + dpi + "x · 玻璃模糊 " + (int)Settings.UiGlassBlur + "px", "gray");
        }
        catch { }
    }

    // 背景图目录 (EXE 旁 configs\背景\)
    public static string BgDir => Path.Combine(AppContext.BaseDirectory, "configs", "背景");
}
