/*
 * ==================================================================
 * 闪屏修复 (Services/FlickerFixService.cs) — S1A5 / ACT5 版
 * ==================================================================
 * 【它是干什么的】
 *   ACT5 是 2008 年的 DX8 游戏, 在现代 Windows 上（全屏独占 / 垂直同步的
 *   组合不当）画面会闪屏。随包发布的【防闪屏补丁】压住这个问题 —— 它是
 *   dgVoodoo2 的 D3D8 转换层:
 *     D3D8.dll        游戏启动时优先加载的本地 DX8 包装层（关键件）
 *     dgVoodoo.conf   dgVoodoo 配置（输出 API / 缩放 / 垂直同步等）
 *     dgVoodooCpl.exe 配置器（想调参数双击它, 改完重启游戏生效）
 *   把它们复制进游戏客户端根目录（与 DNF.exe 同层）即生效; 撤下即恢复原状。
 *
 *   首页的【闪屏修复】开关（HomeView）勾选 = 部署, 取消 = 撤下。
 *   三个入口共用本服务: 开关本身（Flows.ApplyFlickerFixAsync）、
 *   启动自检（Flows.AutoSetup, 勾着就自动补齐）、开始游戏（Flows.Play,
 *   进游戏前再保险一次）。
 *
 * 【两套补丁 / 模式】（2026-10-13 加）
 *   开关旁边有一个【电脑/Wine】切换:
 *     · 电脑  = 老的 dgVoodoo2 方案, 部署补丁文件夹根下的全部文件
 *       （D3D8.dll + dgVoodoo.conf + dgVoodooCpl.exe）; Windows 本机用。
 *     · Wine  = 闪烁原生修复（Linux/Wine 适配, 见补丁夹里的 DNF60A5_Linux适配总结.md）,
 *       部署子文件夹【闪烁原生修复-wine修复\】里的两个 dll:
 *         dinput8.dll            Wine 适配代理（字体间距 + 假任务栏; Windows 下纯透传）
 *         d3d8_viewport_fix.dll  视口深度修复（SetViewport MinZ/MaxZ 未初始化的共通 bug;
 *                                经 dll_ext 宿主挂载, 部署时会登记进 dll_ext.ini 的 [plugins]）
 *       补丁夹里的 .c 源码与 .md 文档不部署。dinput8.dll 要真正被 Wine 加载还需要玩家
 *       侧 export WINEDLLOVERRIDES="dinput8=n,b"（启动器管不到 Wine 环境变量, 日志里会提示）。
 *   两套部署同一批客户端文件时互斥: 切模式 / 换模式部署会先把另一套的部署撤干净再装新的。
 *   清单里记着"这批文件是哪个模式部署的", 撤下按记录走。
 *
 * 【补丁文件夹在哪】
 *   约定与启动器放在一起: <启动器目录>\防闪屏补丁\。找不到时自动向上找
 *   两级 —— 两种包布局的包根都在这两级上（game\ 一键端: 启动器在 game\,
 *   补丁在包根; 游玩\ACT5 整合包: 启动器在 游玩\ACT5\, 补丁随包根放）。
 *   三处都没有 → 部署失败并报出找过的位置, 提示把文件夹放回启动器旁边。
 *
 * 【撤下怎么保证不误删玩家的文件】（纪律与 DllExtService 一致）
 *   · 部署时目标位置已有【内容不同】的文件 → 先备份到
 *     <启动器目录>\runtime\防闪屏补丁备份\ 再覆盖, 备份登记进清单;
 *   · 撤下时先比对 md5: 与我们部署时记录一致 = 我们的副本 → 删/还原;
 *     不一致 = 玩家后来换过 → 不动它, 只提示;
 *   · 备份用完即弃, 清单里没登记的陈旧备份绝不拿来回填（"撤下后文件复活"
 *     的根源就是拿陈旧备份回填, 见 DllExtService 的教训）。
 *   清单: <启动器目录>\runtime\防闪屏补丁.json（已部署项 / 部署后的 md5 / 备份登记）。
 *
 * 【启动器的纪律】
 *   全程只复制 / 删除 / 算 md5 —— 绝不 LoadLibrary 补丁里的任何东西。
 *   回归测试台: tests\flickerfix_probe\run.cmd（不启动界面, 秒级跑完）。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerUI.Services;

/// <summary>部署清单 (runtime\防闪屏补丁.json): 已部署项 / 部署后的 md5 / 玩家原文件备份登记</summary>
public class FlickerFixManifest
{
    /// <summary>当前已经复制进客户端目录的文件名（撤下时按它来删）</summary>
    public List<string> Deployed { get; set; } = new List<string>();

    /// <summary>
    /// 文件名(小写) → 我们部署那份的 md5。
    /// 用途: 撤下时判定"客户端目录里放着的这个文件是不是我们自己拷进去的" ——
    /// 不一致说明玩家后来换过, 绝不能删（同 DllExtService.DeployedHash 的教训）。
    /// </summary>
    public Dictionary<string, string> DeployedHash { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// 备份里存着真·玩家原文件的名字。**用完即弃**: 撤下时还原过就把备份文件
    /// 和这个登记一起清掉, 不留陈旧备份。
    /// </summary>
    public List<string> BackedUp { get; set; } = new List<string>();

    /// <summary>
    /// 这批已部署文件是哪个模式装进去的: "pc" / "wine" / 空（旧版清单 = pc）。
    /// 切模式时按它知道要撤哪套; 撤下与状态点也按它对账。
    /// </summary>
    public string Mode { get; set; } = "";
}

public static class FlickerFixService
{
    public const string PatchDirName = "防闪屏补丁";

    // ===== 两种模式 =====
    /// <summary>电脑（Windows 本机）: dgVoodoo2, 部署补丁文件夹根下的全部文件</summary>
    public const string ModePc = "pc";
    /// <summary>Wine/Linux: 闪烁原生修复, 部署子文件夹里的两个 dll</summary>
    public const string ModeWine = "wine";

    /// <summary>Wine 补丁子文件夹（在 防闪屏补丁\ 里面）</summary>
    public const string WineSubDirName = "闪烁原生修复-wine修复";

    /// <summary>Wine 模式部署的文件（显式清单 —— 补丁夹里的 .c 源码与 .md 文档不部署）</summary>
    static readonly string[] WineFiles = { "dinput8.dll", "d3d8_viewport_fix.dll" };

    /// <summary>Wine 模式要登记进 dll_ext.ini [plugins] 的插件（视口修复走 dll_ext 宿主挂载;
    /// dinput8 是代理 dll, 由 Wine 的 WINEDLLOVERRIDES 拉, 不进 ini）</summary>
    public const string WineIniPlugin = "d3d8_viewport_fix.dll";

    static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "runtime", "防闪屏补丁.json");
    static string BackupDir => Path.Combine(AppContext.BaseDirectory, "runtime", "防闪屏补丁备份");

    /// <summary>规范化模式: null/空/未知 → 电脑（老行为）。清单里旧记录的空 Mode 也按它读。</summary>
    public static string NormalizeMode(string mode)
        => (mode ?? "").Trim().Equals(ModeWine, StringComparison.OrdinalIgnoreCase) ? ModeWine : ModePc;

    // =================================================================
    // 补丁文件夹定位
    // =================================================================
    /// <summary>
    /// 找随包的防闪屏补丁文件夹: 先找启动器同目录, 再向上两级
    /// （一键端 / 整合包两种布局的包根都覆盖）。找不到返回 null,
    /// searched 带回找过的位置（给报错文案用）。
    /// </summary>
    public static string FindPatchDir(out string searched)
    {
        searched = "";
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 3; i++)
        {
            var cand = Path.Combine(dir, PatchDirName);
            searched += (searched.Length == 0 ? "" : "\n") + "  " + cand;
            if (Directory.Exists(cand)) return cand;
            var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var up = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(up) || string.Equals(up, dir, StringComparison.OrdinalIgnoreCase)) break;
            dir = up;
        }
        return null;
    }

    // =================================================================
    // 部署
    // =================================================================
    /// <summary>
    /// 把补丁文件复制进客户端目录（与 DNF.exe 同层）。mode = 电脑（dgVoodoo 整夹）
    /// 或 Wine（原生修复两个 dll + dll_ext.ini 登记）。
    /// 若客户端里已有【另一个模式】的部署, 先把它撤干净再装新的（两套别混着放）。
    /// 幂等: 内容一致的跳过不碰（游戏在跑也不会被卡住）; 覆盖玩家自己的
    /// 文件前先备份。每个文件成功后就落一次清单 —— 中途失败重试能续上。
    /// </summary>
    public static bool Deploy(string clientDir, string mode, Action<string> log, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            { error = "客户端目录无效。"; return false; }

            mode = NormalizeMode(mode);
            var patchDir = FindPatchDir(out var searched);
            if (patchDir == null)
            {
                error = "没找到【" + PatchDirName + "】文件夹。找过:\n" + searched.TrimEnd()
                      + "\n\n请把它放回启动器旁边（与 S1a5-Launcher.exe 同一层），再重新勾选。";
                return false;
            }

            string srcDir;
            if (mode == ModeWine)
            {
                srcDir = Path.Combine(patchDir, WineSubDirName);
                if (!Directory.Exists(srcDir))
                {
                    error = "没找到【" + PatchDirName + "\\" + WineSubDirName + "】子文件夹（Wine 补丁不齐）。"
                          + "\n请把【闪烁原生修复-wine修复】文件夹放回防闪屏补丁文件夹里再试。";
                    return false;
                }
            }
            else srcDir = patchDir;

            string[] srcFiles;
            if (mode == ModeWine)
            {
                srcFiles = WineFiles
                    .Select(f => Path.Combine(srcDir, f))
                    .Where(p => File.Exists(p))
                    .ToArray();
                var missing = WineFiles.FirstOrDefault(f => !File.Exists(Path.Combine(srcDir, f)));
                if (missing != null)
                { error = "Wine 补丁缺文件: " + missing + "（请确认【" + WineSubDirName + "】文件夹完整）。"; return false; }
            }
            else
            {
                srcFiles = Directory.GetFiles(srcDir);
                if (srcFiles.Length == 0)
                { error = "【" + PatchDirName + "】文件夹是空的（应有 D3D8.dll、dgVoodoo.conf 等文件）。"; return false; }
            }

            var m = Load();

            // 另一套的部署还在客户端里 → 先撤干净（清别的模式的文件, 不清刚记下的备份规则）
            var recorded = NormalizeMode(m.Mode);
            if (m.Deployed.Count > 0 && recorded != mode)
            {
                log?.Invoke("[闪屏修复] 检测到另一套补丁（" + ModeText(recorded) + "）在位 —— 先撤下它。");
                if (!RemoveDeployed(clientDir, log, out var rmErr))
                { error = rmErr; return false; }
                m = Load();
            }

            foreach (var src in srcFiles)
            {
                var name = Path.GetFileName(src);
                var dst = Path.Combine(clientDir, name);
                var hSrc = Md5(src);
                if (hSrc == null) { error = "读不了补丁文件: " + src; return false; }

                string hDst = null;
                if (File.Exists(dst))
                {
                    hDst = Md5(dst);
                    if (hDst == null)
                    {
                        // 存在但读不出来 = 多半正被游戏占用。清单里记着它且是我们部署的
                        // → 当"已在位"跳过 (AutoSetup/Play 的例行补齐不会在游戏运行时误报);
                        //   没记过 (来历不明 + 读不了) → 老实报错, 宁可不部署也不瞎覆盖。
                        if (m.DeployedHash.ContainsKey(Key(name)))
                        {
                            Mark(m, name, hSrc);
                            Save(m);
                            log?.Invoke("[闪屏修复] 已在位（文件被占用，内容未校验）: " + name);
                            continue;
                        }
                        error = "读不了客户端目录里已有的 " + name + "（游戏可能正在运行）—— 请先退出游戏再勾选。";
                        return false;
                    }
                }
                if (hDst != null && hDst.Equals(hSrc, StringComparison.OrdinalIgnoreCase))
                {
                    Mark(m, name, hSrc);
                    Save(m);
                    log?.Invoke("[闪屏修复] 已在位: " + name);
                    continue;
                }
                if (hDst != null && !IsOurOwnCopy(m, name, hDst))
                {
                    // 客户端里那份不是我们放的 —— 玩家自己的文件, 覆盖前必须留退路
                    Directory.CreateDirectory(BackupDir);
                    var bak = Path.Combine(BackupDir, name);
                    if (!m.BackedUp.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        try { File.Copy(dst, bak, true); }
                        catch (Exception ex) { error = "备份 " + name + " 失败: " + ex.Message; return false; }
                        m.BackedUp.Add(name);
                        log?.Invoke("[闪屏修复] 已备份客户端原文件: " + name);
                    }
                    else log?.Invoke("[闪屏修复] 已存在原文件备份，保持不动: " + name);
                }
                if (!CopyAtomic(src, dst, out var err))
                {
                    error = "复制 " + name + " 失败: " + err
                          + "（如果游戏正在运行，请先退出游戏再勾选闪屏修复）";
                    return false;
                }
                Mark(m, name, hSrc);
                m.Mode = mode;
                Save(m);
                log?.Invoke("[闪屏修复] 已复制: " + name);
            }

            // Wine 模式收尾: 把视口修复插件登记进 dll_ext.ini（宿主按 [plugins] 挂载）。
            // ini 不存在时由 DllExtService 建模板; 登记/撤下只增删我们这一个条目。
            if (mode == ModeWine)
            {
                if (!RegisterWinePlugin(clientDir, true, log, out var iniErr))
                { error = iniErr; return false; }
                log?.Invoke("[闪屏修复] Wine 补丁部署完成。用 Wine 启动时还需要设置"
                          + " WINEDLLOVERRIDES=\"dinput8=n,b\"（见补丁夹里的 Linux 适配总结）。");
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>模式的中文名（日志用）</summary>
    static string ModeText(string mode) => NormalizeMode(mode) == ModeWine ? "Wine" : "电脑";

    /// <summary>
    /// 把 d3d8_viewport_fix.dll 登记/摘出 dll_ext.ini 的 [plugins] 段（只动我们这一个条目,
    /// 玩家自己的插件行保持不动）。ini 不存在时按模板新建。
    /// </summary>
    static bool RegisterWinePlugin(string clientDir, bool add, Action<string> log, out string error)
    {
        error = null;
        try
        {
            var ok = DllExtService.ReadIniEntries(clientDir, out var entries, out error);
            if (!ok) return false;
            var had = entries.Any(x => x.Equals(WineIniPlugin, StringComparison.OrdinalIgnoreCase));
            if (add && !had) entries.Add(WineIniPlugin);
            if (!add) entries.RemoveAll(x => x.Equals(WineIniPlugin, StringComparison.OrdinalIgnoreCase));
            if (had == add) return true;   // 已经是目标状态, 不动文件
            if (!DllExtService.WriteIniEntries(clientDir, entries, out error)) return false;
            log?.Invoke("[闪屏修复] " + (add ? "已登记" : "已摘除") + " dll_ext.ini 扩展条目: " + WineIniPlugin);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // =================================================================
    // 撤下
    // =================================================================
    /// <summary>
    /// 撤下: 删我们的副本 / 还原备份过的玩家原文件; 玩家后来改过的不动。
    /// 备份用完即弃, 没登记的陈旧备份一律清掉（不回填）。
    /// </summary>
    public static bool RemoveDeployed(string clientDir, Action<string> log, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            { error = "客户端目录无效。"; return false; }

            var m = Load();
            foreach (var name in m.Deployed.ToList())
            {
                var dst = Path.Combine(clientDir, name);
                var hOurs = m.DeployedHash.TryGetValue(Key(name), out var h) ? h : null;

                if (!File.Exists(dst))
                {
                    log?.Invoke("[闪屏修复] 本来就不在: " + name);
                }
                else if (Md5(dst) is not string hNow)
                {
                    // 存在但读不出来 = 多半正被游戏占用 —— 记录保留, 等游戏退出后重试
                    error = "读不了 " + name + "（游戏可能正在运行）—— 请先退出游戏再关闭闪屏修复。";
                    return false;
                }
                else if (hOurs != null && hNow.Equals(hOurs, StringComparison.OrdinalIgnoreCase))
                {
                    // 是我们的副本 → 还原真备份, 否则直接删
                    var bak = Path.Combine(BackupDir, name);
                    if (m.BackedUp.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(bak))
                    {
                        try { File.Copy(bak, dst, true); }
                        catch (Exception ex) { error = "恢复 " + name + " 失败: " + ex.Message; return false; }
                        TryDelete(bak);
                        m.BackedUp.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                        log?.Invoke("[闪屏修复] 已恢复原文件: " + name);
                    }
                    else
                    {
                        try { File.Delete(dst); }
                        catch (Exception ex)
                        { error = "删除 " + name + " 失败（游戏可能正在运行）: " + ex.Message; return false; }
                        log?.Invoke("[闪屏修复] 已移除: " + name);
                    }
                }
                else
                {
                    log?.Invoke("[闪屏修复] 客户端里的 " + name + " 与部署时不一致（可能被改过）—— 没有动它。");
                }
                m.Deployed.Remove(name);
                m.DeployedHash.Remove(Key(name));
                Save(m);
            }

            // 陈旧备份清理: 登记还在用的留着, 其它一律删 —— 免得下次被误当玩家原文件回填
            if (Directory.Exists(BackupDir))
            {
                foreach (var bak in Directory.GetFiles(BackupDir))
                    if (!m.BackedUp.Contains(Path.GetFileName(bak), StringComparer.OrdinalIgnoreCase))
                        TryDelete(bak);
                try
                {
                    if (Directory.GetFiles(BackupDir).Length == 0 && Directory.GetDirectories(BackupDir).Length == 0)
                        Directory.Delete(BackupDir);
                }
                catch { }
            }

            // Wine 模式部署过的 → 摘掉 dll_ext.ini 里的登记（文件还在/被改过也摘,
            // ini 条目与"那颗 dll 是不是我们的副本"是两码事; 摘除失败不吞 —— 那会让
            // 玩家以为撤干净了而插件其实还挂着）
            if (NormalizeMode(m.Mode) == ModeWine)
            {
                if (!RegisterWinePlugin(clientDir, false, log, out var iniErr))
                { error = iniErr; return false; }
            }
            m.Mode = "";
            Save(m);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // =================================================================
    // 状态查询 (首页开关的状态点用, 每 2 秒会被问一次 —— 只做 File.Exists)
    // =================================================================
    /// <summary>
    /// 指定模式的补丁当前是否在客户端目录里（清单部署记录与模式一致且文件都在）。
    /// 清单里 Mode 为空 = 旧版部署 = 电脑模式（老清单没有模式字段, 那时只有 dgVoodoo 一套）。
    /// </summary>
    public static bool IsDeployedAt(string clientDir, string mode)
    {
        try
        {
            var m = Load();
            if (m.Deployed.Count == 0) return false;
            if (NormalizeMode(m.Mode) != NormalizeMode(mode)) return false;
            foreach (var name in m.Deployed)
                if (!File.Exists(Path.Combine(clientDir ?? "", name))) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>清单里记录的部署模式（"" = 没部署过 = 电脑口径）。状态点对账用。</summary>
    public static string DeployedMode()
    {
        try { return NormalizeMode(Load().Mode); }
        catch { return ModePc; }
    }

    // =================================================================
    // 清单读写 (原子写, 与 LauncherSettings / DllExtService 同一套)
    // =================================================================
    static FlickerFixManifest Load()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new FlickerFixManifest();
            var m = JsonSerializer.Deserialize<FlickerFixManifest>(File.ReadAllText(ManifestPath));
            if (m == null) return new FlickerFixManifest();
            m.Deployed ??= new List<string>();
            m.DeployedHash ??= new Dictionary<string, string>();
            m.BackedUp ??= new List<string>();
            m.Deployed.RemoveAll(string.IsNullOrWhiteSpace);
            m.BackedUp.RemoveAll(string.IsNullOrWhiteSpace);
            m.Mode ??= "";
            return m;
        }
        catch { return new FlickerFixManifest(); }
    }

    static void Save(FlickerFixManifest m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath));
            var json = JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true });
            var tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(ManifestPath))
            {
                try { File.Replace(tmp, ManifestPath, null); }
                catch { File.Copy(tmp, ManifestPath, true); try { File.Delete(tmp); } catch { } }
            }
            else File.Move(tmp, ManifestPath);
        }
        catch { }
    }

    static void Mark(FlickerFixManifest m, string name, string md5)
    {
        if (!m.Deployed.Contains(name, StringComparer.OrdinalIgnoreCase)) m.Deployed.Add(name);
        m.DeployedHash[Key(name)] = md5;
    }

    /// <summary>客户端里那份是不是我们部署的副本（与记录的 md5 一致）。读不出来按"不是"处理 —— 宁可多备份一次。</summary>
    static bool IsOurOwnCopy(FlickerFixManifest m, string name, string currentMd5)
        => !string.IsNullOrEmpty(currentMd5)
        && m.DeployedHash.TryGetValue(Key(name), out var h)
        && h.Equals(currentMd5, StringComparison.OrdinalIgnoreCase);

    static string Key(string name) => name.ToLowerInvariant();

    /// <summary>复制到临时文件再改名 (写一半断电不会毁掉目标文件)；写后比长度</summary>
    static bool CopyAtomic(string src, string dst, out string error)
    {
        error = null;
        var tmp = dst + ".s1a5tmp";
        try
        {
            File.Copy(src, tmp, true);
            if (new FileInfo(tmp).Length != new FileInfo(src).Length)
            { error = "复制后长度不一致"; return false; }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    static string Md5(string path)
    {
        try
        {
            using (var md = MD5.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(md.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
        catch { return null; }
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
