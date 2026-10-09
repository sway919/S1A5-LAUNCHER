using System;
using System.Windows;
using System.Windows.Threading;
using ServerUI.Services;

namespace ServerUI;

public partial class App : Application
{
    // 静态构造 = 生成的 Main 里 `new App()` 那一刻, 早于 App.InitializeComponent()(解析 App.xaml),
    // 用来把"宿主/CLR 起步"与后面各段分开量 (见 Core/AppSys.cs 的 BootMark)。
    static App() { Core.AppSys.BootMark("App 静态构造 (Main 刚进入)"); }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                // 崩溃时把完整异常(含内部异常)写进日志并立刻落盘, 否则重启就查不到了
                Core.AppSys.Log("[崩溃] " + args.Exception, "err");
                Core.AppSys.SaveLog();
            }
            catch { }
            MessageBox.Show("程序遇到未处理的异常：\n\n" + args.Exception.Message
                + "\n\n详情见运行日志。", "S1A5 启动器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        base.OnStartup(e);
        Core.AppSys.BootMark("OnStartup 进入");

        /*
         * ================================================================
         * 启动顺序 (为什么这么排)
         * ================================================================
         * 双击到"主界面真的画在屏幕上"实测约 4 秒, 拆开是:
         *   · 宿主/CLR/Costura 起步   ~0.8s   ← 在地板下面, 砍不掉
         *   · 四份主题字典首次解析    ~0.9s
         *   · 主窗口 XAML 解析        ~1.2s
         *   · Show() 里的排版+首帧    ~1.0s
         * 后三段都在 UI 线程上, 期间窗口无法重绘 —— 所以顺序只能是:
         *   1) 先把【启动落地窗】显示出来 (~0.9s 就能看见);
         *   2) 再装主题字典、建主窗口这些重活;
         *   3) 主窗口第一帧画完 (ContentRendered) 才关落地窗, 不露白。
         * 另外: ShutdownMode 在过渡期必须是 OnExplicitShutdown, 否则落地窗一关
         * (那时主窗口还没登记为可见) 应用会直接退出。
         * ================================================================
         */
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var splash = new Views.SplashWindow();
        Core.AppSys.BootMark("落地窗: 构造返回");
        MainWindow = splash;
        splash.Show();
        Core.AppSys.BootMark("落地窗已显示");

        /*
         * Background 优先级 = 让上面这次 Show 的布局/渲染先跑完, 落地窗真正落到
         * 屏幕上之后, 再去做下面这 3 秒的重活。
         */
        splash.Dispatcher.BeginInvoke(new Action(() => BringUpMainWindow(splash)),
            DispatcherPriority.Background);
    }

    void BringUpMainWindow(Views.SplashWindow splash)
    {
        try
        {
            // 1) 主题字典 + 令牌 (明暗由 AppSys.ApplyTheme 决定)
            Core.AppSys.EnsureThemeDictionaries();
            Core.AppSys.ApplyTheme();
            Core.AppSys.BootMark("主题字典就绪");
        }
        catch (Exception ex)
        {
            Core.AppSys.Log("[启动] 主题加载失败，界面将退回内置样式: " + ex.Message, "err");
        }

        // 2) 首次运行 · 使用协议 (防倒狗闸门)
        //    必须逐字输入确认短语才能过; 关闭/Esc/点不同意 → 直接结束进程。
        //    放在主题就位之后、主窗口之前: 协议窗用的是主题令牌, 得等字典装好;
        //    而它必须挡在主界面之前, 玩家没确认就进不去。
        //    过渡期 ShutdownMode 已是 OnExplicitShutdown, 关掉协议窗不会误退出。
        if (!Core.AppSys.Settings.AgreementAccepted)
        {
            bool ok;
            try { splash.Hide(); } catch { }      // 落地窗是 Topmost, 不藏起来会压住协议窗
            try { ok = Views.AgreementWindow.Ask(); }
            catch (Exception ex)
            {
                Core.AppSys.Log("[协议] 弹窗失败: " + ex.Message, "err");
                ok = false;
            }

            if (!ok)
            {
                // 不同意 = 不使用本程序。不留后台进程, 直接退出。
                try { Core.AppSys.Log(">>> 未接受使用协议，程序退出。", "warn"); Core.AppSys.SaveLog(); } catch { }
                try { splash.Close(); } catch { }
                Shutdown(0);
                return;
            }

            Core.AppSys.Settings.AgreementAccepted = true;
            Core.AppSys.SaveSettings();
            Core.AppSys.Log(">>> 已接受使用协议。", "ok");
            try { splash.Show(); } catch { }      // 协议窗关了, 继续用落地窗挡到主界面第一帧
        }

        // 3) 主窗口
        MainWindow win;
        try
        {
            win = new MainWindow();
        }
        catch (Exception ex)
        {
            Core.AppSys.Log("[启动] 主窗口创建失败: " + ex, "err");
            try { splash.Close(); } catch { }
            Core.Ui.Err("主界面创建失败：\n\n" + ex.Message + "\n\n完整异常已写入运行日志。", "启动失败");
            Shutdown(1);
            return;
        }

        MainWindow = win;
        // 主窗口登记为可见之后再交回常规关闭行为。放在 Show() 之前也安全
        // (落地窗这时还开着, 不会因为"最后一个窗口关了"而退出)。
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        // 第一帧出来之后再撤落地窗 —— 否则会闪一下没画完的主界面
        win.ContentRendered += (s, e) =>
        {
            try { splash.Close(); } catch { }
        };
        win.Show();
        Core.AppSys.BootMark("主窗口已显示 (Show 返回)");
    }
}
