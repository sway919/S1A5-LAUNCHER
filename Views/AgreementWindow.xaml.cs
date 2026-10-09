using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ServerUI.Views;

/*
 * 首次运行 · 使用协议 (见 AgreementWindow.xaml 顶部注释)
 *
 * 规则: 玩家必须逐字输入确认短语 (RequiredPhrase), 完全一致才能点【我同意并继续】。
 * 任何"关闭窗口 / Esc / 点不同意"的路径都返回 false, 由 App 结束进程。
 *
 * 这两个常量由项目方指定: 改防倒狗那句话 / 改协议正文, 只动这里。
 */
public partial class AgreementWindow : Window
{
    /// <summary>必须逐字输入的确认短语 (与参考项目 60US starter 同一句, 可按需改)</summary>
    public const string RequiredPhrase = "确认协议，我不是蠢蛋，我没有倒卖和购买，我会退款并举报卖家";

    /// <summary>协议正文 (由项目方指定, 一字不改)</summary>
    public const string AgreementText =
        "本项目仅供个人学习与研究使用，严禁任何形式的商业用途与倒卖。游戏版权归原开发商所有，请于下载后 24 小时内删除。因使用本项目产生的一切后果，由使用者自行承担。\n\n" +
        "本项目基于公开的开源成果整理，所有正式公告、更新内容与使用说明，以随包文档与启动器里的说明为准。\n\n" +
        "请勿利用本项目从事破坏正常服务、传播恶意程序、侵犯他人计算机系统或其他违反所在地法律法规的行为。\n\n" +
        "如果遇到某些平台倒卖，请默默退款并点击举报。";

    public bool Accepted { get; private set; }

    public AgreementWindow()
    {
        InitializeComponent();
        BodyText.Text = AgreementText;
        PhraseText.Text = RequiredPhrase;
        Loaded += (s, e) => Inp.Focus();
    }

    void Win_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    void Inp_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var got = (Inp.Text ?? "").Trim();
        bool ok = string.Equals(got, RequiredPhrase, StringComparison.Ordinal);
        BtOk.IsEnabled = ok;

        if (ok)
        {
            Hint.Text = "✓ 完全一致，可以继续了。";
            Hint.Foreground = Brush("Brush.AccentText");
        }
        else if (got.Length == 0)
        {
            Hint.Text = "还没输入。";
            Hint.Foreground = Brush("Brush.Text3");
        }
        else
        {
            // 给"差在哪"的提示: 少字/多字最容易出现在标点上
            var diff = got.Length - RequiredPhrase.Length;
            Hint.Text = "与要求的那句不一致"
                      + (diff == 0 ? "（长度一样，检查一下标点，逗号是中文“，”）"
                                   : (diff < 0 ? "（少了 " + (-diff) + " 个字符）"
                                               : "（多了 " + diff + " 个字符）"))
                      + "。可以整句复制粘贴。";
            Hint.Foreground = Brush("Brush.Warn");
        }
    }

    void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!BtOk.IsEnabled) return;
        Accepted = true;
        Close();
    }

    void Decline_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Accepted = false; Close(); }
        base.OnKeyDown(e);
    }

    Brush Brush(string key)
        => TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.White;

    /// <summary>显示协议窗; 返回玩家是否同意</summary>
    public static bool Ask()
    {
        var w = new AgreementWindow();
        w.ShowDialog();
        return w.Accepted;
    }
}
