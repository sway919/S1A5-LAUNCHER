/*
 * ==================================================================
 * 启动器本地设置 (LauncherSettings) — S1A5 版
 * ==================================================================
 * 保存位置: 本程序 EXE 同目录 S1a5启动器.config.json
 *
 * 【为什么需要它】
 *   启动器要知道服务端包目录 (含 server\*.jar) 与游戏客户端目录
 *   (含 DNF.exe)。玩家可能把本 EXE 放在一键端目录、或两者之外的任意
 *   位置, 所以首次运行会自动定位一次, 结果缓存在这里; 之后每次启动直接
 *   复用, 不再反复扫盘。两个目录都可以在【设置 → 目录定位】里手工指定。
 *
 * 【外观设置】
 *   主题 / 背景图 / 原画融合度 / 玻璃浓度 —— 界面外观的设置位, 照搬
 *   60US starter 的那一套 (从背景原画实时取色的设计系统)。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ServerUI.Services;

public class LauncherSettings
{
    // ===== 目录 =====
    // 服务端包目录 (含 server\*.jar / data\s1a5.db)
    public string ServerDir { get; set; } = "";

    // 游戏客户端目录 (含 DNF.exe / Script.pvf)
    public string ClientDir { get; set; } = "";

    // ===== S1A5 的连接与账号 =====
    // 游戏端口（服务端 -Ds1a5.port，写闸/进游戏都用它）
    public int GamePort { get; set; } = 10011;
    // 网页 GM 端口
    public int WebGmPort { get; set; } = 5052;
    // 进游戏的账号 / 密码（账号不存在时服务端首启会自动创建）
    public const string DefaultAccount = "123456789";
    public const string DefaultPassword = "123456789";
    public string Account { get; set; } = DefaultAccount;
    public string Password { get; set; } = DefaultPassword;

    // ===== 客户端补丁 =====
    // 【闪屏修复】勾选时把随包的 防闪屏补丁\ (dgVoodoo2) 复制进客户端根目录,
    // 取消勾选时撤下并恢复原状。部署细节见 Services\FlickerFixService.cs。
    public bool FlickerFixEnabled { get; set; } = false;

    // ===== 外观 (沿用 60US starter 的令牌) =====
    public string UiTheme { get; set; } = "dark";
    public List<string> UiBackgrounds { get; set; } = new List<string>();
    public int UiBgIndex { get; set; } = 0;
    public double UiOverlay { get; set; } = 0.45;
    public double UiArtFusion { get; set; } = 0.30;
    public double UiGlassBlur { get; set; } = 16;
    public double UiScrimOpacity { get; set; } = 0.30;
    public double UiGlassOpacity { get; set; } = 0.62;

    // ===== 首次运行标记 =====
    public bool FirstRunDone { get; set; } = false;

    /*
     * 使用协议是否已确认 (首次运行必须逐字输入确认短语, 见 Views\AgreementWindow)。
     * 只认配置文件里这一位 —— 删掉配置文件就会重新要求确认。
     * 旧配置文件里没有这个字段 → 反序列化后保持初始值 false → 会弹一次, 符合预期。
     */
    public bool AgreementAccepted { get; set; } = false;

    // ===== 文件位置 =====
    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "S1a5启动器.config.json");

    // 上次 Load 是否因为文件内容损坏而回退到默认值 (供界面提示)
    public static bool LoadFailed { get; private set; }

    public static LauncherSettings Load()
    {
        LoadFailed = false;
        LauncherSettings s = null;
        bool corrupted = false;

        /*
         * 读取与"损坏"必须分开处理 (沿用 60US starter 的教训):
         *   · 文件被别的程序短暂独占 (杀软扫描 / 网盘同步 / 另一个实例正在写) →
         *     重试几次; 仍失败就回退默认值, 但【绝不能动那个文件】。
         *   · 内容真的解析不了 (手改坏了 / 断电写了一半) → 留一份 .bad 作证据再回退。
         */
        string text = null;
        for (int attempt = 0; attempt < 3 && text == null; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath)) break;
                text = File.ReadAllText(FilePath);
            }
            catch (IOException) { System.Threading.Thread.Sleep(150); }
            catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(150); }
            catch { break; }
        }

        if (text == null)
        {
            if (File.Exists(FilePath)) LoadFailed = true;
        }
        else
        {
            try
            {
                s = JsonSerializer.Deserialize<LauncherSettings>(text);
                if (s == null) { corrupted = true; LoadFailed = true; }
            }
            catch (JsonException) { corrupted = true; LoadFailed = true; }
            catch { corrupted = true; LoadFailed = true; }
        }

        if (corrupted)
        {
            try
            {
                var bad = FilePath + ".bad";
                if (File.Exists(bad)) File.Delete(bad);
                if (File.Exists(FilePath)) File.Move(FilePath, bad);
            }
            catch { }
        }

        s ??= new LauncherSettings();

        // 兜底: 旧配置文件缺字段 / 值非法时补默认值
        if (s.ServerDir == null) s.ServerDir = "";
        if (s.ClientDir == null) s.ClientDir = "";
        if (s.Account == null) s.Account = DefaultAccount;
        if (s.Password == null) s.Password = DefaultPassword;
        // 旧配置里存的还是随包那份 10038 / 123456 → 升到新的默认账号 123456789
        if (s.Account == "10038" && s.Password == "123456")
        {
            s.Account = DefaultAccount;
            s.Password = DefaultPassword;
        }
        if (s.GamePort < 1 || s.GamePort > 65535) s.GamePort = 10011;
        if (s.WebGmPort < 1 || s.WebGmPort > 65535) s.WebGmPort = 5052;
        if (s.UiBackgrounds == null) s.UiBackgrounds = new List<string>();
        s.UiBackgrounds.RemoveAll(string.IsNullOrWhiteSpace);
        if (s.UiBgIndex < 0) s.UiBgIndex = 0;
        if (double.IsNaN(s.UiOverlay) || s.UiOverlay < 0 || s.UiOverlay > 0.9) s.UiOverlay = 0.45;
        if (double.IsNaN(s.UiArtFusion) || s.UiArtFusion < 0 || s.UiArtFusion > 1) s.UiArtFusion = 0.30;
        if (double.IsNaN(s.UiGlassBlur) || s.UiGlassBlur < 0 || s.UiGlassBlur > 40) s.UiGlassBlur = 16;
        if (double.IsNaN(s.UiGlassOpacity) || s.UiGlassOpacity < 0.35 || s.UiGlassOpacity > 1) s.UiGlassOpacity = 0.62;
        if (double.IsNaN(s.UiScrimOpacity) || s.UiScrimOpacity < 0 || s.UiScrimOpacity > 1) s.UiScrimOpacity = 0.30;
        if (s.UiTheme != "light") s.UiTheme = "dark";
        return s;
    }

    /*
     * 保存设置。【必须原子写】先写同目录 .tmp 再整体替换,
     * 避免出现"写了一半"的配置文件 (写到一半断电/被杀软中断)。
     */
    public void Save()
    {
        try
        {
            var snapshot = new LauncherSettings
            {
                ServerDir = ServerDir,
                ClientDir = ClientDir,
                GamePort = GamePort,
                WebGmPort = WebGmPort,
                Account = Account,
                Password = Password,
                FlickerFixEnabled = FlickerFixEnabled,
                UiTheme = UiTheme,
                UiBackgrounds = new List<string>(UiBackgrounds ?? new List<string>()),
                UiBgIndex = UiBgIndex,
                UiOverlay = UiOverlay,
                UiArtFusion = UiArtFusion,
                UiGlassBlur = UiGlassBlur,
                UiGlassOpacity = UiGlassOpacity,
                UiScrimOpacity = UiScrimOpacity,
                FirstRunDone = FirstRunDone,
                AgreementAccepted = AgreementAccepted
            };
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomic(json);
        }
        catch { }
    }

    /// <summary>写临时文件 + 原子替换, 避免出现"写了一半"的配置文件</summary>
    static void WriteAtomic(string json)
    {
        var enc = new UTF8Encoding(false);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json, enc);
        if (File.Exists(FilePath))
        {
            try { File.Replace(tmp, FilePath, null); return; }
            catch
            {
                // 网络盘/FAT 不支持 Replace → 退化为覆盖
                File.Copy(tmp, FilePath, true);
                try { File.Delete(tmp); } catch { }
                return;
            }
        }
        File.Move(tmp, FilePath);
    }
}
