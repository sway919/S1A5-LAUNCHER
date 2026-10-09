/*
 * ==================================================================
 * 双框架兼容助手 (Compat.cs)
 * ==================================================================
 * 主版本 (net10.0-windows) 与 Win7 兼容版 (net48) 共享同一份源码。
 * .NET Framework 4.8 缺少的部分 API 在此提供等价实现:
 *   Compat.ExePath()            ≈ Environment.ProcessPath
 *   Compat.GetRelativePath()    ≈ Path.GetRelativePath      (net48 无)
 *   Compat.KillTree()           ≈ Process.Kill(true)         (net48 无整树重载)
 *   Compat.ApplyArgs()          ≈ ProcessStartInfo.ArgumentList (net48 无)
 *   Compat.Clamp()/Cbrt()       ≈ Math.Clamp / Math.Cbrt     (net48 无)
 *
 * 【为什么以 Win7 版为基准】
 *   net48 版本依赖系统自带的 .NET Framework 4.8 (Win10/11 自带, Win7 SP1 装一次即可),
 *   配合 Costura.Fody 把 AntdUI 等依赖全部内嵌, 成品只有一个几百 KB~几 MB 的 EXE,
 *   相比 net10 自包含单文件 (53MB) 体积小一个数量级, 对一键端分发更友好。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ServerUI
{
    static class Compat
    {
        /*
         * Math.Clamp 是 .NET Core 2.0 / .NET Standard 2.1 才有的 API,
         * net48 (Win7 兼容版 / WPF 分发版) 没有。两个框架共用同一份源码,
         * 所以统一走这里, 不用 #if 分支。
         */
        public static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);

        public static int Clamp(int v, int min, int max)
            => v < min ? min : (v > max ? max : v);

        /*
         * Math.Cbrt (立方根) 同样是 .NET Core 2.1+ 才有的 API, net48 没有。
         * 负数的立方根按符号单独处理 (Math.Pow 对负底数 + 非整数指数会返回 NaN)。
         */
        public static double Cbrt(double x)
        {
            if (x == 0) return 0;
            var r = Math.Pow(Math.Abs(x), 1.0 / 3.0);
            return x < 0 ? -r : r;
        }

        /// <summary>当前进程 EXE 完整路径</summary>
        public static string ExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return AppContext.BaseDirectory; }
        }

        /*
         * 相对路径计算 (net10 用 Path.GetRelativePath, net48 无此 API)。
         * 目标不在 from 之下时原样返回目标的完整路径。
         */
        public static string GetRelativePath(string from, string to)
        {
            var fromFull = Path.GetFullPath(from);
            // 驱动器根目录 (如 C:\) 不能去掉尾部斜杠, 否则前缀匹配失效
            var fromPrefix = fromFull.EndsWith("\\", StringComparison.Ordinal)
                ? fromFull
                : fromFull + "\\";
            var toFull = Path.GetFullPath(to);
            if (toFull.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase))
                return toFull.Substring(fromPrefix.Length);
            return toFull;
        }

        /*
         * 结束进程及其子进程, 并【确认它真的退出了】。
         * net10: Process.Kill(entireProcessTree: true)
         * net48: 调用系统 taskkill /F /T /PID
         *
         * 返回值: true = 进程已确认退出; false = 仍存活 (调用方应提示玩家手动处理)。
         *
         * 【为什么要等】旧实现只管发出结束请求就返回。控制器 start 超时后
         * 立刻 KillTree 再返回 -2, 而此时 DNF90Server 可能还在退出中,
         * 端口还没释放 —— 紧接着的下一次 start 就会"端口被占用"而失败,
         * 这在连续启停的压测里是最主要的连锁失败来源。
         */
        public static bool KillTree(Process p)
        {
            if (p == null) return true;
            try
            {
                if (p.HasExited) return true;
            }
            catch { return true; }

#if NET48
            try
            {
                using (var k = new Process())
                {
                    k.StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /T /PID " + p.Id,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    k.Start();
                    // 必须排空管道, 否则 taskkill 输出多时同样会卡住
                    var so = k.StandardOutput.ReadToEndAsync();
                    var se = k.StandardError.ReadToEndAsync();
                    if (!k.WaitForExit(15000)) { try { k.Kill(); } catch { } }
                    try { Task.WaitAll(new[] { so, se }, 3000); } catch { }
                }
            }
            catch { }
            if (WaitExit(p, 15000)) return true;

            // 兜底: taskkill 不可用/被拦截时只杀自身 (杀不到子树, 由调用方提示)
            try { p.Kill(); } catch { }
            return WaitExit(p, 15000) || !Alive(p);
#else
            // 先整树: 控制器是服务端的父进程, 只杀父会留下孤儿 DNF90Server 继续占端口
            try { p.Kill(true); } catch { }
            if (WaitExit(p, 15000)) return true;

            // 兜底: 整树结束失败时只杀自身
            try { p.Kill(); } catch { }
            return WaitExit(p, 10000) || !Alive(p);
#endif
        }

        /// <summary>轮询等待进程退出; 返回是否已退出</summary>
        public static bool WaitExit(Process p, int timeoutMs)
        {
            if (p == null) return true;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (p.HasExited) return true;
                    System.Threading.Thread.Sleep(100);
                }
                return p.HasExited;
            }
            catch { return true; }
        }

        static bool Alive(Process p)
        {
            try { return !p.HasExited; } catch { return false; }
        }

        /*
         * 给 ProcessStartInfo 设置参数。
         * net10: ArgumentList (框架自动加引号)
         * net48: 手动拼 Arguments 字符串 (含空格/引号的参数加双引号)
         *
         * net48 下必须按 Windows 命令行规则转义: 参数末尾连续的 '\' 要成对翻倍,
         * 否则 "E:\my game\" 这种以反斜杠结尾的路径会把结尾的 \" 变成转义引号,
         * 引号配对被破坏, 后续参数会被并进同一个值。
         */
        public static void ApplyArgs(ProcessStartInfo psi, IEnumerable<string> args)        {
#if NET48
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteWin32(a));
            }
            psi.Arguments = sb.ToString();
#else
            foreach (var a in args) psi.ArgumentList.Add(a);
#endif
        }

#if NET48
        /// <summary>按 Windows CommandLineToArgvW 规则给单个参数加引号</summary>
        static string QuoteWin32(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < a.Length; i++)
            {
                int backslashes = 0;
                while (i < a.Length && a[i] == '\\') { backslashes++; i++; }

                if (i == a.Length)
                {
                    // 结尾的反斜杠要翻倍, 否则会转义我们加的收尾引号
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (a[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(a[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
#endif
    }
}
