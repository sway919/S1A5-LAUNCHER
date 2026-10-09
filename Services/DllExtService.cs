/*
 * ==================================================================
 * DLL 扩展 (Services/DllExtService.cs) — S1A5 / ACT5 版
 * ==================================================================
 * 【它是干什么的】
 *   把玩家自备的 .dll 挂进游戏客户端 (汉化 / 字体 / 功能插件类补丁),
 *   并能一键全部撤下、恢复原状。本页就是客户端那份 dll_ext.ini 的图形管理。
 *
 * 【挂载机制 —— DumpReport.dll 扩展宿主】
 *   ACT5 客户端目录里的 DumpReport.dll 是扩展宿主 (源码在仓库
 *   服务端源码\tools\A5\ext_host.cpp): 客户端启动时它先把 A5.dll 拉进进程
 *   (A5.dll 照旧是"必须的客户端补丁"), 再由延迟线程读同目录 dll_ext.ini,
 *   按 [plugins] 段的行序逐个 LoadLibrary, 结果写同目录 dll_ext.log。
 *   所以"挂载" = 把扩展件复制进客户端目录 + 在 ini 里注册一行;
 *   "撤下" = 删掉复制进去的文件 + 从 ini 移除对应行。
 *   客户端目录里【同名旧文件】先备份到 扩展目录\备份\<名字>, 撤下时恢复 ——
 *   覆盖别人的 dll 必须留退路。
 *
 * 【ini 口径 (与宿主 ext_host.cpp 的解析对齐, 改这里要对照它)】
 *   · 只认 [plugins] 段, 段外忽略; 一行一条 "N=文件名.dll" (= 左边键名忽略);
 *   · 行首 ; 或 # = 注释, 空行忽略;
 *   · 编码 UTF-8 (带不带 BOM 都行) 或 ANSI(GBK); UTF-16 会被整份拒绝 = 零挂载;
 *   · 最多 64 条 (超出的部分宿主不再尝试加载);
 *   · 行序 = 加载顺序; 文件名不带路径分隔符时按客户端目录解析,
 *     带 \ 或 / 时照原样使用 (可以写绝对路径)。
 *   本工具只增删【本工具登记的条目行】: 玩家手写/其它工具写的行永远不动
 *   (判定口径 = 指向扩展目录之外的 dll)。
 *
 * 【扩展件放哪】
 *   扩展件放在   <启动器目录>\runtime\DLL扩展\*.dll
 *   随件说明     <同名>.plugin.json (可选, 作者提供)
 *   登记清单     <启动器目录>\runtime\DLL扩展\extensions.json
 *                (启用项 Enabled / 已挂载项 Deployed / 用户排序 Order)
 *
 * 【A5.dll —— 不是插件, 但页面上要做成"必选行"】
 *   A5.dll 是客户端补丁: 没有它击杀不上报、怪物打不死。它由 DumpReport.dll
 *   同一条加载链拉起, 不进 dll_ext.ini、不参与挂载/撤下 —— 本服务只为它
 *   提供"是否存在于客户端目录"的检测 (见 CheckA5)。
 *
 * 【为什么部署前要求游戏已退出】
 *   游戏运行时 DLL 被映射进进程, 删不掉也覆盖不了。部署/撤销一律先确认
 *   客户端没在运行 (由调用方用 Flows.IsGameClientRunning 拦)。
 *
 * 【启动器的纪律】
 *   全程只读文件 / 解析 JSON / 读 PE 版本资源 / 算 md5 —— 绝不 LoadLibrary
 *   玩家的插件 (那是客户端宿主的事)。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerUI.Services;

/// <summary>扩展登记清单 (extensions.json): 启用项 / 已挂载项 / 用户排序</summary>
public class DllExtManifest
{
    /// <summary>被勾选启用、点【应用更改】时应当部署进客户端的扩展件名</summary>
    public List<string> Enabled { get; set; } = new List<string>();
    /// <summary>当前确实已经复制进客户端的扩展件名 (撤下时按它来删)</summary>
    public List<string> Deployed { get; set; } = new List<string>();
    /// <summary>
    /// 扩展件顺序 (文件名, 序 = 列表显示顺序 = 写进 dll_ext.ini 的条目顺序)。
    /// 由 DLL 扩展页拖拽排序维护; 不在表里的文件按名称排在其后。
    /// 宿主按 ini 行序加载插件, 所以这个顺序就是加载优先级。
    /// </summary>
    public List<string> Order { get; set; } = new List<string>();

    /// <summary>
    /// 文件名(小写) → 我们上次拷进客户端那份的 md5。
    /// 用途: 判断"客户端目录里放着的这个文件是不是我们自己拷进去的"。
    /// 没有它就只能靠"备份在不在"来猜 —— 第二次点【应用更改】时目标位置放着的
    /// 正是我们上一篇的副本, 会被误当成玩家原文件备份, 撤下时又"还原"它,
    /// 结果是文件留在客户端目录里, 且玩家真文件在后续挂载中被无备份覆盖。
    /// </summary>
    public Dictionary<string, string> DeployedHash { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// 备份里存着真·玩家原文件的名字。**用完即弃**: 撤下时还原过就把备份文件
    /// 和这个登记一起清掉, 不留陈旧备份。(陈旧备份正是"撤下后文件复活"的根源:
    /// 撤下时看到一个陈年备份, 就把它当玩家原文件写了回去。)
    /// </summary>
    public List<string> BackedUp { get; set; } = new List<string>();
}

/// <summary>
/// 插件展示元数据 (纯显示用, 宿主/ini 完全不感知)。来源见 ResolveMeta 的
/// 四级回退链。
/// </summary>
public class PluginMeta
{
    public string Name { get; set; } = "";        // 展示名 (如 "DFO 手柄震动")
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Summary { get; set; } = "";     // 一句话说明 (列表页小字)
    public List<string> Notes { get; set; } = new List<string>();  // 注意事项 (详情页)
    public string Homepage { get; set; } = "";
    public string Source { get; set; } = "默认";  // 说明来源: 随件说明文件/内置目录/文件属性/默认
}

public class DllExtItem
{
    // 注意: 这些成员必须是【属性】不能是字段 —— WPF {Binding} 只解析属性,
    // 公有字段绑定会静默失败, 界面上就成了"文件名那一列永远是空的"。
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool Enabled { get; set; }
    public bool Deployed { get; set; }
    /// <summary>展示元数据 (四级回退链解析, 见 PluginMeta; 永不 null)</summary>
    public PluginMeta Meta { get; set; } = new PluginMeta();
    public string SizeText
    {
        get
        {
            var mb = Size / 1048576.0;
            return mb >= 1 ? mb.ToString("0.0") + " MB" : (Size / 1024.0).ToString("0") + " KB";
        }
    }
    public string StateText => Deployed ? "已挂载" : (Enabled ? "待挂载" : "未启用");
    /// <summary>列表页灰色小字 (一句话说明; 详情页才展示完整元数据)</summary>
    public string SummaryText => (Meta?.Summary ?? "").Trim();
}

public static class DllExtService
{
    public const string IniName = "dll_ext.ini";   // 客户端目录里的扩展清单 (宿主读它)
    public const string LogName = "dll_ext.log";   // 宿主每次启动写的加载结果
    public const string PatchDll = "A5.dll";       // 必须的客户端补丁 (不是插件)
    public const int MaxPlugins = 64;              // 宿主最多尝试加载的条数

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "runtime", "DLL扩展");
    public static string BackupDir => Path.Combine(Dir, "备份");
    static string ManifestPath => Path.Combine(Dir, "extensions.json");

    /// <summary>客户端里的扩展清单 (宿主启动时按它逐条加载)</summary>
    public static string IniPath(string clientDir) => Path.Combine(clientDir ?? "", IniName);

    /// <summary>客户端的扩展加载日志 (宿主每次启动覆盖/追加写)</summary>
    public static string LogPath(string clientDir) => Path.Combine(clientDir ?? "", LogName);

    // =================================================================
    // 清单读写
    // =================================================================
    public static DllExtManifest Load()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new DllExtManifest();
            var m = JsonSerializer.Deserialize<DllExtManifest>(File.ReadAllText(ManifestPath));
            if (m == null) return new DllExtManifest();
            m.Enabled ??= new List<string>();
            m.Deployed ??= new List<string>();
            m.Order ??= new List<string>();
            m.DeployedHash ??= new Dictionary<string, string>();
            m.BackedUp ??= new List<string>();
            m.Enabled.RemoveAll(string.IsNullOrWhiteSpace);
            m.Deployed.RemoveAll(string.IsNullOrWhiteSpace);
            m.Order.RemoveAll(string.IsNullOrWhiteSpace);
            m.BackedUp.RemoveAll(string.IsNullOrWhiteSpace);
            return m;
        }
        catch { return new DllExtManifest(); }
    }

    public static void Save(DllExtManifest m)
    {
        try
        {
            Directory.CreateDirectory(Dir);
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

    // =================================================================
    // 插件展示元数据 (四级回退链, 纯启动器显示用 —— 宿主/ini 不感知)
    //   1) 随件说明文件  <dll名>.plugin.json (作者提供, 信息最全, 优先级最高)
    //   2) 内置目录      已知插件的 md5 → 文案 (启动器维护, 开箱即得)
    //   3) 文件属性      DLL 版本资源 FileDescription 等 (作者编译时写进 PE)
    //   4) 默认文案      "未提供说明……"
    // 每一级要么整体采用要么整体跳过, 不跨级合并; 读元数据绝不 LoadLibrary 插件。
    // =================================================================
    public static PluginMeta ResolveMeta(string dllPath)
    {
        try
        {
            // 1) 随件说明文件
            var side = Path.ChangeExtension(dllPath, ".plugin.json");
            if (File.Exists(side))
            {
                var m = ParseSidecar(side);
                if (m != null) return m;
            }
            // 2) 内置目录 (md5 命中 = 与我们描述过的文件字节一致, 直接采用)
            var md5 = Md5OfFile(dllPath);
            if (md5 != null && BuiltinCatalog.TryGetValue(md5, out var bm))
                return bm;
            // 3) 文件属性 (版本资源; FileDescription = 一句话说明)
            var vi = FileVersionInfo.GetVersionInfo(dllPath);
            var desc = (vi.FileDescription ?? "").Trim();
            if (desc.Length > 0)
            {
                var m = new PluginMeta { Source = "文件属性", Summary = desc };
                m.Version = ((vi.ProductVersion ?? "").Trim().Length > 0 ? vi.ProductVersion : vi.FileVersion)?.Trim() ?? "";
                m.Author = (vi.CompanyName ?? "").Trim();
                foreach (var ln in (vi.Comments ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    var t = ln.Trim();
                    if (t.Length > 0) m.Notes.Add(t);
                }
                return m;
            }
        }
        catch { }
        // 4) 默认
        return new PluginMeta { Source = "默认", Summary = "未提供说明（第三方插件，启用前请自行确认来源）。" };
    }

    /// <summary>随件说明文件解析：宽容读、未知字段忽略；全空/坏 JSON = 回下一级</summary>
    static PluginMeta ParseSidecar(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // schema 缺省 = 1；本版只认 1, 更高版本留给未来 (向后兼容: 未知字段忽略)
            if (root.TryGetProperty("schema", out var sch) && sch.ValueKind == JsonValueKind.Number)
            {
                if (sch.TryGetInt32(out var sv) && sv != 1) return null;
            }
            var m = new PluginMeta { Source = "随件说明文件" };
            if (TryStr(root, "name", out var v)) m.Name = v;
            if (TryStr(root, "version", out v)) m.Version = v;
            if (TryStr(root, "author", out v)) m.Author = v;
            if (TryStr(root, "summary", out v)) m.Summary = v;
            if (TryStr(root, "homepage", out v)) m.Homepage = v;
            if (root.TryGetProperty("notes", out var n))
            {
                if (n.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in n.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String && e.GetString().Trim().Length > 0)
                            m.Notes.Add(e.GetString().Trim());
                }
                else if (n.ValueKind == JsonValueKind.String && n.GetString().Trim().Length > 0)
                {
                    m.Notes.Add(n.GetString().Trim());
                }
            }
            // 一点内容都没有 = 等于没说明, 让回退链继续
            if (string.IsNullOrWhiteSpace(m.Summary) && m.Notes.Count == 0 && string.IsNullOrWhiteSpace(m.Name))
                return null;
            return m;
        }
        catch { return null; }   // 坏 JSON 不拦导入, 只是回退到下一级
    }

    static bool TryStr(JsonElement o, string name, out string value)
    {
        value = null;
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var e)) return false;
        if (e.ValueKind != JsonValueKind.String) return false;
        value = e.GetString().Trim();
        return value.Length > 0;
    }

    static string Md5OfFile(string path)
    {
        try
        {
            using (var md = MD5.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(md.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// 内置目录: 已知插件的 md5 → 文案 (启动器维护)。当前留空 —— 以后有实机验证过的
    /// 插件按 md5 登记在这里, 开箱就有说明 (md5 命中 = 与描述过的文件字节一致)。
    /// </summary>
    static readonly Dictionary<string, PluginMeta> BuiltinCatalog =
        new Dictionary<string, PluginMeta>(StringComparer.OrdinalIgnoreCase)
        {
            // ["0123456789abcdef0123456789abcdef"] = new PluginMeta
            // {
            //     Source = "内置目录",
            //     Name = "示例插件",
            //     Version = "1.0",
            //     Author = "作者名",
            //     Summary = "一句话说明",
            //     Notes = new List<string> { "注意事项一", "注意事项二" },
            // },
        };

    // =================================================================
    // 列表
    // =================================================================
    public static List<DllExtItem> List()
    {
        var m = Load();
        var res = new List<DllExtItem>();
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var f in Directory.GetFiles(Dir, "*.dll"))
            {
                var name = Path.GetFileName(f);
                var fi = new FileInfo(f);
                res.Add(new DllExtItem
                {
                    Name = name,
                    Size = fi.Length,
                    Enabled = m.Enabled.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)),
                    Deployed = m.Deployed.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)),
                    Meta = ResolveMeta(f),
                });
            }
        }
        catch { }
        // 排序 = 应用更改时写 ini 的顺序: 清单 Order 表优先 (拖拽维护), 未登记的按名称排其后
        int Rank(string n)
        {
            var i = m.Order.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        return res.OrderBy(x => Rank(x.Name)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>拖拽排序落盘：清单 Order 表 = 完整文件名序列 (顺序 = ini 写入顺序)</summary>
    public static void SetOrder(IEnumerable<string> orderedNames)
    {
        try
        {
            var m = Load();
            m.Order = orderedNames.Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save(m);
        }
        catch { }
    }

    /// <summary>把一个外部 dll 收进扩展目录（同名 .plugin.json 随件说明一并收下，若有）</summary>
    public static bool Import(string srcFile, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(srcFile)) { error = "找不到文件。"; return false; }
            if (!srcFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { error = "只接受 .dll 文件。"; return false; }
            Directory.CreateDirectory(Dir);
            var dst = Path.Combine(Dir, Path.GetFileName(srcFile));
            string err;
            if (!CopyAtomic(srcFile, dst, out err)) { error = err; return false; }
            var sideSrc = Path.ChangeExtension(srcFile, ".plugin.json");
            if (File.Exists(sideSrc))
            {
                try { File.Copy(sideSrc, Path.Combine(Dir, Path.GetFileName(sideSrc)), true); }
                catch { }   // 说明文件收不进去不拦导入 —— 回退链还有后几级
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>从扩展目录删掉一个扩展件（连带 .plugin.json 与排序记录）</summary>
    public static bool Delete(string name, string clientDir, out string error)
    {
        error = null;
        try
        {
            var m = Load();
            if (m.Deployed.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
            { error = "这个扩展件还在客户端里挂着，请先点【全部撤下】。"; return false; }
            var f = Path.Combine(Dir, name);
            if (File.Exists(f)) File.Delete(f);
            var side = Path.Combine(Dir, Path.ChangeExtension(name, ".plugin.json"));
            if (File.Exists(side)) { try { File.Delete(side); } catch { } }
            m.Enabled.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            m.Order.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            SetHash(m, name, null);
            UnmarkBackedUp(m, name);
            TryDelete(Path.Combine(BackupDir, name));   // 扩展件都删了, 它的备份没有意义
            Save(m);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>扩展目录里现有的 dll 文件名（本工具"认识并管理顺序"的集合; ini 里指向目录外 dll 的条目 = 手工条目）</summary>
    static List<string> StoreNames()
    {
        var r = new List<string>();
        try { foreach (var f in Directory.GetFiles(Dir, "*.dll")) r.Add(Path.GetFileName(f)); }
        catch { }
        return r;
    }

    // =================================================================
    // 启用状态
    // =================================================================
    public static bool SetEnabled(string name, bool on, out string error)
    {
        error = null;
        try
        {
            var m = Load();
            m.Enabled.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (on) m.Enabled.Add(name);
            Save(m);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // =================================================================
    // A5.dll 客户端补丁 (页面上那行"必选, 不可关闭"用)
    // 只检测"在不在客户端目录里" —— 它不是插件, 不进 ini、不参与挂载。
    // =================================================================
    public sealed class A5State
    {
        public bool Present;      // 客户端目录里有 A5.dll
        public bool Warn;         // 需要警示显示 (缺失 / 客户端未定位)
        public string Text = "";  // 一行状态文字
    }

    public static A5State CheckA5(string clientDir)
    {
        var s = new A5State();
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            {
                s.Text = "还没定位客户端目录 —— 请到【设置 → 目录定位】指定。";
                s.Warn = true;
                return s;
            }
            if (!File.Exists(Path.Combine(clientDir, PatchDll)))
            {
                s.Text = "缺失 —— 杀怪不会上报、怪物打不死（点【开始游戏】时还会再提醒一次）";
                s.Warn = true;
                return s;
            }
            s.Present = true;
            s.Text = "已部署 —— 击杀会上报，怪物可以正常打死";
            return s;
        }
        catch
        {
            s.Text = "检测失败。";
            s.Warn = true;
            return s;
        }
    }

    // =================================================================
    // dll_ext.ini 读写 (与宿主 ext_host.cpp 的解析口径对齐)
    //
    // 宿主侧口径: 只认 [plugins] 段内的 "N=名字.dll" 行, 按文件顺序加载;
    // UTF-8 (带不带 BOM 都行) 或 ANSI(GBK), UTF-16 不支持 (= 零挂载);
    // 文件名带 \ 或 / 的照原样用, 否则按客户端目录解析。
    //
    // 本地写盘一律 UTF-8 无 BOM (GBK 旧内容会被正确转码成 UTF-8);
    // 只重写 [plugins] 段的条目行, 文件其余部分 (注释/其它节) 原样保留。
    // =================================================================

    /// <summary>读出现有 [plugins] 条目 (文件序)。文件不存在 = 空表；ok=false = ini 存在但宿主读不了 (UTF-16 等)。</summary>
    public static bool ReadIniEntries(string clientDir, out List<string> entries, out string error)
    {
        entries = new List<string>();
        error = null;
        try
        {
            var path = IniPath(clientDir);
            if (!File.Exists(path)) return true;
            var raw = File.ReadAllBytes(path);
            if (IsUtf16(raw)) { error = Utf16Msg; return false; }
            var txt = DecodeIniBytes(raw);
            bool inPlugins = false;
            foreach (var rawLine in txt.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("["))
                {
                    inPlugins = line.Equals("[plugins]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inPlugins) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var val = line.Substring(eq + 1).Trim();
                if (val.Length == 0) continue;
                if (!entries.Any(x => x.Equals(val, StringComparison.OrdinalIgnoreCase)))
                    entries.Add(val);
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>把 [plugins] 段整体写回 (只重排条目行，段内注释与文件其余部分保留)。文件不存在则按模板新建。</summary>
    public static bool WriteIniEntries(string clientDir, List<string> entries, out string error)
    {
        error = null;
        try
        {
            var path = IniPath(clientDir);
            // 去重 (Windows 文件名不区分大小写)，保持顺序
            var list = new List<string>();
            foreach (var n in entries)
                if (!string.IsNullOrWhiteSpace(n) && !list.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    list.Add(n);

            var nl = "\r\n";
            string head = null, mid = "", tail = "";
            if (File.Exists(path))
            {
                var raw = File.ReadAllBytes(path);
                if (IsUtf16(raw)) { error = Utf16Msg; return false; }
                var txt = DecodeIniBytes(raw);
                // 按行找 [plugins] 节与节尾 (下一个 [ 开头的节或文件尾)
                var lines = txt.Replace("\r\n", "\n").Split('\n');
                int sec = -1;
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Trim().Equals("[plugins]", StringComparison.OrdinalIgnoreCase)) { sec = i; break; }
                if (sec < 0)
                {
                    // 没有 [plugins] 节：原文整体当 head，节追加到末尾
                    head = txt.EndsWith("\n") || txt.Length == 0 ? txt : txt + nl;
                }
                else
                {
                    int end = lines.Length;
                    for (int i = sec + 1; i < lines.Length; i++)
                    {
                        var t = lines[i].Trim();
                        if (t.StartsWith("[") && t.EndsWith("]")) { end = i; break; }
                    }
                    // head = 节头行【之前】的内容 (节头行由下面统一补, 不能带进来, 否则写两遍)
                    var headLines = string.Join(nl, lines.Take(sec));
                    head = headLines.Length > 0 ? headLines + nl : "";
                    // 段内保留注释行 —— 条目行由下面统一重排, 不在这里重复写
                    var keep = new List<string>();
                    for (int i = sec + 1; i < end; i++)
                    {
                        var t = lines[i].Trim();
                        if (t.StartsWith(";") || t.StartsWith("#")) keep.Add(lines[i]);
                    }
                    if (keep.Count > 0) mid = string.Join(nl, keep) + nl;
                    tail = string.Join(nl, lines.Skip(end));
                    if (tail.Length > 0) tail = nl + tail;
                }
            }

            var sb = new StringBuilder();
            if (head == null)
                sb.Append("; ============================================================\r\n")
                  .Append("; ACT5 客户端 DLL 扩展清单（dll_ext.ini）\r\n")
                  .Append("; ============================================================\r\n")
                  .Append("; 客户端启动时 DumpReport.dll（扩展宿主）按 [plugins] 段逐行加载插件；\r\n")
                  .Append("; 规则: 一行一个 \"N=文件名.dll\"（= 左边只是序号，按行序加载）；\r\n")
                  .Append(";       行首 ; 或 # 是注释；编码 UTF-8 或 ANSI(GBK)，UTF-16 不认；\r\n")
                  .Append(";       加载失败只写同目录 dll_ext.log，不影响游戏；改完重启客户端生效。\r\n")
                  .Append("; 挂载/撤下只增删启动器登记的条目行，手工写的行保持不动。\r\n");
            else
                sb.Append(head);
            sb.Append("[plugins]").Append(nl);
            sb.Append(mid);
            for (int i = 0; i < list.Count; i++) sb.Append(i + 1).Append('=').Append(list[i]).Append(nl);
            sb.Append(tail);
            if (sb.Length == 0 || sb[sb.Length - 1] != '\n') sb.Append(nl);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
            }
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static bool IsUtf16(byte[] raw) =>
        raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF));

    const string Utf16Msg =
        "dll_ext.ini 是 UTF-16 编码，扩展宿主读不了它（一条插件都不会挂，等于零挂载）。\n\n"
      + "请手工把它另存为 UTF-8 或 ANSI，或删掉后点【应用更改】让它重建。";

    /// <summary>UTF-8 严格解码优先，失败回退 GBK (与宿主同口径)；再失败按 UTF-8 宽松兜底</summary>
    static string DecodeIniBytes(byte[] raw)
    {
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch
        {
            try { return Encoding.GetEncoding(936).GetString(raw); }
            catch { return new UTF8Encoding(false, false).GetString(raw); }
        }
    }

    // =================================================================
    // dll_ext.log (宿主写的加载结果) 摘要
    // =================================================================
    /// <summary>读客户端 dll_ext.log 的"上次加载结果"摘要 (成功/尝试 + 失败明细)。只读文件。</summary>
    public static string ReadLogSummary(string clientDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
                return "还没定位客户端目录。";
            var p = LogPath(clientDir);
            if (!File.Exists(p)) return "客户端目录里还没有 dll_ext.log（完整启动过一次游戏后才会生成）。";

            var lines = DecodeIniBytes(File.ReadAllBytes(p)).Replace("\r\n", "\n").Split('\n');
            int sum = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains("插件加载完毕")) { sum = i; break; }

            if (sum < 0)
            {
                // 没有"加载完毕"行: 可能宿主被 UTF-16/大小异常拦下, 或游戏还没跑完整
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var t = lines[i].Trim();
                    if (t.Length > 0 && (t.Contains("WARN") || t.Contains("INFO")))
                        return "上次加载：" + StripStamp(t);
                }
                return "dll_ext.log 里没有加载结果行（客户端可能还没完整启动过一次）。";
            }

            var res = "上次加载：" + StripStamp(lines[sum].Trim());
            var fails = new List<string>();
            for (int i = sum - 1; i >= 0 && fails.Count < 6; i--)
            {
                var t = lines[i].Trim();
                if (t.Contains("宿主启动")) break;
                if (t.Contains("加载失败")) fails.Add(StripStamp(t));
            }
            if (fails.Count > 0)
            {
                fails.Reverse();
                res += "\n" + string.Join("\n", fails.Select(x => "  · " + x));
            }
            return res;
        }
        catch (Exception ex) { return "读 dll_ext.log 失败：" + ex.Message; }
    }

    /// <summary>去掉行首的 "[HH:MM:SS] " 时间戳 (页面上只是看结果, 时间戳没意义)</summary>
    static string StripStamp(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("[") )
        {
            var e = t.IndexOf(']');
            if (e > 0 && e + 1 < t.Length) t = t.Substring(e + 1).TrimStart();
        }
        return t;
    }

    // =================================================================
    // 部署 / 撤销
    // =================================================================

    // =================================================================
    // "这份文件是不是我们自己拷进去的?" —— 备份/还原的判据
    //
    // 【为什么需要它】早先的判据是"备份在不在": 只要客户端目录里已经有同名文件
    // 且备份不存在, 就把它当玩家原文件备份下来。于是**第二次点【应用更改】**时,
    // 目标位置放着的正是我们上一篇拷进去的副本, 会被当成玩家文件：
    //   ① 撤下时"还原"它 → 插件文件留在客户端目录里（撤不干净）;
    //   ② 玩家自己那份随后被无备份覆盖 → **玩家文件丢失**。
    // 判据换成"内容是不是我们放的那份"之后, 这两个都消失。
    // =================================================================

    static string KeyOf(string name) => (name ?? "").Trim().ToLowerInvariant();

    static string HashOf(DllExtManifest m, string name)
        => m.DeployedHash != null && m.DeployedHash.TryGetValue(KeyOf(name), out var v) ? v : null;

    static void SetHash(DllExtManifest m, string name, string md5)
    {
        m.DeployedHash ??= new Dictionary<string, string>();
        if (string.IsNullOrEmpty(md5)) m.DeployedHash.Remove(KeyOf(name));
        else m.DeployedHash[KeyOf(name)] = md5.ToLowerInvariant();
    }

    static bool IsBackedUp(DllExtManifest m, string name)
        => m.BackedUp != null && m.BackedUp.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    static void MarkBackedUp(DllExtManifest m, string name)
    {
        m.BackedUp ??= new List<string>();
        if (!IsBackedUp(m, name)) m.BackedUp.Add(name);
    }

    static void UnmarkBackedUp(DllExtManifest m, string name)
        => m.BackedUp?.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 客户端目录里那个文件是不是我们上一篇拷进去的?
    ///   ① 清单里记着它上次部署后的 md5, 且当前内容一致 → 是我们的;
    ///   ② 退一步: 当前内容与扩展库里的源文件逐字节相同 → 几乎必然是我们的
    ///      (玩家不会恰好放一份一模一样的); 这条也兜住了老清单没有 md5 的情况。
    /// 判定失败(读不出来)按"不是我们的"处理 —— 宁可多备份一次, 不可少备份。
    /// </summary>
    static bool IsOurOwnCopy(DllExtManifest m, string name, string clientFilePath)
    {
        var now = Md5OfFile(clientFilePath);
        if (now == null) return false;
        var known = HashOf(m, name);
        if (!string.IsNullOrEmpty(known) && known.Equals(now, StringComparison.OrdinalIgnoreCase))
            return true;
        var src = Md5OfFile(Path.Combine(Dir, name));
        return src != null && src.Equals(now, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从客户端目录删掉一个挂载件；只有登记过的真备份才还原，用完即弃</summary>
    static bool RemoveOne(DllExtManifest m, string clientDir, string name, Action<string> log, out string error)
    {
        error = null;
        try
        {
            var dst = Path.Combine(clientDir, name);
            try { if (File.Exists(dst)) File.Delete(dst); }
            catch (Exception ex)
            { error = "删除 " + name + " 失败（游戏可能还在运行）：" + ex.Message; return false; }

            var bak = Path.Combine(BackupDir, name);
            if (File.Exists(bak))
            {
                if (IsBackedUp(m, name))
                {
                    // 登记过的才是真·玩家原文件 —— 还原它
                    try { File.Copy(bak, dst, true); log?.Invoke("[DLL扩展] 已恢复原文件：" + name); }
                    catch (Exception ex) { error = "恢复 " + name + " 失败：" + ex.Message; return false; }
                }
                else
                {
                    // 没登记 = 陈旧备份（旧版本留下的，或我们自己的副本被误备份）。
                    // **绝不能拿它还原** —— 那正是"撤下后文件还在"的根因。
                    log?.Invoke("[DLL扩展] 丢弃陈旧备份：" + name);
                }
                // 用完即弃：不留陈旧备份, 免得下次挂载再被误当成玩家文件
                TryDelete(bak);
            }
            else
            {
                log?.Invoke("[DLL扩展] 已撤下：" + name);
            }

            UnmarkBackedUp(m, name);
            SetHash(m, name, null);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// 把启用清单同步进客户端：复制扩展件 + 在 dll_ext.ini 注册条目 (写后回读校验)。
    /// 上次挂了、这次已取消勾选的件会被撤下 (ini 条目一并移除);
    /// 玩家手写的 ini 条目 (指向扩展目录之外 dll 的) 永远不动。ini 内容 = 手工条目 ∪ 本次勾选集。
    /// </summary>
    public static bool Deploy(string clientDir, Action<string> log, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            { error = "客户端目录无效。"; return false; }

            var m = Load();

            // 先确认 ini 可读可写，再动文件 —— 免得 dll 拷进去了注册却失败
            if (!ReadIniEntries(clientDir, out var iniEntries, out error)) return false;
            Directory.CreateDirectory(BackupDir);

            // 本次要挂的 = 勾选且扩展目录里确实存在的
            var desired = new List<string>();
            foreach (var name in m.Enabled)
            {
                var src = Path.Combine(Dir, name);
                if (!File.Exists(src)) { log?.Invoke("[DLL扩展] 跳过（扩展目录里没有）：" + name); continue; }
                desired.Add(name);
            }
            // 上次挂了、这次没勾 = 过期件，撤下
            var stale = m.Deployed.Where(d => !desired.Any(x => x.Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();

            foreach (var name in stale)
                if (!RemoveOne(m, clientDir, name, log, out error)) return false;

            foreach (var name in desired)
            {
                var src = Path.Combine(Dir, name);
                var dst = Path.Combine(clientDir, name);
                if (File.Exists(dst) && !IsOurOwnCopy(m, name, dst))
                {
                    // 客户端目录里那份**不是**我们放进去的 —— 这是玩家自己的文件
                    // (或别的工具放的), 覆盖前必须留退路。
                    var bak = Path.Combine(BackupDir, name);
                    // 已有登记过的备份就不重复覆盖它: 那份才是真·原文件,
                    // 再次覆盖会让我们自己的副本顶掉它。
                    if (!IsBackedUp(m, name))
                    {
                        try { File.Copy(dst, bak, true); MarkBackedUp(m, name); log?.Invoke("[DLL扩展] 已备份客户端原文件：" + name); }
                        catch (Exception ex) { error = "备份 " + name + " 失败：" + ex.Message; return false; }
                    }
                    else log?.Invoke("[DLL扩展] 已存在原文件备份，保持不动：" + name);
                }
                string err;
                if (!CopyAtomic(src, dst, out err))
                { error = "部署 " + name + " 失败：" + err; return false; }
                SetHash(m, name, Md5OfFile(dst));
                log?.Invoke("[DLL扩展] 已挂载：" + name);
            }

            // ini 顺序 = 扩展件加载顺序（宿主按条目文件顺序加载）。合并规则：
            //   · 玩家手工条目（指向扩展目录之外 dll 的）位置与内容都不动, 排在管理条目之前；
            //   · 启动器管理的条目 = 本次勾选集, 按清单 Order（DLL 扩展页拖拽顺序）排在其后；
            //   · 过期件（上次挂了这次没勾）连同条目一起移除。
            var known = StoreNames();
            var manual = iniEntries
                .Where(e => !stale.Any(x => x.Equals(e, StringComparison.OrdinalIgnoreCase)))
                .Where(e => !known.Any(k => k.Equals(e, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var ordered = desired
                .OrderBy(n => { var i = m.Order.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; })
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var merged = manual;
            foreach (var name in ordered)
                if (!merged.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    merged.Add(name);
            if (merged.Count > MaxPlugins)
                log?.Invoke("[DLL扩展] 注意：清单共有 " + merged.Count + " 条，超过扩展宿主的 "
                          + MaxPlugins + " 条上限 —— 按行序只加载前 " + MaxPlugins + " 条。");

            if (!WriteIniEntries(clientDir, merged, out error)) return false;

            // 写后回读校验 ×2 —— 一点缝隙都不留：
            // ① ini 条目：曾出现过"看着成功、实际没注册"（外部编辑器旧缓冲覆盖保存）；
            // ② 文件本体：条目写进去了但 dll 没落到客户端目录 = 启动时 gle=126。
            if (!ReadIniEntries(clientDir, out var verify, out error)) return false;
            var missing = desired.Where(d => !verify.Any(x => x.Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
            {
                error = "应用更改后校验失败：" + string.Join("、", missing)
                      + " 没能写进 dll_ext.ini（文件可能被其它程序占用或改写）。\n\n"
                      + "请关闭可能打开着这个文件的编辑器后重试；启动游戏前请确认该文件里有这些条目。";
                return false;
            }
            foreach (var name in desired)
            {
                long want = -1, got = -1;
                try { want = new FileInfo(Path.Combine(Dir, name)).Length; } catch { }
                try { var fi = new FileInfo(Path.Combine(clientDir, name)); if (fi.Exists) got = fi.Length; } catch { }
                if (want < 0 || got != want)
                {
                    error = "应用更改后校验失败：" + name + " 没有完整落到客户端目录里（应为 "
                          + want + " 字节，实际 " + got + "）。";
                    return false;
                }
            }

            m.Deployed = desired;
            Save(m);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>把本工具挂载的扩展件全部从客户端撤下 (含 dll_ext.ini 条目)，并恢复被覆盖的原文件</summary>
    public static bool Undeploy(string clientDir, Action<string> log, out string error)
    {
        error = null;
        try
        {
            var m = Load();
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            { error = "客户端目录无效。"; return false; }

            if (!ReadIniEntries(clientDir, out var iniEntries, out error)) return false;

            foreach (var name in m.Deployed.ToList())
                if (!RemoveOne(m, clientDir, name, log, out error)) return false;

            var remain = iniEntries.Where(e => !m.Deployed.Any(x => x.Equals(e, StringComparison.OrdinalIgnoreCase))).ToList();
            if (!WriteIniEntries(clientDir, remain, out error)) return false;
            // 写后回读校验 (同 Deploy): 撤下后这些条目必须真的不在了
            if (!ReadIniEntries(clientDir, out var verify, out error)) return false;
            var leftover = m.Deployed.Where(d => verify.Any(x => x.Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();
            if (leftover.Count > 0)
            {
                error = "撤下后校验失败：" + string.Join("、", leftover)
                      + " 仍留在 dll_ext.ini 里（文件可能被其它程序占用或改写）。\n\n"
                      + "请关闭可能打开着这个文件的编辑器后重试。";
                return false;
            }

            m.Deployed.Clear();
            // RemoveOne 里已经把"用完的备份"清掉了, 这里再兜一遍:
            // 备份目录里除了登记仍在用的, 其它一律清掉, 不留陈旧备份。
            SweepStaleBackups(m);
            Save(m);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>客户端里当前有哪些扩展件是"挂载中"的 (用于状态显示)</summary>
    public static int DeployedCount()
    {
        try { return Load().Deployed.Count; }
        catch { return 0; }
    }

    // =================================================================
    // 小工具
    // =================================================================

    /// <summary>复制到临时文件再改名 (写一半断电不会毁掉目标文件)；写后比长度</summary>
    static bool CopyAtomic(string src, string dst, out string error)
    {
        error = null;
        var tmp = dst + ".s1a5tmp";
        try
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            long srcLen = new FileInfo(src).Length;
            if (File.Exists(tmp)) File.Delete(tmp);
            File.Copy(src, tmp, true);
            if (new FileInfo(tmp).Length != srcLen)
            {
                error = "复制到临时文件后长度不一致（源 " + srcLen + " 字节）";
                TryDelete(tmp);
                return false;
            }
            if (File.Exists(dst))
            {
                try { File.Replace(tmp, dst, null); }
                catch
                {
                    // 网络盘 / FAT 等不支持 Replace 的卷: 退化为覆盖, 仍比直连复制安全
                    File.Copy(tmp, dst, true);
                    TryDelete(tmp);
                }
            }
            else File.Move(tmp, dst);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDelete(tmp);
            return false;
        }
    }

    static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    /// <summary>
    /// 清掉不再登记在用的备份文件。
    /// 备份的语义是"暂时替玩家保管的原文件"——玩家一旦撤下, 原文件就回位了,
    /// 备份继续留着只会变成下一次挂载的隐患（会被误认成玩家原文件）。
    /// </summary>
    static void SweepStaleBackups(DllExtManifest m)
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return;
            foreach (var f in Directory.GetFiles(BackupDir))
            {
                var n = Path.GetFileName(f);
                // 仍登记为"在用备份"的留下（同名件还挂着, 撤下时要还原它）
                if (IsBackedUp(m, n)) continue;
                if (m.Deployed.Any(d => d.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                TryDelete(f);
            }
        }
        catch { }
    }

    /// <summary>打开客户端目录 (扩展件与 dll_ext.ini 所落的地方)</summary>
    public static void OpenClientDir(string clientDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + clientDir + "\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>在资源管理器中选中 dll_ext.log —— log 没有文件关联，直接 Start 打不开它</summary>
    public static bool OpenLog(string clientDir)
    {
        try
        {
            var p = LogPath(clientDir);
            if (!File.Exists(p)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + p + "\"",
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }
}
