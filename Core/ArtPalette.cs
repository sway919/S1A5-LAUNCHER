/*
 * ==================================================================
 * ArtPalette — 原画自适应配色 (Core/ArtPalette.cs)
 * ==================================================================
 * 目标: 玩家换成任意背景图, 界面都能"融进原画, 又与原画有对比" ——
 *       色相跟着原画走, 明度差由规则保证, 于是既协调又清晰。
 *
 * 算法栈 (每一步都有论文/标准支撑, 数学实现见 Core/ColorScience.cs):
 *   1. 取主色 —— 5bit/通道直方图 + Material Color Utilities 的 Score
 *      (按面积×彩度打分; targetChroma=48, 权重 0.7/0.3/0.1, 彩度<5 或占比≤1% 淘汰)。
 *   2. 色彩空间 —— CAM16-UCS (Luo & Li 2017, CIE 248:2022)。Tone 用 CIE L*,
 *      色相/彩度用 CAM16 —— 即 Material You 的 HCT, 比 CIELAB 色相更线性。
 *   3. 色相分工 —— 中性面(卡片/按钮/文字/压暗带)用**原画本色相**(最融合);
 *      强调色默认同色相, 只在会撞上主按钮金色时, 从 Cohen-Or et al.,
 *      "Color Harmonization", SIGGRAPH 2006 的和声模板 (互补/分裂互补/三角/
 *      四方) 里挑替代色相, 而不是随手转一个角度。
 *   4. 明度(对比) —— 角色化 Tone 表 + WCAG 2.1 对比度钳制 + CIEDE2000
 *      (Sharma 2005) 的可感知明度差保证, 面板不会与原画糊在一起。
 *   5. 渐变 —— 全部在 Oklab (CSS Color 4 默认插值空间) 里取样, 中点不发灰。
 *
 * 工程实测约束 (自检脚本给出, 见 README §4):
 *   浅色面板的 Tone 必须落在 90~95 —— sRGB 在 Tone 96 以上能承载的彩度只剩
 *   0.3~4, 面板会染不上原画色相而退化成纯白。这条约束决定了下面 surfTone 的取值。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ServerUI.Core;

/* 原画配色档案 */
public sealed class ArtProfile
{
    public double TopTone, MidTone, BottomTone, AvgTone;   // 0..1 (即 L*/100)
    public double SourceHue = 200;
    public double SourceChroma;
    public bool HasSource;
    public int Samples;

    public double TopLum => TopTone;
    public double MidLum => MidTone;
    public double BottomLum => BottomTone;
    public double AvgLum => AvgTone;
    public bool TopIsDark => TopTone < 0.44;
    public bool BottomIsDark => BottomTone < 0.44;
    public bool TopIsLight => TopTone > 0.58;
    public bool BottomIsLight => BottomTone > 0.58;
    public bool Vivid => HasSource && SourceChroma > 6;
    public bool Valid => Samples > 0;
}

public static class ArtPalette
{
    /* 主按钮的金属金色相 (不可被原画挤掉) */
    const double HeroHue = 78;

    /* Cohen-Or 2006 的和声模板 (相对主色相的角度偏移), 按"对比强度"排序 */
    static readonly double[] HarmonicOffsets = { 180, 150, 210, 120, 240, 90, 270, 30, 330 };

    // =================================================================
    // 主色提取: 5bit/通道直方图 + Material Score
    // =================================================================
    public static ArtProfile Analyze(BitmapSource src)
    {
        var p = new ArtProfile();
        if (src == null || src.PixelWidth < 2 || src.PixelHeight < 2) return p;
        try
        {
            const int W = 128, H = 72;
            BitmapSource small = new TransformedBitmap(src,
                new ScaleTransform((double)W / src.PixelWidth, (double)H / src.PixelHeight));
            if (small.Format != PixelFormats.Bgra32)
                small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);

            int w = small.PixelWidth, h = small.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            small.CopyPixels(px, stride, 0);

            var hist = new Dictionary<int, int>();
            var sumR = new Dictionary<int, long>();
            var sumG = new Dictionary<int, long>();
            var sumB = new Dictionary<int, long>();
            double topSum = 0, midSum = 0, botSum = 0, allSum = 0;
            int topN = 0, midN = 0, botN = 0;
            int topEnd = Math.Max(1, (int)(h * 0.14));
            int botStart = (int)(h * 0.78);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    byte b = px[i], g = px[i + 1], r = px[i + 2];
                    var col = Color.FromRgb(r, g, b);

                    double tone = ColorScience.Lstar(col);       // 感知明度 (CIE L*)
                    allSum += tone;
                    if (y < topEnd) { topSum += tone; topN++; }
                    else if (y >= botStart) { botSum += tone; botN++; }
                    else { midSum += tone; midN++; }

                    int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    hist[key] = hist.TryGetValue(key, out var c0) ? c0 + 1 : 1;
                    sumR[key] = (sumR.TryGetValue(key, out var sr) ? sr : 0) + r;
                    sumG[key] = (sumG.TryGetValue(key, out var sg) ? sg : 0) + g;
                    sumB[key] = (sumB.TryGetValue(key, out var sb) ? sb : 0) + b;
                }
            }

            int n = w * h;
            p.Samples = n;
            p.AvgTone = allSum / n / 100.0;
            p.TopTone = (topN > 0 ? topSum / topN : allSum / n) / 100.0;
            p.MidTone = (midN > 0 ? midSum / midN : allSum / n) / 100.0;
            p.BottomTone = (botN > 0 ? botSum / botN : allSum / n) / 100.0;

            var bins = hist.Select(kv =>
            {
                int k = kv.Key, cnt = kv.Value;
                var col = Color.FromRgb((byte)(sumR[k] / cnt), (byte)(sumG[k] / cnt), (byte)(sumB[k] / cnt));
                var hct = ColorScience.FromColor(col);
                return new { Color = col, Count = cnt, Hct = hct, Tone = ColorScience.Lstar(col) };
            }).Where(b => b.Count > 0).ToList();

            const double targetChroma = 48, wProp = 0.7, wAbove = 0.3, wBelow = 0.1;
            const double cutoffChroma = 5.0, cutoffProp = 0.01;

            /*
             * 【明度下限 —— 这是"取到紫色"的根因修复】
             * 这张主图 90% 以上是接近全黑的暗部, 真正带颜色的像素大多是
             * 暗部雾气里的冷紫/冷蓝噪点 (彩度 12 左右、明度 L* 不到 10)。
             * 纯按"面积×彩度"打分, 赢的必然是这些暗部噪点 —— 于是界面被染成
             * 淡紫色 (#D8BAFB), 跟画面给人的印象完全不是一回事。
             * 极暗像素的"颜色"在感知上几乎不存在 (色觉在低明度下退化),
             * 所以按明度设一道门槛, 只让画面里真正看得见的颜色参与竞争:
             * 绯红腰带、橄榄色绑带、黄铜剑柄、银色剑刃、皮肤。
             */
            const double minTone = 15.0;

            var huePop = new double[360];
            foreach (var b in bins)
            {
                if (b.Hct.Chroma < cutoffChroma) continue;
                if (b.Tone < minTone) continue;          // 暗部噪点不参与
                huePop[((int)b.Hct.Hue) % 360] += b.Count;
            }
            var excited = new double[360];
            for (int hue = 0; hue < 360; hue++)
            {
                double prop = huePop[hue] / n;
                if (prop <= 0) continue;
                for (int i = hue - 14; i <= hue + 15; i++)
                    excited[((i % 360) + 360) % 360] += prop;
            }

            double best = double.NegativeInfinity;
            foreach (var b in bins)
            {
                if (b.Hct.Chroma < cutoffChroma) continue;
                if (b.Tone < minTone) continue;          // 同上: 暗部噪点不参选
                double prop = excited[((int)b.Hct.Hue) % 360];
                if (prop <= cutoffProp) continue;
                double chromaWeight = b.Hct.Chroma < targetChroma ? wBelow : wAbove;
                double score = prop * 100 * wProp + (b.Hct.Chroma - targetChroma) * chromaWeight;
                if (score > best)
                {
                    best = score;
                    p.SourceHue = b.Hct.Hue;
                    p.SourceChroma = b.Hct.Chroma;
                    p.HasSource = true;
                }
            }
        }
        catch { p.Samples = 0; }
        return p;
    }

    // =================================================================
    // 局部取色: 只分析"某块面板正下方那一片原画"
    // =================================================================
    /* 全图色相太容易被大色块/LOGO 带跑; 面板真正要融的是它**压着的那块画面**,
       所以首页两块浮动面板各自采样自己下方的区域。 */
    public static ArtProfile AnalyzeRegion(BitmapSource src, int x, int y, int w, int h)
    {
        try
        {
            x = Math.Max(0, Math.Min(x, src.PixelWidth - 2));
            y = Math.Max(0, Math.Min(y, src.PixelHeight - 2));
            w = Math.Max(2, Math.Min(w, src.PixelWidth - x));
            h = Math.Max(2, Math.Min(h, src.PixelHeight - y));
            return Analyze(new CroppedBitmap(src, new Int32Rect(x, y, w, h)));
        }
        catch { return new ArtProfile(); }
    }

    /// <summary>一块面板的底色(实色/渐变/描边), 由它下方那片原画决定</summary>
    public sealed class SurfaceSet
    {
        public Color Solid;
        public Brush Fill;
        public Brush Edge;
    }

    public static SurfaceSet MakeSurface(ArtProfile p, bool isDark, double fusion, double glassOpacity = 0.62)
    {
        fusion = Compat.Clamp(fusion, 0, 1);
        glassOpacity = Compat.Clamp(glassOpacity, 0.35, 1.0);
        double op = isDark ? glassOpacity : Math.Max(glassOpacity, 0.90);
        double tone = p.Valid ? p.MidTone * 100 : 30;
        double bright = Compat.Clamp((tone - 30) / 55, 0, 1);
        double hue = p.Vivid ? p.SourceHue : (isDark ? 196 : 193);
        double chroma = ((isDark ? 9 : 6) + Math.Min(p.SourceChroma * 0.05, 3)) * fusion;

        double surfTone = isDark ? 9 + 5 * bright : 92 - 2 * bright;
        // 与"它压着的那片画面"至少拉开 7 个 Tone (ΔE00 可感知), 否则白压白会糊
        if (Math.Abs(surfTone - tone) < 7)
            surfTone = isDark ? Math.Max(4, tone - 8) : Math.Min(93, tone + 8);

        Color solid = ColorScience.FromHct(surfTone, chroma, hue);
        var set = new SurfaceSet
        {
            Solid = solid,
            Fill = GradOklab(
                With(ColorScience.FromHct(surfTone + 1.5, chroma, hue),
                     (byte)Math.Round(Compat.Clamp(op + 0.02, 0.35, 1) * 255)),
                With(ColorScience.FromHct(surfTone - 2, chroma * 1.1, hue),
                     (byte)Math.Round(Compat.Clamp(op - 0.06, 0.35, 1) * 255))),
            Edge = GradOklab(
                isDark ? With(ColorScience.FromHct(98, chroma * 0.5, hue), 0x38) : ColorScience.FromHct(100, 0, hue),
                isDark ? With(ColorScience.FromHct(90, chroma * 0.5, hue), 0x10)
                       : With(ColorScience.FromHct(52 - 26 * bright, chroma * 1.4, hue), (byte)(0x28 + 0x20 * bright)))
        };
        return set;
    }

    // =================================================================
    // 环境适配 (颜色融合已关闭, 这里只做"可读性"判断, 不做任何染色)
    //   · 标题带/底部带该用深色还是浅色文字, 取决于该带在原画里是亮是暗;
    //   · 上下渐隐带的浓度由设置项 UiScrimOpacity 决定。
    // =================================================================
    public static void ApplyEnvironment(ArtProfile p, bool isDark, double scrimOpacity)
    {
        try
        {
            scrimOpacity = Compat.Clamp(scrimOpacity, 0, 1);
            /*
             * 标题带/底部带的明暗方向 ——【跟着主题走, 不再跟着原画走】(2026-10-08 改)。
             *
             * 原来的规则是"看该带在原画里是亮是暗", 于是浅色主题碰上这张深顶原画,
             * 标题带与底部带都变成深纱 + 浅字 —— 一个浅色界面被上下两条深色带夹住,
             * 与浅色的导航栏/面板直接割裂, 这就是"浅色模式不好看"的一半原因。
             *
             * 现在: 深色主题用深纱 + 浅字, 浅色主题用浅纱 + 深字。两种情况下的
             * 对比度都由【纱本身】保证 (纱在边缘是不透明的), 不依赖原画那块是亮是暗,
             * 所以不再需要按原画翻转 —— 翻转反而会做出"浅底深字压在深纱上"这种读不出的组合。
             */
            bool topDark = isDark;
            bool botDark = isDark;

            // 中性色, 无染色 (chroma 传 0)
            Set("Brush.ScrimTopGrad", ScrimGrad(topDark, true, 0, 0, scrimOpacity));
            Set("Brush.ScrimBotGrad", ScrimGrad(botDark, false, 0, 0, scrimOpacity));
            Chrome(topDark, 0, 0, 0, 0);

            // (内容页压暗沿用主题字典里的值, 这里不动)
        }
        catch { }
    }

    // =================================================================
    // 强调色色相: 默认与原画同色相, 撞【语义保留带】时才从和声模板里挑
    // =================================================================
    /*
     * 语义保留色相带 —— 这两个角色已经被占用了, 强调色不能落进去,
     * 否则"选中态"会和"主行动 / 危险操作"混在一起分不清:
     *   · HeroHue (78° 黄铜金) = 【开始游戏】这个全局唯一的主行动
     *   · DangerHue (8° 绯红)  = 停止服务端 / 删除 / 重新开局 这类危险操作
     *     (这个绯红就是主图里那条腰带和缠腕的颜色)
     * 实测这张主图经明度门槛筛完后, 主色相落在 21°(腰带绯红)。它属于危险带,
     * 于是按和声模板取互补 → 201°(钢蓝), 正好也是画面里剑刃与头发的冷色。
     */
    const double DangerHue = 8;

    static bool ReservedHue(double h)
        => HueDist(h, HeroHue) < 40 || HueDist(h, DangerHue) < 30;

    static double AccentHue(double artHue)
    {
        if (!ReservedHue(artHue)) return artHue;
        foreach (var off in HarmonicOffsets)
        {
            double cand = Wrap(artHue + off);
            if (!ReservedHue(cand) && HueDist(cand, artHue) >= 60) return cand;
        }
        return Wrap(artHue + 180);
    }

    // =================================================================
    // 角色分配 + 注入
    // =================================================================
    /// <param name="fusion">原画融合度 0~1 (设置里的滑杆)。只缩放"染色量",
    /// 不改变角色 Tone 表 —— 于是 0 也能保持干净的黑/白, 1 才完全跟随原画。</param>
    public static void Apply(ArtProfile p, bool isDark, double fusion = 0.30, double glassOpacity = 0.62)
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            fusion = Compat.Clamp(fusion, 0, 1);
            glassOpacity = Compat.Clamp(glassOpacity, 0.35, 1.0);
            /*
             * 浅色主题的深色文字更怕"透" —— 保底要比深色高一截。
             * 原来是 0.78: 实测浅色下卡片只是"奶白糊", 深色原画从底下透上来把
             * 对比度吃掉了 (2026-10-08 用户反馈"浅色模式非常不好看")。
             * 提到 0.90 之后面板是干净的暖瓷白, 原画只留在色温上一点点 ——
             * 也就是本文件头写的"91~96% 白"那个取向 (以前代码没做到)。
             */
            double op = isDark ? glassOpacity : Math.Max(glassOpacity, 0.90);
            byte GA(double extra) => (byte)Math.Round(Compat.Clamp(op + extra, 0.35, 1.0) * 255);
            double artHue = p.Vivid ? p.SourceHue : (isDark ? 196 : 193);
            double pHue = AccentHue(artHue);          // 强调色色相 (可能被和声模板换掉)
            double nHue = artHue;                     // 中性面/文字/压暗带 = 原画本色相
            double srcC = p.SourceChroma;
            // 强调色彩度: 基础取 30 (比 Material TonalSpot 的 36 更收敛 ——
            // 36 在高明度下会偏"糖果色", 30 更接近画面里那种冷钢感),
            // 融合度越高原画彩度越能带进来
            double artAccentC = Compat.Clamp(Math.Max(30, srcC * 0.8), 28, 56);
            double pChroma = 30 + (artAccentC - 30) * fusion;

            // 原画整体明暗 → 决定面板往哪边走
            double artTone = p.Valid ? p.MidTone * 100 : 25;
            double bright = Compat.Clamp((artTone - 30) / 55, 0, 1);   // 0=很暗, 1=很亮

            // ---- 面板 Tone: 必须落在彩度承载能力足够的区间 (见文件头) ----
            double surfTone = isDark ? 9 + 5 * bright : 92 - 2 * bright;
            // 与原画所在区域至少拉开 7 个 Tone (ΔE00 可感知), 否则"白压白"会糊
            if (Math.Abs(surfTone - artTone) < 7)
                surfTone = isDark ? Math.Max(4, artTone - 8) : Math.Min(93, artTone + 8);

            double surfTone2 = isDark ? surfTone + 4 : surfTone + 1.5;   // 悬浮层/输入框
            double surfTone3 = isDark ? surfTone + 8 : surfTone - 3;     // 悬停
            double onTone = isDark ? 92 : 12;
            double onTone2 = isDark ? 78 : 35;
            double onTone3 = isDark ? 58 : 52;

            // 中性面彩度: 请求值控制在 Tone 的承载范围内 (Tone92 约 10), 再乘融合度。
            // 融合度 0.3 (默认) 时只剩 2~4 —— 白/黑保持干净, 只留一点原画色温。
            double nChroma = ((isDark ? 9 : 6) + 3 * bright + Math.Min(srcC * 0.05, 3)) * fusion;

            Color surf = ColorScience.FromHct(surfTone, nChroma, nHue);
            Color surfS = ColorScience.FromHct(surfTone2, nChroma * 0.9, nHue);
            Color on = EnsureContrast(onTone, nChroma * 0.4, nHue, surf, 8.0, !isDark);
            Color on2 = EnsureContrast(onTone2, nChroma * 0.6, nHue, surf, 4.5, !isDark);
            Color on3 = EnsureContrast(onTone3, nChroma * 0.5, nHue, surf, 3.0, !isDark);

            // ---- 强调色 (primary) ----
            double pTone = isDark ? 80 : 40;
            Color accent = ColorScience.FromHct(pTone, pChroma, pHue);
            Color accentHover = ColorScience.FromHct(pTone + 6, pChroma, pHue);
            Color accentPress = ColorScience.FromHct(pTone - 8, pChroma, pHue);
            Color accentText = isDark
                ? EnsureContrast(80, pChroma * 0.85, pHue, surf, 4.5, true)
                : EnsureContrast(40, pChroma, pHue, surf, 4.5, false);
            Color onAccent = isDark ? ColorScience.FromHct(20, pChroma * 0.5, pHue)
                                    : ColorScience.FromHct(100, 0, pHue);
            Color softC = ColorScience.FromHct(isDark ? 30 : 90, Math.Min(pChroma * 0.3, 12), pHue);

            Set("Brush.Accent", accent);
            Set("Brush.AccentHover", accentHover);
            Set("Brush.AccentPress", accentPress);
            Set("Brush.AccentText", accentText);
            Set("Brush.AccentSoft", With(softC, isDark ? (byte)0x4D : (byte)0x33));
            Set("Brush.AccentLine", With(accent, isDark ? (byte)0x8A : (byte)0x66));
            Set("Brush.OnAccent", onAccent);
            Set("Brush.BadgeFill", With(softC, isDark ? (byte)0x3D : (byte)0x29));
            Set("Brush.BadgeLine", With(accent, isDark ? (byte)0x70 : (byte)0x59));

            // ---- 面 (卡片/按钮/输入框) ----
            // 玻璃层的 alpha 由"面板不透明度"驱动 —— 面板下面就是毛玻璃采样层
            Set("Brush.Glass", With(surf, GA(0)));
            Set("Brush.GlassStrong", With(surfS, GA(0.10)));
            Set("Brush.GlassHover", With(ColorScience.FromHct(surfTone3, nChroma * 0.8, nHue), GA(0.16)));
            Set("Brush.GlassPress", With(ColorScience.FromHct(isDark ? surfTone + 12 : surfTone - 6, nChroma, nHue), GA(0.06)));
            Set("Brush.GlassGrad", GradOklab(
                With(ColorScience.FromHct(surfTone + 1.5, nChroma, nHue), GA(0.02)),
                With(ColorScience.FromHct(surfTone - 2, nChroma * 1.1, nHue), GA(-0.06))));
            Set("Brush.GlassEdge", GradOklab(
                isDark ? With(ColorScience.FromHct(98, nChroma * 0.5, nHue), 0x38) : ColorScience.FromHct(100, 0, nHue),
                isDark ? With(ColorScience.FromHct(90, nChroma * 0.5, nHue), 0x10)
                       : With(ColorScience.FromHct(52 - 26 * bright, nChroma * 1.4, nHue), (byte)(0x28 + 0x20 * bright))));
            // 受光高光: 深色给得明显一点 (玻璃质感); 浅色也给一点点 ——
            // 浅色面板几乎不透明, 没高光就是一块死平的奶白。
            Set("Brush.CardSheen", Sheen(isDark ? 0.09 : 0.06, nHue));

            Set("Brush.FieldFill", isDark
                ? With(ColorScience.FromHct(96, nChroma * 0.6, nHue), 0x1C)
                : With(ColorScience.FromHct(surfTone2, nChroma * 0.7, nHue), 0xF2));
            Set("Brush.FieldBorder", isDark
                ? With(ColorScience.FromHct(90, nChroma * 0.8, nHue), 0x2E)
                : With(ColorScience.FromHct(onTone3, nChroma * 0.9, nHue), (byte)(0x24 + 0x18 * bright)));
            Set("Brush.HoverSoft", isDark
                ? With(ColorScience.FromHct(92, nChroma * 0.7, nHue), 0x17)
                : With(ColorScience.FromHct(onTone3, nChroma, nHue), 0x14));
            Set("Brush.Sep", isDark
                ? With(ColorScience.FromHct(92, nChroma * 0.6, nHue), 0x1A)
                : With(ColorScience.FromHct(onTone3, nChroma * 0.9, nHue), 0x17));
            Set("Brush.ProgressTrack", isDark ? With(ColorScience.FromHct(90, nChroma * 0.6, nHue), 0x2E)
                                              : With(ColorScience.FromHct(onTone3, nChroma, nHue), 0x24));
            Set("Brush.Scroll", isDark ? With(ColorScience.FromHct(90, nChroma * 0.6, nHue), 0x4D)
                                       : With(ColorScience.FromHct(onTone3, nChroma, nHue), 0x33));

            // ---- 文字 (中性与原画同色相, 对比度按 8:1 / 4.5:1 钳制) ----
            Set("Brush.Text", on);
            Set("Brush.Text2", on2);
            Set("Brush.Text3", on3);

            // ---- 日志控制台: 两套主题都保持深底 ----
            Color logBg = ColorScience.FromHct(7, Math.Max(nChroma, 2.5), nHue);
            Set("Brush.LogBg", With(logBg, 0xF2));
            Set("Brush.LogText", EnsureContrast(90, nChroma * 0.5, nHue, logBg, 8.0, true));

            // ---- 内容页遮罩 / 弹窗压暗 ----
            Set("Brush.ShadeSolid", isDark
                ? ColorScience.FromHct(Math.Max(surfTone - 4, 2), nChroma, nHue)
                : ColorScience.FromHct(55, nChroma * 1.2, nHue, 0x3D));
            Set("Brush.FadeSolid", ColorScience.FromHct(4, nChroma, nHue));

            // ---- 标题带 / 底部带: 按该带在原画里的明暗取一组"保证对比"的 Tone 对 ----
            bool topDark = p.TopIsDark || (!p.TopIsLight && isDark);
            bool botDark = p.BottomIsDark || (!p.BottomIsLight && isDark);
            Set("Brush.ScrimTopGrad", ScrimGrad(topDark, true, nHue, nChroma));
            Set("Brush.ScrimBotGrad", ScrimGrad(botDark, false, nHue, nChroma));
            Chrome(topDark, nHue, nChroma, pHue, pChroma);

            // ---- 主按钮: 金色用 Tone 表达; 原画偏暖金时压深并描冷边 ----
            bool warmArt = HueDist(nHue, HeroHue) < 45;
            double hTop = warmArt ? 82 : 90, hBot = warmArt ? 60 : 68;
            Set("Brush.HeroFill", GradOklab(
                ColorScience.FromHct(hTop, 34, HeroHue), ColorScience.FromHct(hBot, 46, HeroHue)));
            Set("Brush.HeroFillHot", GradOklab(
                ColorScience.FromHct(hTop + 6, 34, HeroHue), ColorScience.FromHct(hBot + 6, 46, HeroHue)));
            Set("Brush.HeroFillPress", GradOklab(
                ColorScience.FromHct(hTop - 8, 34, HeroHue), ColorScience.FromHct(hBot - 8, 46, HeroHue)));
            Set("Brush.HeroLine", warmArt
                ? With(ColorScience.FromHct(62, 40, Wrap(HeroHue + 155)), 0x8C)
                : With(ColorScience.FromHct(hBot + 8, 40, HeroHue), 0x7A));
            Set("Brush.MarkGrad", GradOklab(
                ColorScience.FromHct(isDark ? 82 : 56, pChroma * 0.9, pHue),
                ColorScience.FromHct(isDark ? 62 : 40, pChroma, pHue)));

            // ---- 官方 Fluent 令牌里的强调填充 (滑块/进度条) ----
            Color fluentAccent = ColorScience.FromHct(isDark ? 52 : 42, pChroma, pHue);
            Set("AccentFillColorDefaultBrush", fluentAccent);
            Set("AccentFillColorSecondaryBrush", With(fluentAccent, 0xE6));
            Set("AccentFillColorTertiaryBrush", With(fluentAccent, 0xB3));
            Set("SystemAccentColor", fluentAccent);
        }
        catch { }
    }

    // =================================================================
    // 标题栏 (chrome) 令牌 —— 只看该带在原画里是深是浅, 与页面主题解耦
    // 深带: 底 Tone≈10 / 字 Tone≈96, 浅带: 底 Tone≈97 / 字 Tone≈12 (对比度天然够)
    // =================================================================
    static void Chrome(bool dark, double nHue, double nChroma, double pHue, double pChroma)
    {
        if (dark)
        {
            Set("Brush.ChromeFg", ColorScience.FromHct(96, nChroma * 0.4, nHue));
            Set("Brush.ChromeFg2", ColorScience.FromHct(80, nChroma * 0.7, nHue));
            Set("Brush.ChromeFg3", ColorScience.FromHct(60, nChroma * 0.7, nHue));
            Set("Brush.ChromeHover", With(ColorScience.FromHct(95, nChroma * 0.7, nHue), 0x1A));
            Set("Brush.ChromeSep", With(ColorScience.FromHct(95, nChroma * 0.5, nHue), 0x24));
            Set("Brush.ChromeCapsule", With(ColorScience.FromHct(10, nChroma, nHue), 0xC6));
            Set("Brush.ChromeCapsuleBorder", With(ColorScience.FromHct(95, nChroma * 0.5, nHue), 0x26));
            Set("Brush.ChromeAccent", ColorScience.FromHct(82, pChroma * 0.9, pHue));
            Set("Brush.ChromeAccentSoft", With(ColorScience.FromHct(80, Math.Min(pChroma, 20), pHue), 0x33));
            Set("Brush.ChromeAccentLine", With(ColorScience.FromHct(82, pChroma * 0.9, pHue), 0x8A));
        }
        else
        {
            Set("Brush.ChromeFg", ColorScience.FromHct(12, nChroma * 0.5, nHue));
            Set("Brush.ChromeFg2", ColorScience.FromHct(34, nChroma * 0.8, nHue));
            Set("Brush.ChromeFg3", ColorScience.FromHct(52, nChroma * 0.8, nHue));
            Set("Brush.ChromeHover", With(ColorScience.FromHct(20, nChroma, nHue), 0x14));
            Set("Brush.ChromeSep", With(ColorScience.FromHct(30, nChroma, nHue), 0x1F));
            Set("Brush.ChromeCapsule", With(ColorScience.FromHct(97, nChroma * 0.6, nHue), 0xE6));
            Set("Brush.ChromeCapsuleBorder", With(ColorScience.FromHct(45, nChroma, nHue), 0x24));
            Set("Brush.ChromeAccent", ColorScience.FromHct(38, pChroma, pHue));
            Set("Brush.ChromeAccentSoft", With(ColorScience.FromHct(90, Math.Min(pChroma * 0.4, 12), pHue), 0x33));
            Set("Brush.ChromeAccentLine", With(ColorScience.FromHct(40, pChroma, pHue), 0x80));
        }

        // 标题栏左右两端的渐变底 —— 与胶囊同色, 从边缘向中间渐隐。
        // 目的是让左侧品牌文字与右侧状态/按钮从原画上"浮"起来:
        // 只靠 1px 字色描边在花哨的原画上读不清, 加一条渐隐底就稳了。
        var barBase = dark ? ColorScience.FromHct(10, nChroma, nHue)
                           : ColorScience.FromHct(97, nChroma * 0.6, nHue);
        Set("Brush.ChromeBarLeft", BarGrad(barBase, true));
        Set("Brush.ChromeBarRight", BarGrad(barBase, false));
    }

    /* 标题栏端部渐变: 贴边一侧实、向中间渐隐 (fromLeft=true 时左端实) */
    static LinearGradientBrush BarGrad(Color baseColor, bool fromLeft)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = fromLeft ? new Point(0, 0) : new Point(1, 0),
            EndPoint = fromLeft ? new Point(1, 0) : new Point(0, 0)
        };
        g.GradientStops.Add(new GradientStop(With(baseColor, 0xE0), 0));
        g.GradientStops.Add(new GradientStop(With(baseColor, 0x9E), 0.5));
        g.GradientStops.Add(new GradientStop(With(baseColor, 0x00), 1));
        g.Freeze();
        return g;
    }

    /* 压暗/提亮渐变: 与原画同色相的低彩度中性色, 不是"发灰的塑料带" */
    static LinearGradientBrush ScrimGrad(bool dark, bool top, double hue, double chroma,
                                         double opacity = 0.60)
    {
        Color solid = dark ? ColorScience.FromHct(9, Math.Max(chroma, 1.5), hue)
                           : ColorScience.FromHct(97, chroma * 0.6, hue);
        byte A(double f) => (byte)Math.Round(Compat.Clamp(opacity * f, 0, 1) * 255);
        byte a0 = A(1.00);
        byte a1 = A(0.60);
        byte a2 = A(0.92);
        byte a3 = A(1.00);
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (top)
        {
            g.GradientStops.Add(new GradientStop(With(solid, a0), 0));
            g.GradientStops.Add(new GradientStop(With(solid, a1), 0.45));
            g.GradientStops.Add(new GradientStop(With(solid, 0), 1));
        }
        else
        {
            g.GradientStops.Add(new GradientStop(With(solid, 0), 0));
            g.GradientStops.Add(new GradientStop(With(solid, a2), 0.6));
            g.GradientStops.Add(new GradientStop(With(solid, a3), 1));
        }
        g.Freeze();
        return g;
    }

    static RadialGradientBrush Sheen(double a, double hue)
    {
        var g = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.12, 0),
            Center = new Point(0.12, 0),
            RadiusX = 0.9,
            RadiusY = 0.75
        };
        Color c = ColorScience.FromHct(100, 0, hue, (byte)Math.Round(a * 255));
        g.GradientStops.Add(new GradientStop(c, 0));
        g.GradientStops.Add(new GradientStop(With(c, 0), 1));
        g.Freeze();
        return g;
    }

    // =================================================================
    // 小工具
    // =================================================================
    /* Oklab 取样渐变: WPF 的 LinearGradientBrush 只在 sRGB 里线性插值,
       中点会发暗发灰; 这里先在 Oklab 里取若干中间色做多段渐变 */
    static LinearGradientBrush GradOklab(Color a, Color b, int steps = 5)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.35, 1) };
        for (int i = 0; i < steps; i++)
        {
            double t = (double)i / (steps - 1);
            g.GradientStops.Add(new GradientStop(ColorScience.Mix(a, b, t), t));
        }
        g.Freeze();
        return g;
    }

    /* 对比度钳制 (pywal 思路 + WCAG): 不足就沿 Tone 推, 直到达标 */
    static Color EnsureContrast(double tone, double chroma, double hue, Color bg, double want, bool toLight, byte a = 255)
    {
        Color fg = ColorScience.FromHct(tone, chroma, hue, a);
        for (int i = 0; i < 40 && ColorScience.Contrast(ColorScience.Flatten(fg, bg), bg) < want; i++)
        {
            tone += toLight ? 2 : -2;
            if (tone <= 0 || tone >= 100) break;
            fg = ColorScience.FromHct(tone, chroma, hue, a);
        }
        return fg;
    }

    static void Set(string key, Brush b)
    {
        var app = Application.Current;
        if (app != null) app.Resources[key] = b;
    }

    static void Set(string key, Color c) => Set(key, new SolidColorBrush(c));

    static Color With(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static double Wrap(double h)
    {
        h %= 360;
        return h < 0 ? h + 360 : h;
    }

    static double HueDist(double a, double b)
    {
        double d = Math.Abs(Wrap(a) - Wrap(b)) % 360;
        return d > 180 ? 360 - d : d;
    }
}
