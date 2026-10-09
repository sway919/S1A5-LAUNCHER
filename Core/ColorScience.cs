/*
 * ==================================================================
 * ColorScience — 感知色彩计算 (Core/ColorScience.cs)
 * ==================================================================
 * 全部算法都有论文/标准支撑, 用于替代此前"HSL 拍脑袋 + 欧氏色差"的做法:
 *
 *  1. CAM16 与 CAM16-UCS —— Luo & Li, "Uniform colour spaces based on
 *     CIECAM16", Color Research & Application 2017; 并被 CIE 248:2022 采纳。
 *     Material You 的 HCT 就是"CAM16 的色相/彩度 + CIE L* 的明度"。
 *     比 CIELAB 的色相更线性 (蓝紫不会偏色), 渐变/取色不会发脏。
 *  2. CIEDE2000 —— Sharma, Wu, Dalal, "The CIEDE2000 color-difference formula",
 *     Color Research & Application 2005。判定"两种颜色是否真的看得出区别",
 *     用来保证面板与原画、文字与面板之间不会被糊在一起。
 *  3. Oklab / Oklch —— Ottosson 2020, 已被 CSS Color 4 采纳为默认插值空间。
 *     渐变与混色在 Oklab 里做, 中点不会变暗变灰 (sRGB 插值的经典问题)。
 *  4. WCAG 2.1 相对亮度 + 对比度, 用于文字可读性的硬约束。
 *
 * 为什么不直接用 Mixbox (scrtwpns/mixbox, 3.4k★, SIGGRAPH Asia 2021
 * "Practical Pigment Mixing for Digital Painting"): 它是颜料混合的最前沿,
 * 但精度依赖约 1MB 的查找表, 与"零依赖单文件 EXE"的分发方式冲突。
 * 本文件用 Oklab 插值达到"混合不发脏"的同等效果。
 *
 * 命名沿用 HCT: Tone = CIE L* (0..100), Chroma/Hue = CAM16 的 C / h。
 * ==================================================================
 */
using System;
using System.Windows.Media;

namespace ServerUI.Core;

public static class ColorScience
{
    // =================================================================
    // sRGB ↔ 线性 ↔ XYZ (D65)
    // =================================================================
    public static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double c)
        => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    static byte B8(double v) => (byte)Math.Round(Compat.Clamp(v, 0, 1) * 255);

    /// <summary>sRGB → XYZ, Y 归一到 0..100 (CAM16 用)</summary>
    public static void XyzFromColor(Color c, out double x, out double y, out double z)
    {
        double r = SrgbToLinear(c.R / 255.0), g = SrgbToLinear(c.G / 255.0), b = SrgbToLinear(c.B / 255.0);
        x = (0.41233895 * r + 0.35762064 * g + 0.18051042 * b) * 100;
        y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b) * 100;
        z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) * 100;
    }

    public static void LinearRgb(double x, double y, double z, out double r, out double g, out double b)
    {
        x /= 100; y /= 100; z /= 100;
        r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        b = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
    }

    public static Color ColorFromXyz(double x, double y, double z, byte alpha = 255)
    {
        LinearRgb(x, y, z, out var r, out var g, out var b);
        return Color.FromArgb(alpha, B8(LinearToSrgb(r)), B8(LinearToSrgb(g)), B8(LinearToSrgb(b)));
    }

    // =================================================================
    // CIE L* (Tone)
    // =================================================================
    public static double LstarFromY(double y)
    {
        double yy = y / 100.0;
        return yy <= 216.0 / 24389.0 ? yy * 24389.0 / 27.0 : Compat.Cbrt(yy) * 116 - 16;
    }

    public static double YFromLstar(double lstar)
        => (lstar > 8 ? Math.Pow((lstar + 16) / 116, 3) : lstar / (24389.0 / 27.0)) * 100.0;

    public static double Lstar(Color c)
    {
        XyzFromColor(c, out _, out var y, out _);
        return LstarFromY(y);
    }

    // =================================================================
    // Oklab / Oklch (渐变插值用; CIE 与 CSS Color 4 都采用)
    // =================================================================
    public static void OklabFromColor(Color c, out double L, out double a, out double b)
    {
        double r = SrgbToLinear(c.R / 255.0), g = SrgbToLinear(c.G / 255.0), bb = SrgbToLinear(c.B / 255.0);
        double l = Compat.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bb);
        double m = Compat.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bb);
        double s = Compat.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bb);
        L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        b = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
    }

    public static Color ColorFromOklab(double L, double a, double b, byte alpha = 255)
    {
        double l = L + 0.3963377774 * a + 0.2158037573 * b;
        double m = L - 0.1055613458 * a - 0.0638541728 * b;
        double s = L - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return Color.FromArgb(alpha,
            B8(LinearToSrgb(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s)),
            B8(LinearToSrgb(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s)),
            B8(LinearToSrgb(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s)));
    }

    /// <summary>在 Oklab 里插值 (CSS Color 4 默认做法), 中点不发灰</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Compat.Clamp(t, 0, 1);
        OklabFromColor(a, out var l1, out var a1, out var b1);
        OklabFromColor(b, out var l2, out var a2, out var b2);
        return ColorFromOklab(l1 + (l2 - l1) * t, a1 + (a2 - a1) * t, b1 + (b2 - b1) * t,
                              (byte)Math.Round(a.A + (b.A - a.A) * t));
    }

    // =================================================================
    // CIEDE2000 (Sharma 2005) —— 感知色差 ΔE00
    // =================================================================
    public static double DeltaE2000(Color c1, Color c2)
    {
        Lab(c1, out var L1, out var a1, out var b1);
        Lab(c2, out var L2, out var a2, out var b2);

        double kL = 1, kC = 1, kH = 1;
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1), C2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double Cbar = (C1 + C2) / 2;
        double Cbar7 = Math.Pow(Cbar, 7);
        double G = 0.5 * (1 - Math.Sqrt(Cbar7 / (Cbar7 + 6103515625.0)));   // 25^7
        double a1p = (1 + G) * a1, a2p = (1 + G) * a2;
        double C1p = Math.Sqrt(a1p * a1p + b1 * b1), C2p = Math.Sqrt(a2p * a2p + b2 * b2);
        double h1p = HueAngle(b1, a1p), h2p = HueAngle(b2, a2p);

        double dLp = L2 - L1, dCp = C2p - C1p;
        double dhp;
        if (C1p * C2p == 0) dhp = 0;
        else
        {
            dhp = h2p - h1p;
            if (dhp > 180) dhp -= 360; else if (dhp < -180) dhp += 360;
        }
        double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(Rad(dhp / 2));

        double Lbarp = (L1 + L2) / 2, Cbarp = (C1p + C2p) / 2;
        double hbarp;
        if (C1p * C2p == 0) hbarp = h1p + h2p;
        else
        {
            hbarp = (h1p + h2p) / 2;
            if (Math.Abs(h1p - h2p) > 180) hbarp += (h1p + h2p) < 360 ? 180 : -180;
        }

        double T = 1 - 0.17 * Math.Cos(Rad(hbarp - 30)) + 0.24 * Math.Cos(Rad(2 * hbarp))
                     + 0.32 * Math.Cos(Rad(3 * hbarp + 6)) - 0.20 * Math.Cos(Rad(4 * hbarp - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hbarp - 275) / 25, 2));
        double Cbarp7 = Math.Pow(Cbarp, 7);
        double RC = 2 * Math.Sqrt(Cbarp7 / (Cbarp7 + 6103515625.0));
        double Lm50 = (Lbarp - 50) * (Lbarp - 50);
        double SL = 1 + (0.015 * Lm50) / Math.Sqrt(20 + Lm50);
        double SC = 1 + 0.045 * Cbarp;
        double SH = 1 + 0.015 * Cbarp * T;
        double RT = -Math.Sin(Rad(2 * dTheta)) * RC;

        double tL = dLp / (kL * SL), tC = dCp / (kC * SC), tH = dHp / (kH * SH);
        return Math.Sqrt(tL * tL + tC * tC + tH * tH + RT * tC * tH);
    }

    static double HueAngle(double b, double ap)
    {
        if (b == 0 && ap == 0) return 0;
        double h = Math.Atan2(b, ap) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    static double Rad(double deg) => deg * Math.PI / 180;

    static void Lab(Color c, out double L, out double a, out double b)
    {
        XyzFromColor(c, out var x, out var y, out var z);
        double fx = F(x / 95.047), fy = F(y / 100.0), fz = F(z / 108.883);
        L = 116 * fy - 16;
        a = 500 * (fx - fy);
        b = 200 * (fy - fz);
    }

    static double F(double t)
        => t > 216.0 / 24389.0 ? Compat.Cbrt(t) : (24389.0 / 27.0 * t + 16) / 116;

    // =================================================================
    // CAM16 (Luo & Li 2017) —— 本文件的核心
    // =================================================================
    /* 标准观察条件 (与 Material You 的 HCT 取同一组参数):
       D65 白点 / La=11.72 / Yb=20 / 平均环绕 */
    sealed class Viewing
    {
        public double N, Z, Nbb, Ncb, Aw, FlRoot, C;
        public double[] D = new double[3];      // 适应因子
    }

    static readonly Viewing VC = MakeViewing();

    static Viewing MakeViewing()
    {
        double[] wp = { 95.047, 100.0, 108.883 };
        double rw = wp[0] * 0.401288 + wp[1] * 0.650173 + wp[2] * -0.051461;
        double gw = wp[0] * -0.250268 + wp[1] * 1.204414 + wp[2] * 0.045854;
        double bw = wp[0] * -0.002079 + wp[1] * 0.048952 + wp[2] * 0.953127;

        const double la = 11.72, yb = 20.0, surround = 2.0;
        double f = 0.8 + surround / 10.0;                       // 1.0
        double c = f >= 0.9 ? Lerp(0.59, 0.69, (f - 0.9) * 10.0) : Lerp(0.525, 0.59, (f - 0.8) * 10.0);
        double d = Compat.Clamp(f * (1 - (1.0 / 3.6) * Math.Exp((-la - 42.0) / 92.0)), 0, 1);
        double nc = f;
        double k = 1.0 / (5.0 * la + 1.0);
        double k4 = k * k * k * k;
        double k4F = 1 - k4;
        double fl = k4 * la + 0.1 * k4F * k4F * Compat.Cbrt(5.0 * la);
        double n = yb / wp[1];
        double z = 1.48 + Math.Sqrt(n);
        double nbb = 0.725 * Math.Pow(1.0 / n, 0.2);
        double ncb = nbb;

        double flRoot = Math.Pow(fl, 0.25);
        var v = new Viewing
        {
            N = n, Z = z, Nbb = nbb, Ncb = ncb, C = c, FlRoot = flRoot,
            D = new[] { d * (100.0 / rw) + 1 - d, d * (100.0 / gw) + 1 - d, d * (100.0 / bw) + 1 - d }
        };
        // 白点的 achromatic response
        double rp = Nonlin(v.D[0] * rw, flRoot), gp = Nonlin(v.D[1] * gw, flRoot), bp = Nonlin(v.D[2] * bw, flRoot);
        double aw = (2 * rp + gp + 0.05 * bp - 0.305) * nbb;
        v.Aw = aw;
        return v;
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * Compat.Clamp(t, 0, 1);

    static double Nonlin(double channel, double flRoot)
    {
        double x = Math.Pow(Math.Abs(channel) * flRoot / 100.0, 0.42);
        return Math.Sign(channel) * 400.0 * x / (x + 27.13);
    }

    public readonly struct Hct
    {
        public readonly double Tone, Chroma, Hue;   // Tone = L*, Chroma/Hue = CAM16
        public Hct(double t, double c, double h) { Tone = t; Chroma = c; Hue = h; }
    }

    /// <summary>RGB → HCT</summary>
    public static Hct FromColor(Color c)
    {
        XyzFromColor(c, out var x, out var y, out var z);
        FromXyz(x, y, z, out var j, out var chroma, out var hue);
        return new Hct(LstarFromY(y), chroma, hue);
    }

    static void FromXyz(double x, double y, double z, out double J, out double C, out double h)
    {
        double r = x * 0.401288 + y * 0.650173 + z * -0.051461;
        double g = x * -0.250268 + y * 1.204414 + z * 0.045854;
        double b = x * -0.002079 + y * 0.048952 + z * 0.953127;
        double rp = Nonlin(VC.D[0] * r, VC.FlRoot), gp = Nonlin(VC.D[1] * g, VC.FlRoot), bp = Nonlin(VC.D[2] * b, VC.FlRoot);

        double a = (11 * rp - 12 * gp + bp) / 11.0;
        double bb = (rp + gp - 2 * bp) / 9.0;
        double hRad = Math.Atan2(bb, a);
        h = hRad * 180 / Math.PI;
        if (h < 0) h += 360;

        double et = 0.25 * (Math.Cos(hRad + 2) + 3.8);
        double t = (50000.0 / 13.0 * VC.Ncb * et * Math.Sqrt(a * a + bb * bb))
                 / (rp + gp + 1.05 * bp + 0.305);
        double alpha = Math.Pow(t, 0.9) * Math.Pow(1.64 - Math.Pow(0.29, VC.N), 0.73);
        double ac = (2 * rp + gp + 0.05 * bp - 0.305) * VC.Nbb;
        // 模型的定义域是 A>0; 纯黑 (Tone<0.8) 时 A 会转负, pow(负数, 小数) 会出 NaN。
        // 极限值就是 J=0 (黑), 这里显式取极限 —— 与 CIECAM16 的物理含义一致。
        double ratio = ac / VC.Aw;
        J = ratio <= 0 ? 0.0 : 100.0 * Math.Pow(ratio, VC.C * VC.Z);
        C = alpha * Math.Sqrt(J / 100.0);
    }

    /// <summary>HCT → RGB: 先按 Tone 反解 J, 再降彩度出色域</summary>
    public static Color FromHct(double tone, double chroma, double hue, byte alpha = 255)
    {
        tone = Compat.Clamp(tone, 0, 100);
        double targetY = YFromLstar(tone);
        // 二分找"这个 Tone 下能保真承载的最大彩度":
        //   · 请求彩度本来就可达 → 原样返回, 不做任何削减;
        //   · 请求超出色域 (如 Tone 96 以上要 C=16) → 收敛到实际可达值,
        //     而不会像逐步试探那样一路塌到中性灰。
        // 判据用往返彩度 (sRGB 是 8bit, 近白处量化本身就会吃掉一点彩度)。
        double want = Math.Max(0, chroma);
        double lo = 0, hi = want;
        for (int i = 0; i < 22; i++)
        {
            double mid = (lo + hi) / 2;
            double jj = JForY(targetY, mid, hue);
            Color cc = FromJch(jj, mid, hue, alpha);
            var bk = FromColor(Color.FromRgb(cc.R, cc.G, cc.B));
            if (bk.Chroma >= mid - Math.Max(0.35, mid * 0.05)) lo = mid; else hi = mid;
        }
        double cf = lo;
        double jf = JForY(targetY, cf, hue);
        return FromJch(jf, cf, hue, alpha);
    }

    /* 是否落在 sRGB 色域内 (线性通道容差) */
    static bool InGamutJch(double j, double c, double h, double tol = 0.005)
    {
        XyzFromJch(j, c, h, out var x, out var y, out var z);
        LinearRgb(x, y, z, out var r, out var g, out var b);
        return r >= -tol && r <= 1 + tol && g >= -tol && g <= 1 + tol && b >= -tol && b <= 1 + tol;
    }

    /* 二分求 J: 使 (J, C, h) 的 Y 落在目标 Y (Y 对 J 单调) */
    static double JForY(double targetY, double c, double h)
    {
        double lo = 0, hi = 100;
        // 深色区 J 会小到 0.1 量级, 定长 22 步的绝对精度不够 → 多切几刀
        for (int i = 0; i < 34; i++)
        {
            double mid = (lo + hi) / 2;
            XyzFromJch(mid, c, h, out _, out var y, out _);
            if (y < targetY) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    static Color FromJch(double j, double c, double h, byte alpha)
    {
        XyzFromJch(j, c, h, out var x, out var y, out var z);
        return ColorFromXyz(x, y, z, alpha);
    }

    static void XyzFromJch(double j, double c, double h, out double x, out double y, out double z)
    {
        // 正变换是 C = alpha * sqrt(J/100); 所以反解 alpha = C / sqrt(J/100)
        double alpha = (c == 0 || j == 0) ? 0.0 : c / Math.Sqrt(j / 100.0);
        double t = Math.Pow(alpha / Math.Pow(1.64 - Math.Pow(0.29, VC.N), 0.73), 1.0 / 0.9);
        double hRad = h * Math.PI / 180;
        double eHue = 0.25 * (Math.Cos(hRad + 2) + 3.8);
        double ac = VC.Aw * Math.Pow(j / 100.0, 1.0 / VC.C / VC.Z);
        double p1 = eHue * (50000.0 / 13.0) * VC.Ncb;
        double p2 = ac / VC.Nbb;
        double hSin = Math.Sin(hRad), hCos = Math.Cos(hRad);
        double gamma = 23.0 * (p2 + 0.305) * t / (23.0 * p1 + 11.0 * t * hCos + 108.0 * t * hSin);
        double a = gamma * hCos, b = gamma * hSin;
        double rA = (460.0 * p2 + 451.0 * a + 288.0 * b) / 1403.0;
        double gA = (460.0 * p2 - 891.0 * a - 261.0 * b) / 1403.0;
        double bA = (460.0 * p2 - 220.0 * a - 6300.0 * b) / 1403.0;

        double rC = InvNonlin(rA, VC.FlRoot) / VC.D[0];
        double gC = InvNonlin(gA, VC.FlRoot) / VC.D[1];
        double bC = InvNonlin(bA, VC.FlRoot) / VC.D[2];

        x = 1.86206786 * rC - 1.01125463 * gC + 0.14918677 * bC;
        y = 0.38752654 * rC + 0.62144744 * gC - 0.00897398 * bC;
        z = -0.01584150 * rC - 0.03412294 * gC + 1.04996444 * bC;
    }

    static double InvNonlin(double v, double flRoot)
    {
        double t = Math.Abs(v);
        double x = 100.0 / flRoot * Math.Pow(27.13 * t / Math.Max(400.0 - t, 1e-6), 1.0 / 0.42);
        return Math.Sign(v) * x;
    }

    // =================================================================
    // CAM16-UCS 色差 ΔE' (Luo & Li 2017) —— 与 CIEDE2000 互为印证
    // =================================================================
    public static double DeltaEu(Color c1, Color c2)
    {
        XyzFromColor(c1, out var x1, out var y1, out var z1);
        XyzFromColor(c2, out var x2, out var y2, out var z2);
        FromXyz(x1, y1, z1, out var j1, out var ch1, out var h1);
        FromXyz(x2, y2, z2, out var j2, out var ch2, out var h2);
        Ucs(j1, ch1, h1, out var l1, out var a1, out var b1);
        Ucs(j2, ch2, h2, out var l2, out var a2, out var b2);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    static void Ucs(double j, double c, double h, out double L, out double a, out double b)
    {
        double jp = 1.7 * j / (1 + 0.007 * j);
        double m = c * VC.FlRoot;
        double mp = Math.Log(1 + 0.0228 * m) / 0.0228;
        double rad = h * Math.PI / 180;
        L = jp; a = mp * Math.Cos(rad); b = mp * Math.Sin(rad);
    }

    // =================================================================
    // WCAG 2.1 相对亮度 / 对比度
    // =================================================================
    public static double RelLum(Color c)
        => 0.2126729 * SrgbToLinear(c.R / 255.0)
         + 0.7151522 * SrgbToLinear(c.G / 255.0)
         + 0.0721750 * SrgbToLinear(c.B / 255.0);

    public static double Contrast(Color a, Color b)
    {
        double la = RelLum(a), lb = RelLum(b);
        if (la < lb) { (la, lb) = (lb, la); }
        return (la + 0.05) / (lb + 0.05);
    }

    /// <summary>半透明前景按叠在背景上估算实际颜色</summary>
    public static Color Flatten(Color fg, Color bg)
    {
        if (fg.A == 255) return fg;
        double t = fg.A / 255.0;
        return Color.FromRgb((byte)(fg.R * t + bg.R * (1 - t)),
                             (byte)(fg.G * t + bg.G * (1 - t)),
                             (byte)(fg.B * t + bg.B * (1 - t)));
    }
}
