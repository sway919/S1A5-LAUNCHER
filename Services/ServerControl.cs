/*
 * ==================================================================
 * 服务端控制 (Services/ServerControl.cs) — S1A5 版
 * ==================================================================
 * S1A5 的服务端不是 60US 那种 C# exe, 而是【一个 Java 单进程】:
 *   java -jar server\s1a5-*-all.jar {GamePort}
 * 网页 GM 也是 java, 用 -cp 挂同一个 fat jar 跑 com.dylan.tools.webgm.WebGmMain。
 * 两个进程名都叫 java.exe —— 所以识别/停止都必须【按命令行匹配】(WMI),
 * 不能像 60US 那样按进程名找。
 *
 * 公开面 (AppSys/Flows/ArchiveService 引用的一字不改):
 *   IsValidRoot / IsValidClient / ResolveServerDir / ResolveClientDir /
 *   IsServerRunning / IsWebGmRunning / ServerPid / IsDbLocked /
 *   StartServerAsync / StopServerAsync / OpenGmToolAsync /
 *   KillWebGm / KillGmTool /
 *   PortIsListening / ReadDbInfo /
 *   DefaultGamePort / DefaultGmPort / DbPath / DbWal / DbShm / LogDir / ScriptPath
 *   + StartResult / StopResult
 *
 * 【JDK 25 探测】服务端 jar 是 Java 25 字节码, Java 21 起不来 —— 所以每个
 *   候选 java.exe 都要真的跑一遍 java -version 解析主版本号, ≥25 才算数
 *   (机器 PATH 上有 Java 21 时绝不能直接认)。
 *
 * 【数据库】data\s1a5.db 单文件 SQLite, WAL 开着 = 活库是 .db/.db-wal/.db-shm
 *   三个。没有 60US 的 gm-lock 文件 —— IsDbLocked 恒 false。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Management;

namespace ServerUI.Services;

public static class ServerControl
{
    // =================================================================
    // 常量
    // =================================================================
    public const int DefaultGamePort = 10011;   // 服务端 -Ds1a5.port 的出厂默认
    public const int DefaultGmPort = 5052;      // 网页 GM --port 的出厂默认
    public const string DbName = "s1a5.db";     // 存档主库 (ArchiveService 也在用)

    const string JavaMinMajor = "25";           // 服务端 jar 的字节码版本

    // =================================================================
    // 目录判定
    // =================================================================
    /// <summary>服务端 fat jar (server\s1a5-*-all.jar, 开发形态 target\)</summary>
    public static string JarPath(string root)
    {
        if (string.IsNullOrEmpty(root)) return "";
        try
        {
            foreach (var sub in new[] { "server", "target" })
            {
                var dir = Path.Combine(root, sub);
                if (!Directory.Exists(dir)) continue;
                var hit = Directory.GetFiles(dir, "s1a5-*-all.jar")
                                   .OrderBy(f => f).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        catch { }
        return "";
    }

    /// <summary>AppRoot = 服务端包根：找得到 fat jar, 或 client+data 子目录都在</summary>
    public static bool IsValidRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (!string.IsNullOrEmpty(JarPath(dir))) return true;
        return Directory.Exists(Path.Combine(dir, "client"))
            && Directory.Exists(Path.Combine(dir, "data"));
    }

    /// <summary>客户端目录 = DNF.exe 与 Script.pvf 都在的那一层</summary>
    public static bool IsValidClient(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "DNF.exe"))
            && File.Exists(ScriptPath(dir));
    }

    /// <summary>客户端必需文件清单 (缺哪个报哪个)</summary>
    public static readonly string[] ClientFiles = { "DNF.exe", "Script.pvf", "ImagePacks2" };

    public static List<string> MissingClientFiles(string dir)
    {
        var miss = new List<string>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return miss;
        foreach (var f in ClientFiles)
        {
            var p = Path.Combine(dir, f);
            if (!File.Exists(p) && !Directory.Exists(p)) miss.Add(f);
        }
        return miss;
    }

    public static string ScriptPath(string clientDir) => Path.Combine(clientDir ?? "", "Script.pvf");

    /// <summary>
    /// 自动定位服务端包根：
    ///   ① 从 EXE 所在目录【向上】最多 6 层（启动器放在包根、或包根下任意子目录里都能找到）；
    ///   ② 再【横向】找 —— 服务端包根可能是与 EXE 所在目录同级的某个目录。
    /// 第 ② 条是给这种布局加的：
    ///     游玩\ACT5\S1a5-Launcher.exe      ← 启动器跟客户端放在一起
    ///     游玩\服务端+源码\target\*.jar     ← 服务端是它的"兄弟"
    /// 只往上走永远碰不到兄弟目录。横向只探 2 层并跳过素材目录（客户端那边几千个 NPK，
    /// 绝不能去翻），所以这条扫描是毫秒级的。
    /// <param name="deep">false = 只做便宜的上溯（状态轮询每 2 秒调一次，别每次都横向扫）。</param>
    /// </summary>
    public static string ResolveServerDir(string hint, bool deep = true)
    {
        var starts = new List<string> { AppContext.BaseDirectory };
        if (!string.IsNullOrWhiteSpace(hint)) starts.Add(hint);
        try { starts.Add(Environment.CurrentDirectory); } catch { }

        foreach (var s in starts)
        {
            var cur = s;
            for (int i = 0; i <= 6 && !string.IsNullOrEmpty(cur); i++)
            {
                if (IsValidRoot(cur)) return Path.GetFullPath(cur);
                var parent = Path.GetDirectoryName(cur.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == cur || string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }
        }
        if (!deep) return "";

        foreach (var s in starts)
        {
            var cur = s;
            for (int i = 0; i <= 2 && !string.IsNullOrEmpty(cur); i++)
            {
                var hit = ScanDownForRoot(cur, 2);
                if (!string.IsNullOrEmpty(hit)) return hit;
                var parent = Path.GetDirectoryName(cur.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == cur || string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }
        }
        return "";
    }

    /* 明显是客户端素材 / 构建产物 / 系统目录的名字 —— 自动扫描时直接跳过 */
    static readonly string[] ScanSkipDirs =
    {
        "ImagePacks2", "ImagePacks3", "ImagePacks4", "SoundPacks", "Music", "Video", "fonts",
        "runtime", "logs", "data", "src", "target", "obj", "bin", "node_modules",
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "Program Files (x86)"
    };

    static string ScanDownForRoot(string dir, int depth)
    {
        if (depth <= 0) return "";
        try
        {
            if (!Directory.Exists(dir)) return "";
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (IsValidRoot(d)) return Path.GetFullPath(d);
                if (Array.IndexOf(ScanSkipDirs, name) >= 0) continue;
                var deeper = ScanDownForRoot(d, depth - 1);
                if (!string.IsNullOrEmpty(deeper)) return deeper;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 自动定位客户端目录，四步：
    ///   ① 服务端包根下的 client\（一键端那种布局）；
    ///   ② **启动器自己就躺在客户端目录里**（本项目 游玩\ACT5\ 那种布局）—— 优先认脚下这个；
    ///   ③ 服务端根的下级目录里找；
    ///   ④ 与 EXE 同级/上级的目录里找（跳过素材目录）。
    /// </summary>
    public static string ResolveClientDir(string serverRoot)
    {
        if (IsValidClient(Path.Combine(serverRoot ?? "", "client")))
            return Path.Combine(serverRoot, "client");
        try
        {
            var self = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (IsValidClient(self)) return self;
        }
        catch { }
        try
        {
            if (!string.IsNullOrEmpty(serverRoot) && Directory.Exists(serverRoot))
                foreach (var d in Directory.GetDirectories(serverRoot))
                    if (IsValidClient(d)) return d;
        }
        catch { }
        try
        {
            var cur = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            for (int i = 0; i <= 3 && !string.IsNullOrEmpty(cur); i++)
            {
                foreach (var d in Directory.GetDirectories(cur))
                {
                    if (Array.IndexOf(ScanSkipDirs, Path.GetFileName(d)) >= 0) continue;
                    if (IsValidClient(d)) return d;
                }
                var parent = Path.GetDirectoryName(cur);
                if (parent == cur || string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }
        }
        catch { }
        return "";
    }

    // =================================================================
    // 路径
    // =================================================================
    public static string DbPath(string root) => Path.Combine(root ?? "", "data", DbName);
    public static string DbWal(string root) => DbPath(root) + "-wal";
    public static string DbShm(string root) => DbPath(root) + "-shm";
    public static string LogDir(string root) => Path.Combine(root ?? "", "logs");
    public static string GmBackupDir(string root) => Path.Combine(root ?? "", "data", "webgm-backups");

    /// <summary>60US 的 gm-lock 在 S1A5 不存在 —— 恒为未锁</summary>
    public static bool IsDbLocked(string root) => false;

    // =================================================================
    // java.exe 探测 (必须 ≥ 25)
    // =================================================================
    static string _javaPath;
    static string _javaVersion;
    static string _javaError;
    static bool _javaProbed;

    /// <summary>上一次探测到的 java 路径与版本号 (设置页环境信息用)</summary>
    public static string JavaPath => _javaPath ?? "";
    public static string JavaVersionText => _javaVersion ?? "未探测";

    /// <summary>
    /// 丢掉"已探测"缓存, 强制下次重新扫。
    /// 【失败也要缓存】—— 每次探测都要给若干候选跑 `java -version` 子进程, 一遍 300ms~1s;
    /// 不缓存失败结果的话, 设置页/状态刷新每调一次 FindJava 就卡一次 (实测打开设置页 732ms)。
    /// 玩家装了 Java 之后靠这个 InvalidateJava() 再查（【重新检测】按钮 / 起服务端前 / 装完）。
    /// </summary>
    public static void InvalidateJava()
    {
        _javaPath = null;
        _javaVersion = null;
        _javaError = null;
        _javaProbed = false;
    }

    /// <summary>找 java.exe：包内 jdk25 → 上溯若干级 → PATH → JAVA_HOME → 常见安装目录。每个候选都跑 -version 验证 ≥25。</summary>
    public static string FindJava(out string error)
    {
        if (_javaProbed)
        {
            error = _javaError;
            return _javaPath ?? "";
        }
        error = null;

        var candidates = new List<string>();
        // 包根 / EXE 目录及其上四级 —— 覆盖 jdk25\ 与 tools\jdk25\ 两个约定位置。
        // 上溯四级是为了 游玩\ACT5\S1a5-Launcher.exe 这种"启动器埋在客户端目录里"的布局:
        // 便携 JDK 放在 游玩\jdk25\ 也要能找到。
        var roots = new List<string>();
        var hint = ServerUI.Core.AppSys.ServerDir;
        if (!string.IsNullOrWhiteSpace(hint)) roots.Add(hint);
        // 服务端根的上一级也看一眼（jar 在 服务端+源码\target 下时, JDK 常在它旁边）
        try
        {
            var p = Path.GetDirectoryName((hint ?? "").TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }
        catch { }
        var baseDir = AppContext.BaseDirectory;
        for (int i = 0; i <= 4; i++)
        {
            try
            {
                var cur = baseDir;
                for (int k = 0; k < i; k++) cur = Path.GetDirectoryName(cur.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(cur)) roots.Add(cur);
            }
            catch { break; }
        }
        foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(r, "jdk25", "bin", "java.exe"));
            candidates.Add(Path.Combine(r, "tools", "jdk25", "bin", "java.exe"));
        }
        // PATH
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var seg in pathVar.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(seg)) continue;
                candidates.Add(Path.Combine(seg.Trim(), "java.exe"));
            }
        }
        catch { }
        // JAVA_HOME
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(jh)) candidates.Add(Path.Combine(jh, "bin", "java.exe"));
        // 常见安装目录 (安装包默认就装到这些地方, 目录名含 25 —— 全用户装 & 单用户装都扫)
        var pf = new List<string> { @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium",
                                    @"C:\Program Files\Microsoft", @"C:\Program Files\Amazon Corretto",
                                    @"C:\Program Files\Zulu" };
        try
        {
            var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(la))
            {
                pf.Add(Path.Combine(la, "Programs", "Eclipse Adoptium"));
                pf.Add(Path.Combine(la, "Programs", "Microsoft"));
                pf.Add(Path.Combine(la, "Programs", "Java"));
            }
        }
        catch { }
        try
        {
            var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pfx)) pf.Add(Path.Combine(pfx, "Java"));
        }
        catch { }
        foreach (var baseD in pf)
        {
            try
            {
                if (!Directory.Exists(baseD)) continue;
                foreach (var d in Directory.GetDirectories(baseD, "*25*"))
                    candidates.Add(Path.Combine(d, "bin", "java.exe"));
                foreach (var d in Directory.GetDirectories(baseD, "*jdk*"))
                    candidates.Add(Path.Combine(d, "bin", "java.exe"));
            }
            catch { }
        }

        string bestError = "没有找到 Java 25 (java.exe)。";
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (c == null || !File.Exists(c)) continue;
            int major;
            string verText;
            var e2 = ProbeJavaVersion(c, out major, out verText);
            if (e2 != null) { bestError = c + " 跑不起来: " + e2; continue; }
            if (major < 25)
            {
                bestError = c + " 是 Java " + major + "，本服务端需要 Java ≥ 25。";
                continue;
            }
            _javaPath = c;
            _javaVersion = verText;
            _javaError = null;
            _javaProbed = true;
            return c;
        }
        // 失败也记下来（原因一起缓存），否则每次调用都要重跑一遍 java -version
        _javaProbed = true;
        _javaError = bestError;
        error = bestError;
        return "";
    }

    /// <summary>跑 java -version 解析主版本号 ("25"、"25.0.1"、"1.8.0_xxx" 都认)</summary>
    static string ProbeJavaVersion(string javaExe, out int major, out string verText)
    {
        major = 0; verText = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return "超时"; }
                try { Task.WaitAll(new Task[] { so, se }, 3000); } catch { }
                var text = (se.Result ?? "") + "\n" + (so.Result ?? "");
                var m = Regex.Match(text, "version \"([0-9]+)(\\.([0-9]+))?");
                if (!m.Success) return "输出里没有版本号";
                major = int.Parse(m.Groups[1].Value);
                verText = m.Groups[1].Value + (m.Groups[3].Success ? "." + m.Groups[3].Value : "");
                return null;
            }
        }
        catch (Exception ex) { return ex.Message; }
    }

    // =================================================================
    // Java 安装包：在包内找 + 跑起来
    // =================================================================
    /*
     * 【为什么要这套】
     * 打包的人（本项目）习惯把 JDK 安装包一起塞进包里（约定放在
     * <服务端包>\所需环境安装包\jdk-25_windows-x64_bin.msi），玩家什么都不用自己下。
     * 但安装包放在哪一层、叫什么名字，全看打包的人怎么摆 —— 所以这里**不去猜固定路径**，
     * 而是从几个起点做一次有界的广度扫描：只认 *.msi 和 jdk*.exe，只下探 3 层，
     * 跳过素材/系统目录，最多访问 ScanBudget 个目录 —— 保证毫秒级、绝不翻客户端的几千个 NPK。
     */
    const int ScanBudget = 400;

    /// <summary>在包内找 Java 安装包（优先 jdk+25+.msi）。找不到返回 ""（**空结果也缓存** —— 见 InvalidateJavaInstaller）。</summary>
    public static string FindJavaInstaller()
    {
        if (_installerScanned)
        {
            if (!string.IsNullOrEmpty(_installerPath) && File.Exists(_installerPath)) return _installerPath;
            if (string.IsNullOrEmpty(_installerPath)) return "";     // 上次没找到, 不再翻一遍
        }

        var hits = new List<string>();
        var visited = 0;
        var starts = new List<string>();
        try { starts.Add(AppContext.BaseDirectory); } catch { }
        var sd = ServerUI.Core.AppSys.ServerDir;
        if (!string.IsNullOrWhiteSpace(sd) && Directory.Exists(sd)) starts.Add(sd);
        // 上溯两级（EXE 目录 → 包根 → 解压根）
        foreach (var s in starts.ToArray())
        {
            try
            {
                var cur = Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar);
                for (int i = 0; i < 2; i++)
                {
                    var p = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(p) || p == cur) break;
                    starts.Add(p);
                    cur = p;
                }
            }
            catch { }
        }

        foreach (var s in starts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(s, 0));
            while (queue.Count > 0 && visited < ScanBudget)
            {
                var kv = queue.Dequeue();
                var dir = kv.Key;
                var depth = kv.Value;
                visited++;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.msi"))
                        AddInstallerHit(hits, f);
                    foreach (var f in Directory.GetFiles(dir, "jdk*.exe"))
                        AddInstallerHit(hits, f);
                    foreach (var f in Directory.GetFiles(dir, "*jdk*setup*.exe"))
                        AddInstallerHit(hits, f);
                    if (depth >= 3) continue;
                    // 盘符根目录不下探（会变成全盘扫描）
                    var rootOf = Path.GetPathRoot(dir)?.TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), rootOf, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        var nm = Path.GetFileName(d);
                        if (Array.IndexOf(ScanSkipDirs, nm) >= 0) continue;
                        if (nm.StartsWith("$", StringComparison.Ordinal)) continue;
                        queue.Enqueue(new KeyValuePair<string, int>(d, depth + 1));
                    }
                }
                catch { }
            }
        }

        // 排序：名字里同时有 jdk 和 25 的最像；.msi 比 .exe 好（能静默带参数装）；路径浅的优先
        var best = hits
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => { var n = Path.GetFileName(f).ToLowerInvariant(); return (n.Contains("jdk") ? 2 : 0) + (n.Contains("25") ? 1 : 0); })
            .ThenByDescending(f => f.ToLowerInvariant().EndsWith(".msi") ? 1 : 0)
            .ThenBy(f => f.Length)
            .FirstOrDefault();
        _installerPath = best ?? "";
        _installerScanned = true;
        return _installerPath;
    }

    static string _installerPath;
    static bool _installerScanned;

    static void AddInstallerHit(List<string> hits, string f)
    {
        try
        {
            var fi = new FileInfo(f);
            // 小于 20MB 的多半不是 JDK 安装包（Temurin 的 msi/exe 都在 150MB 上下）
            if (fi.Length < 20L * 1024 * 1024) return;
            hits.Add(fi.FullName);
        }
        catch { }
    }

    /// <summary>丢掉安装包路径缓存（玩家把包挪过位置 / 换了个包之后重新找）</summary>
    public static void InvalidateJavaInstaller() { _installerPath = null; _installerScanned = false; }

    public class JavaInstallResult
    {
        public bool Ok;
        public string JavaPath = "";
        public string JavaVersion = "";
        public bool NeedReboot;
        public string Error = "";
    }

    /// <summary>
    /// 跑 Java 安装包（.msi 走 msiexec /passive，.exe 直接跑它自己的向导），等它装完，
    /// 然后重新探测 java.exe。会弹一次 Windows 提权确认（玩家点"是"）。
    /// </summary>
    public static Task<JavaInstallResult> InstallJavaAsync(string installerPath, Action<string> log)
    {
        return Task.Run(() =>
        {
            var res = new JavaInstallResult();
            try
            {
                if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
                {
                    res.Error = "安装包不存在：" + installerPath;
                    return res;
                }
                var ext = Path.GetExtension(installerPath).ToLowerInvariant();
                string exe, args;
                if (ext == ".msi")
                {
                    exe = "msiexec.exe";
                    // /passive: 显示进度、不问问题；/norestart: 别自动重启机器。
                    // 装到默认位置（启动器会自动扫到），同时也让安装程序按自己的默认项配置环境变量。
                    args = "/i \"" + installerPath + "\" /passive /norestart";
                }
                else
                {
                    exe = installerPath;
                    args = "";
                }

                log?.Invoke("正在运行 Java 安装程序：" + Path.GetFileName(installerPath));
                log?.Invoke("    如果弹出 Windows 的提权确认，请点【是】；安装期间请别关掉这个窗口。");

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,      // 需要它去提权
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? ""
                };
                int code = 0;
                using (var p = Process.Start(psi))
                {
                    if (p == null) { res.Error = "安装程序没能启动。"; return res; }
                    p.WaitForExit();
                    code = p.ExitCode;
                }
                if (code == 3010) res.NeedReboot = true;          // 成功但建议重启
                else if (code != 0)
                {
                    res.Error = "安装程序返回 " + code + "（" + MsiCodeText(code) + "）";
                    return res;
                }

                InvalidateJava();
                InvalidateJavaInstaller();
                string err;
                var j = FindJava(out err);
                if (string.IsNullOrEmpty(j))
                {
                    res.Error = "安装程序已经跑完，但还是没找到 java.exe。\n\n" + err
                              + "\n\n装完可能需要重开一次启动器（或者重启电脑）。";
                    return res;
                }
                res.JavaPath = j;
                res.JavaVersion = JavaVersionText;
                res.Ok = true;
                return res;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                res.Error = "安装被取消了（提权确认里点了【否】）。";
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
        });
    }

    /// <summary>把 msiexec 的返回码翻成人话（只翻常见的几个，其余的照原样报）</summary>
    static string MsiCodeText(int code)
    {
        switch (code)
        {
            case 1602: return "用户取消了安装";
            case 1603: return "安装发生致命错误";
            case 1619: return "这个安装包打不开（可能文件损坏或不完整）";
            case 1625: return "被系统策略拦住了";
            case 1633: return "这个安装包不支持当前系统（位数不对？）";
            case 1638: return "已经装了另一个版本";
            case 1641: return "安装完成，正在重启";
            case 3010: return "安装完成，建议重启电脑";
            default: return "未知错误码";
        }
    }

    // =================================================================
    // 进程识别 (WMI 按命令行匹配)
    // =================================================================
    public class ProcHit { public int Pid; public string CmdLine = ""; }

    /// <summary>列出所有 java.exe 的 (PID, 命令行)</summary>
    static List<ProcHit> ListJavaProcs()
    {
        var res = new List<ProcHit>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe'"))
            foreach (var o in searcher.Get())
            {
                try
                {
                    res.Add(new ProcHit
                    {
                        Pid = Convert.ToInt32(o["ProcessId"]),
                        CmdLine = (o["CommandLine"] as string) ?? ""
                    });
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    // =================================================================
    // java 进程清单的缓存 (性能关键)
    // =================================================================
    /*
     * WMI 查 Win32_Process 一次要 150~400ms, 而且在 WPF 里只能同步等 ——
     * 以前每次问"服务端在不在跑"都现场扫一遍, 于是:
     *   · 状态计时器每 2 秒扫一次 → 界面每 2 秒抖一下;
     *   · 【存档】页刷一次读两次状态 → 每次切进去卡 250ms (实测);
     *   · 启停时的轮询循环里每次循环都扫 → 启停也变慢。
     * 现在改成 TTL 缓存 + 后台线程定时预热: 前台永远命中缓存 (微秒级),
     * WMI 扫描挪到线程池, 界面线程不再被它卡住。
     */
    static readonly object _procLock = new object();
    static List<ProcHit> _procCache;
    static DateTime _procCacheAt = DateTime.MinValue;
    static System.Threading.Timer _procWatch;
    static readonly TimeSpan ProcTtl = TimeSpan.FromSeconds(1.5);

    /// <summary>java 进程清单 (缓存, 默认 1.5s 内复用)。后台预热线程在跑时基本总是命中。</summary>
    static List<ProcHit> JavaProcsCached()
    {
        lock (_procLock)
            if (_procCache != null && DateTime.UtcNow - _procCacheAt < ProcTtl) return _procCache;
        var fresh = ListJavaProcs();
        lock (_procLock) { _procCache = fresh; _procCacheAt = DateTime.UtcNow; }
        return fresh;
    }

    /// <summary>丢掉缓存 (刚启/停完服务端时用, 让下一次查询立刻重新扫)</summary>
    public static void InvalidateProcCache()
    {
        lock (_procLock) { _procCache = null; _procCacheAt = DateTime.MinValue; }
    }

    /// <summary>后台预热: 每 1.2 秒在线程池上扫一次, 前台查询就永远是缓存命中</summary>
    public static void StartProcWatcher()
    {
        if (_procWatch != null) return;
        _procWatch = new System.Threading.Timer(_ =>
        {
            try { JavaProcsCached(); } catch { }
        }, null, 300, 1200);
    }

    static string JarFileName(string root)
    {
        var jar = JarPath(root);
        return string.IsNullOrEmpty(jar) ? "s1a5-" : Path.GetFileName(jar);
    }

    /// <summary>
    /// 这条 java 命令行是不是**本包**的服务端? (2026-10-09 修)
    ///
    /// 【为什么必须比对完整路径】
    ///   以前只比 jar **文件名** —— 于是机器上任何别处跑着同名 jar 的 java
    ///   (第二个副本、另一个测试沙箱、上次没关干净的残留) 都会被认成"本包服务端在跑":
    ///   点【开始游戏】会跳过启动服务端、直接把游戏拉起来, 客户端连不上服务端,
    ///   表现就是"卡住 / 进不去游戏"。踩过一次, 别再退回文件名比对。
    ///
    /// 判据 (任一成立即算本包): 命令行里有本包 jar 的完整路径 / 本包 db 的完整路径。
    /// 两条都不成立 → false (宁可当成"没在跑"去起一个, 也不要误判成"在跑"而跳过)。
    /// </summary>
    static bool IsThisPackServerCmd(string cmdLine, string root)
    {
        if (string.IsNullOrEmpty(cmdLine)) return false;
        if (cmdLine.IndexOf("webgm.WebGmMain", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        var jar = JarPath(root);
        if (!string.IsNullOrEmpty(jar)
            && cmdLine.IndexOf(jar, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        var db = DbPath(root);
        if (!string.IsNullOrEmpty(db)
            && cmdLine.IndexOf(db, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        return false;
    }

    /// <summary>服务端 java 进程：命令行里带着**本包** jar 或 db 的完整路径、不含 WebGmMain</summary>
    static List<ProcHit> FindServerProcs(string root)
    {
        var res = new List<ProcHit>();
        foreach (var p in JavaProcsCached())
            if (IsThisPackServerCmd(p.CmdLine ?? "", root)) res.Add(p);
        return res;
    }

    /// <summary>网页 GM java 进程：命令行含 webgm.WebGmMain</summary>
    static List<ProcHit> FindWebGmProcs()
    {
        var res = new List<ProcHit>();
        foreach (var p in JavaProcsCached())
            if ((p.CmdLine ?? "").IndexOf("webgm.WebGmMain", StringComparison.OrdinalIgnoreCase) >= 0)
                res.Add(p);
        return res;
    }

    // =================================================================
    // 端口
    // =================================================================
    /// <summary>某 TCP 端口是否正在被监听</summary>
    public static bool PortIsListening(int port)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in props.GetActiveTcpListeners())
                if (ep.Port == port) return true;
        }
        catch { }
        return false;
    }

    // =================================================================
    // 运行状态
    // =================================================================
    /// <summary>
    /// 服务端在跑 = **本包**的 java 进程在。
    ///
    /// 【2026-10-09 修: 不再把"端口有人在听"当成服务端在跑】
    ///   以前这里还有一条 `|| PortIsListening(端口)` —— 于是端口被**别的程序**占着,
    ///   或者别的副本的服务端占着同一个端口时, 会被认成"本包服务端在跑", 点【开始游戏】
    ///   就跳过启动、直接拉客户端, 客户端连不上 → 卡在加载界面。
    ///   端口监听现在只用来判断"能不能起"(见 StartServerAsync 的端口冲突预检),
    ///   不再用来判断"是不是我们的服务端"。
    /// </summary>
    public static bool IsServerRunning(string root)
        => FindServerProcs(root).Count > 0;

    /// <summary>
    /// 游戏端口上**现在有没有人监听**（不管是谁）。StartServerAsync 用它做冲突预检:
    /// 端口被占时直接报"端口被占用", 而不是傻等 60 秒。
    /// </summary>
    public static bool GamePortBusyByOthers(string root)
        => FindServerProcs(root).Count == 0
        && PortIsListening(ServerUI.Core.AppSys.Settings?.GamePort ?? DefaultGamePort);

    /// <summary>网页 GM 在跑 = webgm 进程在, 或网页端口正在监听</summary>
    public static bool IsWebGmRunning(string root)
    {
        if (FindWebGmProcs().Count > 0) return true;
        int port = ServerUI.Core.AppSys.Settings?.WebGmPort ?? DefaultGmPort;
        return PortIsListening(port);
    }

    /// <summary>服务端进程 PID (按命令行匹配; 找不到再看端口归属)</summary>
    public static int ServerPid(string root)
    {
        var hits = FindServerProcs(root);
        if (hits.Count > 0) return hits[0].Pid;
        int port = ServerUI.Core.AppSys.Settings?.GamePort ?? DefaultGamePort;
        if (PortIsListening(port))
            foreach (var p in ListJavaProcs())
                if (PortInfo.ListeningPorts(p.Pid).Contains(port)) return p.Pid;
        return 0;
    }

    public static int WebGmPid()
    {
        var hits = FindWebGmProcs();
        if (hits.Count > 0) return hits[0].Pid;
        int port = ServerUI.Core.AppSys.Settings?.WebGmPort ?? DefaultGmPort;
        if (PortIsListening(port))
            foreach (var p in ListJavaProcs())
                if (PortInfo.ListeningPorts(p.Pid).Contains(port)) return p.Pid;
        return 0;
    }

    // =================================================================
    // 存档读数
    // =================================================================
    public class DbInfo
    {
        public bool Exists;
        public long Size;
        public bool HasWal;
        public string Text = "未定位";
    }

    public static DbInfo ReadDbInfo(string root)
    {
        var res = new DbInfo();
        if (!IsValidRoot(root)) return res;
        var main = DbPath(root);
        if (!File.Exists(main))
        {
            res.Text = "未创建（首次启动服务端时自动建库）";
            return res;
        }
        var fi = new FileInfo(main);
        res.Exists = true;
        res.Size = fi.Length;
        res.HasWal = File.Exists(DbWal(root));
        var mb = fi.Length / 1048576.0;
        res.Text = "s1a5.db · " + (mb >= 1 ? mb.ToString("0.0") + " MB" : (fi.Length / 1024.0).ToString("0") + " KB")
                 + (res.HasWal ? "（预写日志在, 停止服务端后合并）" : "");
        return res;
    }

    // =================================================================
    // 启动 / 停止
    // =================================================================
    public class StartResult
    {
        public bool Ok;
        public int Pid;
        public int Attempts = 1;
        public bool AlreadyRunning;
        public string Error = "";
    }

    public class StopResult
    {
        public bool Ok;
        public bool WasRunning;
        public bool Graceful;
        public bool CtrlCSent;
        public bool WalCleaned;
        public string Error = "";
    }

    /// <summary>
    /// 启动服务端 (java -jar, 自己的控制台窗口), 然后等游戏端口就绪 ——
    /// 首次要解析 86MB 的 Script.pvf, 最多等 60 秒。
    /// </summary>
    public static Task<StartResult> StartServerAsync(string root, Action<string> log)
    {
        return Task.Run(() =>
        {
            var res = new StartResult();
            try
            {
                if (!IsValidRoot(root)) { res.Error = "服务端目录无效（找不到 server\\s1a5-*-all.jar）。"; return res; }
                if (IsServerRunning(root)) { res.AlreadyRunning = true; res.Ok = true; return res; }

                /*
                 * 端口冲突预检 (2026-10-09 加):
                 * 端口上已经有人听着、但按"本包进程"又找不到 —— 那不是我们的服务端
                 * (别的程序 / 另一个副本占着)。这时**直接报错退出**, 不要傻等 60 秒,
                 * 更不要放行 —— 放行的话客户端会连到陌生进程上, 表现为卡在加载界面。
                 */
                int portPre = ServerUI.Core.AppSys.Settings.GamePort;
                if (PortIsListening(portPre))
                {
                    res.Error = "游戏端口 " + portPre + " 已被别的程序占用（不是本包的服务端）。\n\n"
                              + "先关掉占用它的程序，或在【设置 → 目标与环境】把游戏端口改成别的。";
                    return res;
                }

                var jar = JarPath(root);
                var clientDir = ServerUI.Core.AppSys.ClientDir;
                if (!IsValidClient(clientDir))
                    clientDir = ResolveClientDir(root);
                if (!IsValidClient(clientDir))
                {
                    res.Error = "还没定位到客户端目录 —— 服务端启动时要读客户端的 Script.pvf。\n请先到【设置 → 目录定位】指定。";
                    return res;
                }
                ServerUI.Core.AppSys.ClientDir = clientDir;
                ServerUI.Core.AppSys.Settings.ClientDir = clientDir;
                ServerUI.Core.AppSys.SaveSettings();

                int port = ServerUI.Core.AppSys.Settings.GamePort;
                var dbPath = DbPath(root);
                var logDir = LogDir(root);
                try { Directory.CreateDirectory(Path.GetDirectoryName(dbPath)); Directory.CreateDirectory(logDir); } catch { }

                string jerr;
                var java = FindJava(out jerr);
                if (string.IsNullOrEmpty(java))
                {
                    res.Error = "没找到可用的 Java 25 运行时。\n\n" + jerr
                              + "\n\n把 JDK 25 放到包根的 jdk25\\ 或 tools\\jdk25\\ 里即可。";
                    return res;
                }
                log?.Invoke("Java: " + java + " (" + _javaVersion + ")");

                /*
                 * 与 run.cmd 逐字一致的启动命令行 (每一段都要):
                 *   -Ds1a5.port 必须给 —— GameServer.main 读 args[0] 只进局部变量,
                 *   ServerConfig.PORT 认的是这个系统属性。
                 */
                var args =
                    "-Xms256m -Xmx1g -Duser.timezone=Asia/Shanghai -Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 "
                  + "-Ds1a5.port=" + port + " "
                  + "-Ds1a5.clientDir=\"" + clientDir + "\" "
                  + "-Ds1a5.dbUrl=\"jdbc:sqlite:" + dbPath + "\" "
                  + "-Ds1a5.logDir=\"" + logDir + "\" "
                  + "-Ds1a5.dbAutoCreateAccount=1 "
                  + "-jar \"" + jar + "\" " + port;

                var psi = new ProcessStartInfo
                {
                    FileName = java,
                    Arguments = args,
                    WorkingDirectory = root,
                    UseShellExecute = true,          // 自己的控制台窗口
                    CreateNoWindow = false
                };

                log?.Invoke("正在启动服务端 (端口 " + port + ")...");
                InvalidateProcCache();
                var p = Process.Start(psi);
                res.Pid = p?.Id ?? 0;
                log?.Invoke("服务端进程 PID " + res.Pid + "，等待端口就绪（首次启动要解析 Script.pvf，最多 60 秒）...");

                // 等端口就绪, 最多 60 秒
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 60000)
                {
                    if (p != null && p.HasExited)
                    {
                        res.Error = "服务端进程退出了（退出码 " + p.ExitCode + "）。"
                                  + "\n请看服务端窗口或 logs\\s1a5.log 里的报错"
                                  + "（最常见：Script.pvf 缺失、端口被占）。";
                        return res;
                    }
                    if (PortIsListening(port))
                    {
                        res.Ok = true;
                        log?.Invoke("游戏端口 " + port + " 已就绪。");
                        return res;
                    }
                    System.Threading.Thread.Sleep(500);
                }
                res.Error = "60 秒内游戏端口 " + port + " 没就绪。\n"
                          + "服务端可能还在解析 Script.pvf（首次启动较慢），也可能起不来 —— 请看服务端窗口的输出。";
                return res;
            }
            catch (Exception ex) { res.Error = ex.Message; return res; }
        });
    }

    /// <summary>停止服务端：先 AttachConsole 发 CTRL_C 优雅退出（等 20 秒），超时按 PID 结束进程树。</summary>
    public static Task<StopResult> StopServerAsync(string root, Action<string> log)
    {
        return Task.Run(() =>
        {
            var res = new StopResult();
            try
            {
                var hits = FindServerProcs(root);
                int port = ServerUI.Core.AppSys.Settings?.GamePort ?? DefaultGamePort;
                if (hits.Count == 0 && !PortIsListening(port))
                {
                    res.Ok = true;   // 本来就没在跑
                    return res;
                }
                res.WasRunning = true;
                var pid = hits.Count > 0 ? hits[0].Pid : ServerPid(root);
                if (pid <= 0)
                {
                    // 端口在听却找不到进程（别的程序占了端口）—— 不乱杀
                    res.Ok = true;
                    log?.Invoke("游戏端口 " + port + " 被别的进程占用，没有可停止的服务端进程。");
                    return res;
                }

                log?.Invoke("正在请求服务端正常退出 (PID " + pid + ")...");
                try
                {
                    SendCtrlC(pid);
                    res.CtrlCSent = true;
                }
                catch (Exception ex) { log?.Invoke("发送 Ctrl+C 失败: " + ex.Message); }

                // 等 20 秒
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20000)
                {
                    if (FindServerProcs(root).Count == 0 && !PortIsListening(port)) break;
                    System.Threading.Thread.Sleep(300);
                }

                if (FindServerProcs(root).Count == 0 && !PortIsListening(port))
                {
                    res.Ok = true;
                    res.Graceful = true;
                    res.WalCleaned = !File.Exists(DbWal(root));
                    log?.Invoke("服务端已退出。");
                    return res;
                }

                log?.Invoke("20 秒内没有退出，按 PID 结束进程树...");
                var proc = SafeGetProcess(pid);
                if (proc != null) ServerUI.Compat.KillTree(proc);
                InvalidateProcCache();
                System.Threading.Thread.Sleep(800);
                res.Ok = FindServerProcs(root).Count == 0 && !PortIsListening(port);
                if (!res.Ok) res.Error = "服务端进程没能结束，请手动关闭它的控制台窗口。";
                res.WalCleaned = !File.Exists(DbWal(root));
                return res;
            }
            catch (Exception ex) { res.Error = ex.Message; return res; }
        });
    }

    /* 对另一个进程的控制台发 CTRL_C (GUI 程序没有自己的控制台, Attach 上去再发) */
    static void SendCtrlC(int pid)
    {
        FreeConsole();
        if (!AttachConsole(pid)) throw new System.ComponentModel.Win32Exception();
        SetConsoleCtrlHandler(IntPtr.Zero, true);       // 自己别被 CTRL_C 带走
        GenerateConsoleCtrlEvent(0 /*CTRL_C_EVENT*/, 0);
        System.Threading.Thread.Sleep(120);
        FreeConsole();
        SetConsoleCtrlHandler(IntPtr.Zero, false);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    static Process SafeGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); } catch { return null; }
    }

    // =================================================================
    // 网页 GM
    // =================================================================
    /// <summary>
    /// 打开 GM 工具 = 启动网页 GM (S1A5 没有桌面版 GM 工具)。
    /// 等网页端口就绪最多 30 秒; 浏览器由调用方 (Flows.OpenGmTool) 负责。
    /// 【只有它一条路】—— 曾经还有一个 OpenWebGmAsync 别名("一键启动时后台拉起 GM"
    /// 用过), 那个入口已按用户要求去掉: 点【开始游戏】不再自动开 GM, 想用点【GM 工具】。
    /// </summary>
    public static Task<StartResult> OpenGmToolAsync(string root, Action<string> log)
        => StartWebGmAsync(root, log);

    static Task<StartResult> StartWebGmAsync(string root, Action<string> log)
    {
        return Task.Run(() =>
        {
            var res = new StartResult();
            try
            {
                if (!IsValidRoot(root)) { res.Error = "服务端目录无效（找不到 server\\s1a5-*-all.jar）。"; return res; }
                if (IsWebGmRunning(root)) { res.AlreadyRunning = true; res.Ok = true; return res; }

                var jar = JarPath(root);
                var clientDir = ServerUI.Core.AppSys.ClientDir;
                if (!IsValidClient(clientDir)) clientDir = ResolveClientDir(root);
                var pvf = string.IsNullOrEmpty(clientDir) ? "" : ScriptPath(clientDir);
                var imagepacks = string.IsNullOrEmpty(clientDir) ? "" : Path.Combine(clientDir, "ImagePacks2");

                int port = ServerUI.Core.AppSys.Settings.WebGmPort;
                int gamePort = ServerUI.Core.AppSys.Settings.GamePort;

                string jerr;
                var java = FindJava(out jerr);
                if (string.IsNullOrEmpty(java))
                {
                    res.Error = "没找到可用的 Java 25 运行时。\n\n" + jerr;
                    return res;
                }

                var args =
                    "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 "
                  + "-cp \"" + jar + "\" com.dylan.tools.webgm.WebGmMain "
                  + "--port " + port
                  + " --game-port " + gamePort
                  + (string.IsNullOrEmpty(pvf) ? "" : " --pvf \"" + pvf + "\"")
                  + (string.IsNullOrEmpty(imagepacks) ? "" : " --imagepacks \"" + imagepacks + "\"")
                  + " --db \"" + DbPath(root) + "\"";

                var psi = new ProcessStartInfo
                {
                    FileName = java,
                    Arguments = args,
                    WorkingDirectory = root,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                log?.Invoke("正在启动网页 GM (端口 " + port + ")...");
                InvalidateProcCache();
                var p = Process.Start(psi);
                res.Pid = p?.Id ?? 0;

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 30000)
                {
                    if (p != null && p.HasExited)
                    {
                        res.Error = "网页 GM 进程退出了（退出码 " + p.ExitCode + "）。请看它的控制台窗口报错。";
                        return res;
                    }
                    if (PortIsListening(port))
                    {
                        res.Ok = true;
                        log?.Invoke("网页端口 " + port + " 已就绪。");
                        return res;
                    }
                    System.Threading.Thread.Sleep(400);
                }
                res.Error = "30 秒内网页端口 " + port + " 没就绪 —— 请看网页 GM 的控制台窗口。";
                return res;
            }
            catch (Exception ex) { res.Error = ex.Message; return res; }
        });
    }

    /// <summary>结束网页 GM 进程（按命令行匹配、按 PID），返回结束的个数</summary>
    public static int KillWebGm(string root)
    {
        int n = 0;
        foreach (var hit in FindWebGmProcs())
        {
            var p = SafeGetProcess(hit.Pid);
            if (p == null) continue;
            try { if (ServerUI.Compat.KillTree(p)) n++; } catch { }
        }
        return n;
    }

    /// <summary>S1A5 没有桌面版 GM 工具 —— 等价于结束网页 GM</summary>
    public static int KillGmTool(string root) => KillWebGm(root);
}
