/*
 * ==================================================================
 * 业务流程 (Core/Flows.cs) — S1A5 版
 * ==================================================================
 * 每个"按钮"背后的完整流程都收在这里, 视图只负责调用 + 显示。
 * 所有长流程都遵守同一套约定:
 *   · AppSys.Busy 防重入 (两个流程同时改数据库会互相踩踏)
 *   · AppSys.ShowProgress / SetStep / FinishProgress 驱动进度条与日志
 *   · 失败时给出"玩家下一步该做什么", 而不是只抛一句异常
 *
 * 【S1A5 的两条硬约束】
 *   1. 启动顺序: 先开服务端 (java -jar, 等端口就绪), 再进游戏 (DNF.exe
 *      命令行 16 段 '?' 分隔, 与 client-10038.cmd 完全一致)。
 *   2. 写库规矩: 服务端在跑时网页 GM 只读不写 —— 发道具/调等级要先停止
 *      服务端。所以备份/还原/重新开局一律要求服务端已停止。
 * ==================================================================
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ServerUI.Services;

namespace ServerUI.Core;

public static class Flows
{
    // =================================================================
    // 首次运行自检
    //
    // 【为什么分两半】
    //   目录定位、读存档、找 Java (要起一个 java -version 子进程等它退出)
    //   这一半是纯 IO / 进程等待, 放线程池 (Log 是线程安全的, 见 AppSys.Log);
    //   收尾的 RefreshStatus 会改静态快照并触发 StatusChanged, 各视图在事件里
    //   直接碰控件 —— 所以必须回到 UI 线程调。
    //   实测: 这一整段以前在 Loaded 里同步跑 ~1 秒, 把 Show() 也一起拖住了。
    // =================================================================
    public static async Task AutoSetup()
    {
        bool firstRun = !AppSys.Settings.FirstRunDone;
        AppSys.BootMark("AutoSetup: 起自检线程");
        await Task.Run(() => AutoSetupOffThread());
        AppSys.BootMark("AutoSetup: 自检线程结束（回到界面线程）");
        try { AppSys.LogRenderEnv(); AppSys.RefreshStatus(); } catch { }
        AppSys.BootMark("AutoSetup: 渲染环境/状态刷新完成");

        /*
         * Java 25 是服务端起不来的唯一硬前提。缺了就说清楚下一步：
         *   · 包里带了安装包 → 报出路径；**第一次游玩**直接问一句要不要现在装；
         *   · 包里也没有 → 让玩家自己装。
         * （不是第一次、又不点【开始游戏】时不打扰 —— 点【开始游戏】时 EnsureServerAsync 还会再问。）
         */
        try
        {
            string jerr;
            if (string.IsNullOrEmpty(ServerControl.FindJava(out jerr)))
            {
                var msi = ServerControl.FindJavaInstaller();
                AppSys.BootMark("AutoSetup: 找 Java 安装包完成");
                if (!string.IsNullOrEmpty(msi))
                {
                    AppSys.Log("[Java] 没找到 Java 25 —— 服务端需要它。包里带了安装包: " + msi, "warn");
                    if (firstRun) await EnsureJavaAsync(refresh: false);
                    else AppSys.Log("    点【开始游戏】时会引导安装。", "gray");
                }
                else
                {
                    AppSys.Log("[Java] 没找到 Java 25，包里也没找到安装包（*.msi / jdk*.exe）。", "warn");
                    AppSys.Log("    请安装 Java ≥25（或把安装包放进包里，启动器会自动找到）。", "gray");
                }
                AppSys.RefreshStatus();
            }
        }
        catch (Exception ex) { AppSys.Log("[自检] Java 检查出错: " + ex.Message, "err"); }
        AppSys.BootMark("AutoSetup: Java 检查结束");
    }

    static void AutoSetupOffThread()
    {
        try
        {
            AppSys.Log(">>> " + AppSys.AppName + " v" + AppSys.Ver + " 启动", "gold");
            // 后台预热 java 进程清单 (WMI 扫描很慢, 不能放在界面线程上按需扫)
            ServerControl.StartProcWatcher();
            AppSys.BootMark("AutoSetup: 预热线程已起");

            if (!ServerControl.IsValidRoot(AppSys.ServerDir))
            {
                var found = ServerControl.ResolveServerDir(AppSys.ServerDir);
                if (ServerControl.IsValidRoot(found))
                {
                    AppSys.ServerDir = found;
                    AppSys.Settings.ServerDir = found;
                    AppSys.Log(">>> 已自动定位服务端目录: " + found, "ok");
                }
            }
            else AppSys.Log(">>> 服务端目录: " + AppSys.ServerDir, "gray");

            if (!ServerControl.IsValidClient(AppSys.ClientDir))
            {
                var found = ServerControl.ResolveClientDir(AppSys.ServerDir);
                if (ServerControl.IsValidClient(found))
                {
                    AppSys.ClientDir = found;
                    AppSys.Settings.ClientDir = found;
                    AppSys.Log(">>> 已自动定位客户端目录: " + found, "ok");
                }
            }
            else AppSys.Log(">>> 客户端目录: " + AppSys.ClientDir, "gray");
            AppSys.BootMark("AutoSetup: 目录定位完成");

            // 闪屏修复: 上次勾着的话, 客户端一就位就把补丁补齐 (已在位的自动跳过)
            if (AppSys.Settings.FlickerFixEnabled && ServerControl.IsValidClient(AppSys.ClientDir)
                && !FlickerFixService.IsDeployedAt(AppSys.ClientDir, AppSys.Settings.FlickerFixMode))
            {
                string ffErr = null;
                var ffOk = FlickerFixService.Deploy(AppSys.ClientDir, AppSys.Settings.FlickerFixMode, m => AppSys.Log(m, "gray"), out ffErr);
                AppSys.Log(ffOk ? ">>> 闪屏修复补丁已就位。"
                                : "[闪屏修复] 自动补丁失败: " + ffErr, ffOk ? "gray" : "warn");
            }

            AppSys.Settings.FirstRunDone = true;
            AppSys.SaveSettings();

            if (!ServerControl.IsValidRoot(AppSys.ServerDir))
                AppSys.Log("[提示] 没找到服务端目录（含 server\\s1a5-*-all.jar）。请到【设置 → 目录定位】指定。", "warn");
            if (!ServerControl.IsValidClient(AppSys.ClientDir))
                AppSys.Log("[提示] 没找到游戏客户端目录（含 DNF.exe + Script.pvf）。请到【设置 → 目录定位】指定。", "warn");

            if (ServerControl.IsValidRoot(AppSys.ServerDir))
            {
                var db = ServerControl.ReadDbInfo(AppSys.ServerDir);
                AppSys.Log(">>> 存档: " + db.Text, db.Exists ? "gray" : "warn");
                if (ServerControl.IsValidClient(AppSys.ClientDir))
                {
                    AppSys.Log(">>> A5.dll: " + (File.Exists(Path.Combine(AppSys.ClientDir, "A5.dll"))
                        ? "已部署" : "未部署（没有它杀怪不上报）"), "gray");
                    string jerr;
                    var java = ServerControl.FindJava(out jerr);
                    AppSys.BootMark("AutoSetup: Java 探测完成");
                    AppSys.Log(string.IsNullOrEmpty(java)
                        ? "[提示] 没找到 Java 25: " + jerr
                        : ">>> Java: " + ServerControl.JavaVersionText, string.IsNullOrEmpty(java) ? "warn" : "gray");
                }
                if (ServerControl.IsServerRunning(AppSys.ServerDir))
                    AppSys.Log(">>> 检测到服务端已在运行。", "ok");
            }
            AppSys.BootMark("AutoSetup: 自检完成");
        }
        catch (Exception ex) { AppSys.Log("[自检] 出错: " + ex.Message, "err"); }
    }

    // =================================================================
    // 目录定位
    // =================================================================
    public static async Task LocateServerRoot()
    {
        var d = Ui.PickFolder("请选择服务端包根目录（能看到 server\\s1a5-*-all.jar 或 data 目录的那一层）");
        if (string.IsNullOrEmpty(d)) return;
        if (!ServerControl.IsValidRoot(d))
        {
            Ui.Warn("这个目录不像服务端包根目录。\n\n"
                  + "服务端包根目录里应当能看到：\n"
                  + "  server\\s1a5-*-all.jar（服务端）\n"
                  + "  data\\（s1a5.db 存档）\n"
                  + "  client\\（游戏客户端，可选）\n"
                  + "  run.cmd / run-gm.cmd", "目录不对");
            return;
        }
        AppSys.ServerDir = d;
        AppSys.Settings.ServerDir = d;
        AppSys.SaveSettings();
        AppSys.Log(">>> 服务端目录已设为: " + d, "ok");
        if (!ServerControl.IsValidClient(AppSys.ClientDir))
        {
            var c = ServerControl.ResolveClientDir(d);
            if (ServerControl.IsValidClient(c))
            {
                AppSys.ClientDir = c;
                AppSys.Settings.ClientDir = c;
                AppSys.SaveSettings();
                AppSys.Log(">>> 顺带定位到客户端目录: " + c, "ok");
            }
        }
        AppSys.RefreshStatus();
        await Task.CompletedTask;
    }

    public static async Task LocateClientDir()
    {
        var d = Ui.PickFolder("请选择游戏客户端目录（能看到 DNF.exe 与 Script.pvf 的那一层）");
        if (string.IsNullOrEmpty(d)) return;
        if (!ServerControl.IsValidClient(d))
        {
            Ui.Warn("这个目录里没有 DNF.exe 或 Script.pvf，不像游戏客户端目录。\n\n"
                  + "客户端目录里应当能看到：\n"
                  + "  DNF.exe\n  Script.pvf\n  ImagePacks2\\\n  A5.dll（补丁，见首页状态）", "目录不对");
            return;
        }
        AppSys.ClientDir = d;
        AppSys.Settings.ClientDir = d;
        AppSys.SaveSettings();
        AppSys.Log(">>> 客户端目录已设为: " + d, "ok");
        AppSys.RefreshStatus();
        await Task.CompletedTask;
    }

    // =================================================================
    // Java 25 —— 缺了就引导装（包里通常带了安装包）
    // =================================================================
    /*
     * 三个入口共用这一个函数：首次启动自检、点【开始游戏】、设置页的【安装 Java】按钮。
     * 返回 true = "现在能拿到 java.exe 了"。
     *
     * 为什么要有它：服务端 jar 是 Java 25 字节码，机器上没有 Java ≥25 时服务端根本起不来。
     * 打包的人习惯把 JDK 安装包一起塞进包里（约定 <服务端包>\所需环境安装包\），
     * 所以这里不要求玩家自己去下载 —— 直接把找到的安装包摆出来问一句，装完再验一遍。
     */
    /// <param name="refresh">true = 先丢掉缓存重新探测（用户主动动作的路径上要这样，
    /// 他可能刚在别处装完 Java）；AutoSetup 刚探过就传 false，省一遍子进程。</param>
    public static async Task<bool> EnsureJavaAsync(bool refresh = true)
    {
        if (refresh) { ServerControl.InvalidateJava(); ServerControl.InvalidateJavaInstaller(); }
        string err;
        if (!string.IsNullOrEmpty(ServerControl.FindJava(out err))) return true;

        var msi = ServerControl.FindJavaInstaller();
        if (string.IsNullOrEmpty(msi))
        {
            Ui.Warn("这台机器上没有 Java 25，服务端起不来。\n\n"
                  + "包里也没找到 Java 安装包（*.msi / jdk*.exe）。\n\n"
                  + "请自行安装 Java 25（或把安装包放进包里，启动器会自动找到它），"
                  + "装完再点【开始游戏】。", "缺少 Java 25");
            AppSys.Log("[Java] 没有找到 Java 25，包里也没有安装包。请自行安装 Java ≥25。", "warn");
            return false;
        }

        var r = Ui.Confirm(
            "这台机器上还没有 Java 25 —— 服务端跑不起来。\n\n"
          + "这个包里带了安装包：\n"
          + msi + "\n\n"
          + "要现在安装吗？\n"
          + "（会弹出 Windows 的提权确认，点【是】；安装大约 1~2 分钟，期间请勿关闭启动器）",
            "需要先安装 Java 25");
        if (r != System.Windows.MessageBoxResult.Yes)
        {
            AppSys.Log("[Java] 玩家选择暂不安装。装好后点【开始游戏】即可。", "gray");
            return false;
        }

        AppSys.ShowProgress();
        AppSys.SetStep(30, "正在安装 Java 25...");
        AppSys.Busy = true;
        try
        {
            var res = await ServerControl.InstallJavaAsync(msi, m => AppSys.Log(m, "gray"));
            if (!res.Ok)
            {
                AppSys.Log("[Java] 安装失败: " + res.Error.Replace("\n", " "), "err");
                Ui.Err("Java 25 没装上：\n\n" + res.Error, "安装失败");
                return false;
            }
            AppSys.Log(">>> Java 25 已就绪: " + res.JavaPath + "（" + res.JavaVersion + "）", "ok");
            if (res.NeedReboot)
                AppSys.Log("    安装程序建议重启电脑；如果后面起服务端有问题，重启一次再试。", "warn");
            AppSys.SetStep(100);
            AppSys.RefreshStatus();
            return true;
        }
        finally
        {
            AppSys.Busy = false;
            AppSys.FinishProgress();
        }
    }

    // =================================================================
    // 服务端
    // =================================================================
    /// <summary>
    /// 确保服务端在跑: 没跑就【直接起】。
    /// 不再有"要现在启动服务端吗？"那一问 —— 启动器上点【开始游戏】本身就是"我要玩"，
    /// 再弹一个确认纯属多此一举 (2026-10-08 按用户要求去掉)。
    ///
    /// 【返回 true 的语义 = "游戏端口现在可用, 客户端连得上"】(2026-10-09 收紧)
    ///   进程在但端口还没起来 / 端口被别人占着, 都不算 true —— 否则 Play 会直接
    ///   去拉客户端, 客户端连不上, 表现就是**卡在它自己的加载界面**(用户报的"卡在 5%")。
    ///
    /// 返回 false 代表"服务端没备好, 调用方不该继续"。
    /// </summary>
    public static async Task<bool> EnsureServerAsync()
    {
        var root = AppSys.ServerDir;
        if (!ServerControl.IsValidRoot(root))
        {
            Ui.Warn("还没定位到服务端目录。\n\n请到【设置 → 目录定位】指定能看到 server\\s1a5-*-all.jar 的那一层。", "服务端目录未设置");
            AppSys.Go(NavKey.Settings);
            return false;
        }

        int port = AppSys.Settings.GamePort;

        /*
         * 已经在跑? —— 但"在跑"必须是**真的在听端口**, 不能只看进程。
         * 只看进程的话, 服务端还在解析 Script.pvf 的那几十秒里会被当成"备好了",
         * 客户端连上去就是无限等待。日志里"检测到服务端已在运行"后面跟一片空白
         * 的那次事故就是这么来的。
         */
        if (ServerControl.IsServerRunning(root))
        {
            if (ServerControl.PortIsListening(port))
            {
                AppSys.Log(">>> 服务端已在运行（端口 " + port + " 就绪）。", "gray");
                return true;
            }
            // 进程在、端口没起: 等一小会儿 (它多半正在解析 PVF), 还是没起就报错,
            // 让玩家去冷启动一次, 而不是把客户端扔进"连不上"的黑洞。
            AppSys.Log(">>> 检测到服务端进程，但游戏端口 " + port + " 还没就绪 —— 等它 30 秒...", "warn");
            AppSys.ShowProgress();
            AppSys.SetStep(8, "等待服务端端口就绪...");
            try
            {
                for (int i = 0; i < 60; i++)
                {
                    if (ServerControl.PortIsListening(port))
                    {
                        AppSys.Log(">>> 游戏端口 " + port + " 已就绪。", "ok");
                        return true;
                    }
                    if (!ServerControl.IsServerRunning(root)) break;   // 进程没了, 往下走去启动
                    await Task.Delay(500);
                }
            }
            finally { AppSys.FinishProgress(); }

            if (!ServerControl.IsServerRunning(root))
                AppSys.Log(">>> 服务端进程已退出，改为重新启动。", "warn");
            else
            {
                AppSys.Log("[服务端] 进程在跑但端口 " + port + " 一直没就绪 —— 可能卡在解析 Script.pvf。", "err");
                Ui.Err("服务端进程在跑，但游戏端口 " + port + " 30 秒内都没就绪。\n\n"
                     + "常见原因：它卡在解析 Script.pvf，或者上一次没退干净。\n\n"
                     + "请在任务管理器里结束残留的 java.exe，再用【开始游戏】重来一次。",
                       "服务端没有就绪");
                return false;
            }
        }

        // 没装 Java 就先解决 Java（包里一般带了安装包，会问一句然后自动装）
        if (!await EnsureJavaAsync()) return false;

        AppSys.ShowProgress();
        AppSys.SetStep(8, "正在启动服务端...");
        AppSys.Busy = true;
        try
        {
            var res = await ServerControl.StartServerAsync(root, m => AppSys.Log(m, "gray"));
            if (!res.Ok)
            {
                AppSys.Log("[服务端] 启动失败: " + res.Error.Replace("\n", " "), "err");
                Ui.Err(res.Error, "服务端启动失败");
                return false;
            }
            AppSys.Log(res.AlreadyRunning
                ? ">>> 服务端本来就在运行。"
                : ">>> 服务端已启动（PID " + res.Pid + "）。", "ok");
            AppSys.Log("    服务端有自己的控制台窗口，请保持它开着 —— 关掉那个窗口就等于结束这次游玩。", "gray");
            AppSys.SetStep(30);
            AppSys.RefreshStatus();
            return true;
        }
        finally
        {
            AppSys.Busy = false;
            AppSys.FinishProgress();
        }
    }

    public static async Task StopServer()
    {
        var root = AppSys.ServerDir;
        if (!ServerControl.IsValidRoot(root)) { Ui.Warn("还没定位到服务端目录。", "无法停止"); return; }
        if (!ServerControl.IsServerRunning(root)) { AppSys.Log("服务端本来就没在运行。", "gray"); return; }

        if (Ui.Confirm("要停止服务端吗？\n\n服务端会先走正常退出流程（把预写日志合并进存档），"
                     + "然后再关。游戏里请先正常下线。", "停止服务端") != System.Windows.MessageBoxResult.Yes)
            return;

        AppSys.Busy = true;
        AppSys.ShowProgress();
        AppSys.SetStep(20, "正在停止服务端...");
        try
        {
            var res = await ServerControl.StopServerAsync(root, m => AppSys.Log(m, "gray"));
            if (!res.Ok)
            {
                AppSys.Log("[服务端] " + res.Error, "err");
                Ui.Err(res.Error, "服务端没有停下来");
                return;
            }
            if (!res.WasRunning)
            {
                AppSys.Log(">>> 服务端本来就没在运行。", "gray");
            }
            else if (res.Graceful)
            {
                AppSys.Log(">>> 服务端已正常退出" + (res.CtrlCSent ? "（响应了 Ctrl+C）" : "（自己退出的）") + "。", "ok");
                if (res.WalCleaned)
                    AppSys.Log("    存档已完成最终同步（预写日志已回收），现在备份是安全的。", "ok");
                else
                    AppSys.Log("    提示：预写日志文件还在，说明可能有未合并的改动；建议先启动一次再正常停止。", "warn");
            }
            else
            {
                AppSys.Log(">>> 服务端已被强制结束（它 20 秒内没有响应正常退出请求）。", "warn");
                AppSys.Log("    强制结束可能留下未合并的预写日志；如需备份存档，建议先启动服务端再正常停止一次。", "warn");
            }
            AppSys.SetStep(100);
            AppSys.RefreshStatus();
        }
        finally
        {
            AppSys.Busy = false;
            AppSys.FinishProgress();
        }
    }

    public static async Task RestartServer()
    {
        var root = AppSys.ServerDir;
        if (!ServerControl.IsValidRoot(root)) { Ui.Warn("还没定位到服务端目录。", "无法重启"); return; }

        if (ServerControl.IsServerRunning(root))
        {
            if (Ui.Confirm("重启服务端 = 先正常停止再启动。\n\n"
                         + "游戏里请先正常下线，否则会掉线。\n\n要继续吗？", "重启服务端")
                != System.Windows.MessageBoxResult.Yes) return;

            AppSys.Busy = true;
            AppSys.ShowProgress();
            AppSys.SetStep(15, "正在停止服务端...");
            try
            {
                var res = await ServerControl.StopServerAsync(root, m => AppSys.Log(m, "gray"));
                if (!res.Ok) { Ui.Err(res.Error, "重启失败"); return; }
                AppSys.Log(">>> 服务端已停止。", "ok");
            }
            finally { AppSys.Busy = false; AppSys.FinishProgress(); }
        }
        await EnsureServerAsync();
    }

    // =================================================================
    // 网页 GM 工具
    // =================================================================
    /*
     * 打开 GM 工具 = 启动网页 GM (java -cp fat.jar WebGmMain)，然后开浏览器。
     *
     * 【写库规矩】服务端在跑时网页 GM 只读不写 —— 要发道具 / 调等级 / 改背包,
     * 先停止服务端, 写完再启动回来。GM 每次写库前会自动把整个库备份进
     * data\webgm-backups\ (存档页的列表里能看到这些备份)。
     */
    public static async Task OpenGmTool()
    {
        var root = AppSys.ServerDir;
        if (!ServerControl.IsValidRoot(root))
        {
            Ui.Warn("还没定位到服务端目录。\n\n请到【设置 → 目录定位】指定能看到 server\\s1a5-*-all.jar 的那一层。", "服务端目录未设置");
            AppSys.Go(NavKey.Settings);
            return;
        }

        if (ServerControl.IsServerRunning(root))
        {
            AppSys.Log(">>> 服务端正在运行：网页 GM 此时【只读不写】——发道具 / 调等级 / 改背包"
                     + "需要先停止服务端。", "ok");
        }
        else
        {
            AppSys.Log(">>> 服务端未运行：网页 GM 可以直接写存档库（发道具 / 调等级 / 改背包）。", "ok");
        }

        AppSys.ShowProgress();
        try
        {
            AppSys.SetStep(60, "正在打开网页 GM...");
            var webResult = await ServerControl.OpenGmToolAsync(root, m => AppSys.Log(m, "gray"));
            if (!webResult.Ok)
            {
                AppSys.Log("[网页 GM] " + webResult.Error.Replace("\n", " "), "err");
                Ui.Err(webResult.Error, "网页 GM 打不开");
                return;
            }
            var url = "http://127.0.0.1:" + AppSys.Settings.WebGmPort;
            AppSys.Log(webResult.AlreadyRunning
                ? ">>> 网页 GM 本来就在运行，已把浏览器指过去：" + url
                : ">>> 网页 GM 已打开：" + url + "（关掉它的控制台窗口即停止）", "ok");
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                })) { }
            }
            catch (Exception ex) { AppSys.Log("[网页 GM] 打不开浏览器: " + ex.Message + "（地址： " + url + "）", "warn"); }
            AppSys.SetStep(100);
            AppSys.RefreshStatus();
        }
        finally { AppSys.FinishProgress(); }
    }

    // =================================================================
    // 游戏客户端运行状态
    //
    // 【为什么要有"查不出来"这一档】早先只有一个 bool: 找到 DNF 进程但读不出
    // 它的路径时(权限不足 / 位数不匹配, MainModule 会抛), catch 掉就当"没在跑"。
    // 于是【应用更改】会放行 —— 然后去删/覆盖一个正被游戏映射着的 dll。
    // 对"删除 / 覆盖"这类动作, "不知道"必须当成"可能开着"来对待。
    // =================================================================
    public enum ClientRunState
    {
        /// <summary>没找到任何 DNF 进程</summary>
        NotRunning,
        /// <summary>找到了, 而且路径就在我们这份客户端目录里</summary>
        Running,
        /// <summary>找到了 DNF 进程, 但读不出它的路径 —— 无法确认是不是这一份</summary>
        Unverifiable,
    }

    public static ClientRunState GameClientState()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("DNF"); }
        catch { return ClientRunState.NotRunning; }

        var dir = AppSys.ClientDir;
        bool sawUnverifiable = false;
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        sawUnverifiable = true;   // 进程在, 但拿不到路径
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(dir)
                        && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        return ClientRunState.Running;
                    // 路径读得出来、但不是我们这份客户端 —— 别人家的 DNF, 不拦
                }
                catch { sawUnverifiable = true; }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }

        return sawUnverifiable ? ClientRunState.Unverifiable : ClientRunState.NotRunning;
    }

    /// <summary>游戏是不是正在跑（只认"确认在跑"；"查不出来"见 GameClientState）</summary>
    public static bool IsGameClientRunning() => GameClientState() == ClientRunState.Running;

    // =================================================================
    // 闪屏修复 (防闪屏补丁 → 客户端根目录, 见 Services\FlickerFixService.cs)
    // =================================================================
    /*
     * 首页【闪屏修复】开关背后的流程。勾选 = 把随包的补丁复制进客户端根目录
     * （模式 = 设置里的 FlickerFixMode: 电脑 = dgVoodoo2 整夹 / Wine = 原生修复两个 dll）;
     * 取消 = 撤下我们的副本并还原备份过的原文件。
     * 另有两处"保险"入口共用 EnsureFlickerFixDeployed():
     *   · 启动自检 (AutoSetup): 上次勾着、客户端一就位就把补丁补齐;
     *   · 开始游戏 (Play): 进游戏前再补一次 (玩家可能手动删过/重解过客户端)。
     *
     * 【返回 true = 开关状态成立】false = 被拦下, 调用方 (HomeView) 要把开关弹回。
     *   只有两种情况拦: 有流程在跑 (Busy) / 游戏运行中想撤下 (D3D8.dll 正被进程
     *   映射, 删不掉)。部署失败 (补丁文件夹缺失等) 不弹回 —— 开关保持勾选、
     *   状态点变橙, 等补丁文件夹回来后 AutoSetup/Play 会自动补上。
     */
    public static async Task<bool> ApplyFlickerFixAsync(bool on) => await ApplyFlickerFixAsync(on, AppSys.Settings.FlickerFixMode);

    public static async Task<bool> ApplyFlickerFixAsync(bool on, string mode)
    {
        if (AppSys.Busy)
        {
            AppSys.Log("有流程正在执行，请稍候再改闪屏修复开关。", "warn");
            return false;
        }

        if (!on && GameClientState() != ClientRunState.NotRunning)
        {
            AppSys.Log("[闪屏修复] 游戏正在运行 —— D3D8.dll 正被游戏占用，先退出游戏再关闭闪屏修复。", "err");
            return false;
        }

        AppSys.Settings.FlickerFixEnabled = on;
        AppSys.Settings.FlickerFixMode = FlickerFixService.NormalizeMode(mode);
        AppSys.SaveSettings();

        var client = AppSys.ClientDir;
        if (!ServerControl.IsValidClient(client))
        {
            AppSys.Log("[闪屏修复] 还没定位到客户端目录 —— 开关状态已记住，"
                     + "等定位到客户端（或点【开始游戏】）时会自动把补丁补进去。", "warn");
            AppSys.FireStatus();
            return true;
        }

        string err = null;
        var ok = on
            ? await Task.Run(() => FlickerFixService.Deploy(client, AppSys.Settings.FlickerFixMode, m => AppSys.Log(m, "gray"), out err))
            : await Task.Run(() => FlickerFixService.RemoveDeployed(client, m => AppSys.Log(m, "gray"), out err));
        if (ok) AppSys.Log(on ? ">>> 闪屏修复已开启（" + (AppSys.Settings.FlickerFixMode == FlickerFixService.ModeWine ? "Wine 补丁" : "dgVoodoo 补丁") + "，下次进游戏生效）。"
                              : ">>> 闪屏修复已关闭，客户端目录已恢复原状。", "ok");
        else AppSys.Log("[闪屏修复] " + err, "err");
        AppSys.RefreshStatus();
        return true;
    }

    /// <summary>
    /// 确保"勾选了闪屏修复"时补丁真的在客户端目录里。只补缺, 不报弹窗 ——
    /// 它是保险动作, 失败只 warn, 绝不拦【开始游戏】(闪屏是体验问题, 不是硬前提)。
    /// </summary>
    public static async Task EnsureFlickerFixDeployed()
    {
        try
        {
            if (!AppSys.Settings.FlickerFixEnabled) return;
            var client = AppSys.ClientDir;
            if (!ServerControl.IsValidClient(client)) return;
            if (FlickerFixService.IsDeployedAt(client, AppSys.Settings.FlickerFixMode)) return;

            string err = null;
            var ok = await Task.Run(() => FlickerFixService.Deploy(client, AppSys.Settings.FlickerFixMode, m => AppSys.Log(m, "gray"), out err));
            AppSys.Log(ok ? ">>> 闪屏修复补丁已就位。"
                          : "[闪屏修复] 自动补丁失败: " + err + "（不影响启动，可稍后重新勾选一次）",
                        ok ? "gray" : "warn");
        }
        catch (Exception ex) { AppSys.Log("[闪屏修复] 自动补丁出错: " + ex.Message, "warn"); }
    }

    public static async Task Play()
    {
        if (AppSys.Busy) { AppSys.Log("有流程正在执行，请稍候。", "warn"); return; }

        // --- 1. 客户端目录 ---
        if (!ServerControl.IsValidClient(AppSys.ClientDir))
        {
            var found = ServerControl.ResolveClientDir(AppSys.ServerDir);
            if (ServerControl.IsValidClient(found))
            {
                AppSys.ClientDir = found;
                AppSys.Settings.ClientDir = found;
                AppSys.SaveSettings();
                AppSys.Log(">>> 已自动定位客户端目录: " + found, "ok");
            }
            else
            {
                var r = Ui.Confirm(
                    "还没找到游戏客户端目录（含 DNF.exe 与 Script.pvf）。\n\n"
                  + "要现在手工指定吗？\n（客户端目录就是能看到 DNF.exe、ImagePacks2 的那一层）",
                    "需要指定客户端目录");
                if (r != System.Windows.MessageBoxResult.Yes) return;
                await LocateClientDir();
                if (!ServerControl.IsValidClient(AppSys.ClientDir)) return;
            }
        }

        // --- 1.5 闪屏修复: 勾选了就确保补丁在客户端目录里 (失败只 warn, 不拦启动) ---
        await EnsureFlickerFixDeployed();

        // --- 2. A5.dll 客户端补丁提示 (没有它杀怪不上报, 但不拦启动) ---
        if (!File.Exists(Path.Combine(AppSys.ClientDir, "A5.dll")))
        {
            AppSys.Log("[警告] 客户端目录里没有 A5.dll —— 杀怪不会上报，怪物打不死。", "warn");
            var r = Ui.Confirm(
                "客户端目录里没有 A5.dll（客户端补丁）。\n\n"
              + "没有它，击杀怪物不会上报给服务端 —— 怪的血打空也不会死。\n\n"
              + "仍要继续启动游戏吗？", "缺少 A5.dll");
            if (r != System.Windows.MessageBoxResult.Yes) return;
        }

        // --- 3. 服务端得在跑 (进游戏前必须开; 直接起, 不再问一句) ---
        if (!await EnsureServerAsync()) return;

        // --- 4. 已经在游戏里了? ---
        if (IsGameClientRunning())
        {
            var r = Ui.Confirm("检测到游戏客户端已经在运行了。\n\n要再开一个吗？", "游戏已在运行");
            if (r != System.Windows.MessageBoxResult.Yes) return;
        }

        // --- 5. 启动 (DNF.exe 命令行 16 段 '?' 分隔, 与 client-10038.cmd 完全一致) ---
        AppSys.ShowProgress();
        AppSys.SetStep(70, "正在启动游戏客户端...");
        try
        {
            var exe = Path.Combine(AppSys.ClientDir, "DNF.exe");
            if (!File.Exists(exe))
            {
                Ui.Err("客户端目录里找不到 DNF.exe。", "启动失败");
                return;
            }
            var s = AppSys.Settings;
            var account = (s.Account ?? "").Trim();
            var password = (s.Password ?? "").Trim();
            if (account.Length == 0 || password.Length == 0)
            {
                Ui.Warn("账号或密码为空 —— 客户端命令行的段不能为空。\n\n请到【设置 → 连接与账号】补全。", "账号未设置");
                AppSys.Go(NavKey.Settings);
                return;
            }
            var args = "10?127.0.0.1?" + s.GamePort + "?" + account + "?" + password
                     + "?1?1?0?0?0?0?1?0?0?0?19847";
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = AppSys.ClientDir,
                UseShellExecute = true
            };
            var p = Process.Start(psi);
            AppSys.Log(">>> 已启动游戏客户端: " + exe + (p != null ? "（PID " + p.Id + "）" : ""), "ok");
            AppSys.Log("    直连 127.0.0.1:" + s.GamePort + "，账号 " + account + "。", "gray");
            AppSys.SetStep(100);
        }
        catch (Exception ex)
        {
            AppSys.Log("[游戏] 启动失败: " + ex.Message, "err");
            Ui.Err("启动游戏客户端失败：\n\n" + ex.Message, "启动失败");
        }
        finally { AppSys.FinishProgress(); }
        // 常驻标记: Play 是"一键"的全部, 走到这里说明服务端与客户端都处理完了。
        // BootMark 单独落盘(不受 2 秒节流), 事后排查"卡在哪一步"就靠它。
        AppSys.BootMark("Play: 一键流程结束");
    }

    // =================================================================
    // 存档
    // =================================================================
    public static async Task BackupArchive()
    {
        var root = AppSys.ServerDir;
        if (!ArchiveService.CanWrite(root, out var why)) { Ui.Warn(why, "现在不能备份"); return; }

        var db = ServerControl.ReadDbInfo(root);
        if (!db.Exists) { Ui.Warn("还没有存档可以备份。\n\n先启动一次服务端，它会自动建库。", "没有存档"); return; }

        var name = Ui.PromptText("备份存档", "给这份存档起个名字（留空 = 用当前时间）",
            "存档_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "会把 s1a5.db 三件套（主库 + -wal + -shm）一起备份。名字里不能有 \\ / : * ? \" < > | 这些字符。");
        if (name == null) return;

        string err;
        var got = ArchiveService.Backup(root, name, out err);
        if (got == null)
        {
            AppSys.Log("[存档] 备份失败: " + err, "err");
            Ui.Err("备份失败：\n\n" + err, "备份失败");
            return;
        }
        AppSys.Log(">>> 存档已备份: " + got + "（" + (db.Size / 1048576.0).ToString("0.0") + " MB）", "ok");
        AppSys.RefreshStatus();
        await Task.CompletedTask;
    }

    public static async Task RestoreArchive(ArchiveEntry e)
    {
        var root = AppSys.ServerDir;
        if (!ArchiveService.CanWrite(root, out var why)) { Ui.Warn(why, "现在不能还原"); return; }

        if (Ui.Confirm(
                "要还原成这份存档吗？\n\n"
              + "  " + e.Name + "\n"
              + "  " + e.SourceText + " · " + e.TimeText + " · " + e.SizeText + "\n\n"
              + "当前存档会先自动备份成 _还原前_<时间戳>，随时可以回退。\n"
              + "还原完成后需要重新启动服务端。",
                "还原存档", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
            return;

        AppSys.ShowProgress();
        AppSys.SetStep(20, "正在还原存档...");
        try
        {
            string err;
            if (!ArchiveService.Restore(root, e, out err))
            {
                AppSys.Log("[存档] 还原失败: " + err, "err");
                Ui.Err("还原失败：\n\n" + err, "还原失败");
                return;
            }
            AppSys.Log(">>> 存档已还原: " + e.Name, "ok");
            AppSys.Log("    下次启动服务端会用还原后的存档。", "gray");
            AppSys.SetStep(100);
            AppSys.RefreshStatus();
        }
        finally { AppSys.FinishProgress(); }
        await Task.CompletedTask;
    }

    public static async Task ResetGame()
    {
        var root = AppSys.ServerDir;
        if (!ArchiveService.CanWrite(root, out var why)) { Ui.Warn(why, "现在不能重开"); return; }

        if (Ui.Confirm(
                "重新开局 = 删掉当前存档（s1a5.db 三件套），下次启动服务端会建一个全新的空库。\n\n"
              + "角色、等级、道具全部清空，且不能撤销。\n\n"
              + "建议先点【备份当前存档】留一份。\n\n确定要重新开局吗？",
                "重新开局", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
            return;

        string err;
        if (!ArchiveService.ResetGame(root, out err))
        {
            Ui.Err("重新开局失败：\n\n" + err, "失败");
            return;
        }
        AppSys.Log(">>> 存档已清空，重新开局。下次启动服务端会建新库（账号会在进游戏时自动创建）。", "ok");
        AppSys.RefreshStatus();
        await Task.CompletedTask;
    }
}
