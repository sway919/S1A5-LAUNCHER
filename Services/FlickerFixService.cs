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
}

public static class FlickerFixService
{
    public const string PatchDirName = "防闪屏补丁";

    static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "runtime", "防闪屏补丁.json");
    static string BackupDir => Path.Combine(AppContext.BaseDirectory, "runtime", "防闪屏补丁备份");

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
    /// 把补丁文件夹里的所有文件复制进客户端目录（与 DNF.exe 同层）。
    /// 幂等: 内容一致的跳过不碰（游戏在跑也不会被卡住）; 覆盖玩家自己的
    /// 文件前先备份。每个文件成功后就落一次清单 —— 中途失败重试能续上。
    /// </summary>
    public static bool Deploy(string clientDir, Action<string> log, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            { error = "客户端目录无效。"; return false; }

            var patchDir = FindPatchDir(out var searched);
            if (patchDir == null)
            {
                error = "没找到【" + PatchDirName + "】文件夹。找过:\n" + searched.TrimEnd()
                      + "\n\n请把它放回启动器旁边（与 S1a5-Launcher.exe 同一层），再重新勾选。";
                return false;
            }

            var files = Directory.GetFiles(patchDir);
            if (files.Length == 0)
            { error = "【" + PatchDirName + "】文件夹是空的（应有 D3D8.dll、dgVoodoo.conf 等文件）。"; return false; }

            var m = Load();
            foreach (var src in files)
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
                Save(m);
                log?.Invoke("[闪屏修复] 已复制: " + name);
            }
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
            Save(m);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // =================================================================
    // 状态查询 (首页开关的状态点用, 每 2 秒会被问一次 —— 只做 File.Exists)
    // =================================================================
    /// <summary>补丁当前是否在客户端目录里（清单有部署记录且文件都在）。</summary>
    public static bool IsDeployedAt(string clientDir)
    {
        try
        {
            var m = Load();
            if (m.Deployed.Count == 0) return false;
            foreach (var name in m.Deployed)
                if (!File.Exists(Path.Combine(clientDir ?? "", name))) return false;
            return true;
        }
        catch { return false; }
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
