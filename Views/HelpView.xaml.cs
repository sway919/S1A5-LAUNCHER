using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerUI.Core;

namespace ServerUI.Views;

/*
 * 使用说明后台 — 左侧目录 + 右侧正文
 *   正文内容: S1A5 / ACT5 单机端 (与 一键端\使用说明.txt 不必逐字一致)。
 *
 * 正文用极简标记书写, 由 Show() 渲染成控件:
 *   "## xxx"  小节标题
 *   "-  xxx"  要点
 *   "!  xxx"  注意事项 (橙色底)
 *   ">  xxx"  代码/路径 (等宽)
 *   其它      正文段落
 *
 * 每条 _docs.Add(new Doc(...)) 都以 ")); 收尾 (verbatim 字符串 + 两层右括号)。
 */
public partial class HelpView : UserControl, MainWindow.IViewRefresh
{
    /*
     * 目录项。
     *
     * 必须是【属性】而不是字段: WPF 的 DisplayMemberPath / Binding 走的是 TypeDescriptor,
     * 只看 CLR 属性 —— 原来写成 public string Title; 这种字段, 绑定静默失败,
     * 列表里就只剩一排空白项 (找了很久: 看着像"模板没吃到 Title", 其实是字段不可绑定)。
     */
    class Doc
    {
        public string Title { get; }
        public string Sub { get; }
        public string Body { get; }
        public Doc(string t, string s, string b) { Title = t; Sub = s; Body = b; }
    }

    readonly List<Doc> _docs = new List<Doc>();
    int _idx = -1;

    /*
     * 字体:
     *   · 正文统一显式指定微软雅黑 —— 不靠继承, 免得哪天父级换了字体这里跟着变;
     *   · 代码/路径行用【字体回退列表】而不是单个 Consolas:
     *     Consolas 没有中文字形, 只写它的话中文会被逐字回退到宋体,
     *     结果一行里"拉丁是等宽、中文是宋体", 看起来像字体坏了。
     *     WPF 支持逗号分隔的候选列表, 于是拉丁走 Consolas、中文走雅黑。
     */
    static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI");
    static readonly FontFamily CodeFont = new FontFamily("Consolas, Microsoft YaHei UI");

    public HelpView()
    {
        InitializeComponent();
        Build();
        Loaded += (s, e) =>
        {
            // DisplayMemberPath 在 XAML 里设好了 (在代码里"先 ItemsSource 再 DisplayMemberPath"
            // 会让列表显示空白项)
            Lst.ItemsSource = null;
            Lst.ItemsSource = _docs;
            if (Lst.SelectedIndex < 0) Lst.SelectedIndex = 0;
        };
    }

    public void Refresh() { }

    /* 预热: 启动器起来之后趁空闲先把第一页正文渲染 + 排版一遍。
       实测第一次点开【说明】要 ~290ms (长篇文本的折行与排版), 之后再进来只要 ~19ms ——
       提前做掉这一步, 点开时就不会"卡一下"。 */
    public void Prewarm(double width, double height)
    {
        try
        {
            if (_docs.Count == 0) return;
            if (Lst.ItemsSource == null) Lst.ItemsSource = _docs;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_idx < 0) Show(_docs[0]);              // 先把正文建出来
            long msTree = sw.ElapsedMilliseconds; sw.Restart();
            long msLayout = 0, msRaster = 0;
            if (width > 100 && height > 100)
            {
                Measure(new Size(width, height));
                Arrange(new Rect(0, 0, width, height));
                UpdateLayout();
                msLayout = sw.ElapsedMilliseconds; sw.Restart();
                // 排版还不够: 首次真正画出来 (字形光栅化 + 卡片投影) 才是那 ~290ms 的大头。
                // 离屏渲一遍到 RenderTargetBitmap, 把这些缓存都喂热, 之后点开就快了。
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);
                msRaster = sw.ElapsedMilliseconds;
            }
            AppSys.Log("[启动] 说明页预热: 建树 " + msTree + "ms · 排版 " + msLayout + "ms · 光栅 " + msRaster + "ms", "gray");
        }
        catch (Exception ex) { AppSys.Log("[启动] 说明页预热失败: " + ex.Message, "warn"); }
    }

    void Lst_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = Lst.SelectedIndex;
        if (i < 0 || i == _idx) return;
        _idx = i;
        Show(_docs[i]);
    }

    void Show(Doc d)
    {
        Title.Text = d.Title;
        Sub.Text = d.Sub;
        Body.Children.Clear();
        foreach (var raw in d.Body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Body.Children.Add(new Border { Height = 8 }); continue; }

            if (line.StartsWith("## "))
            {
                Body.Children.Add(new TextBlock
                {
                    Text = line.Substring(3),
                    Style = (Style)FindResource("H3"),
                    FontFamily = UiFont,
                    Margin = new Thickness(0, 12, 0, 6)
                });
            }
            else if (line.StartsWith("- "))
            {
                // 用两列 Grid 而不是横向 StackPanel: 横向 StackPanel 给子元素的可用宽度是
                // 【无限】, 里面的 TextBlock 就永远不换行 —— 长要点会被面板直接裁掉
                // (实测就是"文字被切一半", 且只有够短的行看着正常)。
                var row = new Grid { Margin = new Thickness(6, 3, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var dot = new TextBlock
                {
                    Text = "·",
                    Style = (Style)FindResource("Txt2"),
                    FontFamily = UiFont,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                var text = new TextBlock
                {
                    Text = line.Substring(2),
                    Style = (Style)FindResource("Txt2"),
                    FontFamily = UiFont,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21
                };
                Grid.SetColumn(dot, 0);
                Grid.SetColumn(text, 1);
                row.Children.Add(dot);
                row.Children.Add(text);
                Body.Children.Add(row);
            }
            else if (line.StartsWith("! "))
            {
                var b = new Border
                {
                    Background = (Brush)FindResource("Brush.WarnSoft"),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 8, 0, 0)
                };
                b.Child = new TextBlock
                {
                    Text = line.Substring(2),
                    Style = (Style)FindResource("Txt2"),
                    FontFamily = UiFont,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21
                };
                Body.Children.Add(b);
            }
            else if (line.StartsWith("> "))
            {
                Body.Children.Add(new TextBlock
                {
                    Text = line.Substring(2),
                    Style = (Style)FindResource("Txt3"),
                    FontFamily = CodeFont,      // Consolas + 雅黑回退, 见字段说明
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(14, 3, 0, 0)
                });
            }
            else
            {
                Body.Children.Add(new TextBlock
                {
                    Text = line,
                    Style = (Style)FindResource("Txt2"),
                    FontFamily = UiFont,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }
        }
    }

    // =================================================================
    // 正文
    // =================================================================
    void Build()
    {
        _docs.Add(new Doc("怎么玩", "双击启动器，点一下就开始",
@"双击 S1a5-Launcher.exe 打开启动器，然后点右下角的【开始游戏】。剩下的事它自己会做完。

## 第一次打开
- 会先弹一次【使用协议】，要照着屏上那句话【逐字打一遍】（标点也要一样）才能点【我同意并继续】。
- 不确认就用不了 —— 这是防倒狗的门槛；确认过就不会再问了。
- 想重新看一遍协议：删掉启动器目录里的 S1a5启动器.config.json，再打开就会重新要求确认。

## 点下【开始游戏】之后
- 先把服务端开起来（服务端就是待在后台算游戏内容的那部分）。这一步不问确认，直接开。
- 服务端准备好就直接启动游戏，进到选角色的界面，开玩。

## 想用网页 GM
- 点【GM 工具】自己开 —— 点【开始游戏】不会顺手把它打开。

## 想停的时候
- 【停止服务端】只停服务端；【重启】是先停再开。
- 收工时直接关启动器窗口，它会问“要不要一起关服务端” —— 选【是】就是把服务端和网页 GM 一起关掉。
- 服务端是个单独的命令行窗口，跑起来就让它开着 —— 顺手关掉这次就玩不了了。"));

        _docs.Add(new Doc("我的账号", "默认就能玩，想换名字也行",
@"## 默认账号
- 账号和密码都是 123456789。
- 这个号已经建在存档里了，还带着 GM 资格 —— 进游戏敲 #ehdgusrlwn 就能开 GM 模式。

## 想换个名字
- 打开【设置 → 目录与环境】，把账号改成你要的名字，点【保存账号】。
- 换成存档里没有的名字，等于从零开始一个全新的号：等级、东西全都没有。
- 新号默认没有 GM 资格。想让它也能用 GM，去网页 GM 的【GM 名单】把它加进去。"));

        _docs.Add(new Doc("网页 GM 能干什么", "一个本机网页，改东西都在这",
@"网页 GM 是个跑在你自己电脑上的小网页，地址是：
> http://127.0.0.1:5052
点【GM 工具】打开它（启动器不会自己开）。

## 能改什么
- 数值：等级、经验、SP、金币、点券、疲劳。
- 发东西：装备、物品、时装。物品有 25,741 件，都带游戏里的图标，可以按品质、等级、部位筛。
- 一键紫装：按你当前的职业发一整套紫色装备，发之前能先看一眼。
- 改一件装备的强化、耐久、附魔；把背包、穿戴、金库清空。
- 转职；改技能槽位的等级。

## 任务
- 看正在做的任务，每个材料缺多少一目了然。
- 补发材料、标记成可以交、标成已完成、直接完成，都能做。

## 其它几页
- 【GM 名单】：加、删有 GM 资格的账号。
- 【命令簿】：游戏里能用的命令，在这能一键复制。
- 【环境】：换了 PVF 之后，在这点一下重新读一遍（PVF 就是游戏的数据文件，物品、技能这些内容都从它来）。

## 启动器自己的【存档】页
- 备份、还原、删除、导入存档，还有“重置游戏”清档重来。这一页在启动器左边，不在网页里。"));

        _docs.Add(new Doc("改东西的规矩", "服务端开着的时候改不了，先停一下",
@"这条最容易踩坑，记住一句话：服务端开着的时候，网页 GM 只能看，不能改。

## 想改东西，按这个顺序
- 先点【停止服务端】。
- 再去网页 GM 里改。
- 改完点【开始游戏】把服务端开回来。
- 回游戏重新选一次角色，改动才会显示出来。

## 只是看看不用停
- 查背包、看任务、看 GM 名单这些随时都能做，服务端开着也不影响。

## 改之前会自动备份
- 每次动手改之前，它先把整个存档备份一份：
> 一键端\data\webgm-backups\
- 最多留 30 份，改坏了还能翻回去。

! 服务端开着的时候，存档文件正被它占着，网页 GM 这时候写不进去，硬写就出错。所以“能改”和“服务端开着”只能二选一。"));

        _docs.Add(new Doc("游戏里的 GM 命令", "聊天框敲一句话就能用",
@"## 怎么开
- 进游戏以后，在聊天框里敲 #ehdgusrlwn，GM 模式就打开了；再敲一次就关。

## 几个常用的
> #levelto 60        直接把等级改成 60（填 1 到 60 之间的数）
> #money 10000000    把金币改成这个数
> #add item 3171 10  给自己 10 个 3171 号物品
> #grow 1 3          转职（第一个数固定填 1，第二个是职业编号 0 到 5）

## 想用别的命令
- 一共 30 条，都写在：
> 一键端\GM命令.txt
- 网页 GM 的【命令簿】页里也能看到，可以一键复制。

! 网页 GM 的技能页只能改槽位等级。想完整学技能或者洗点，进游戏用命令做。"));

        _docs.Add(new Doc("存档与备份", "存档是三个文件，别只拷一个",
@"你的游戏进度（等级、角色、背包这些）都存在一个文件里：
> 一键端\data\s1a5.db

## 跑起来的时候会多两个文件
- 服务端开着时，存档旁边会多出 s1a5.db-wal 和 s1a5.db-shm 两个小文件。
- 这几个是配套的，缺一个都不行。

! 要拷贝存档，先把服务端停了，再把这三个文件一起拷走。只拷主文件、或者边开边拷，拷出来的是坏的，下次可能直接打不开。

## 存档页能做什么
- 【存档】页可以备份、还原、删除、导入。
- 里面还有个“重置游戏”，会把存档清空、从头再来（动手前会让你确认）。"));

        _docs.Add(new Doc("卡住 / 出问题了", "照着这几条对一下",
@"## 服务端起不来
- 先看看是不是已经有一个服务端窗口开着 —— 开了两个会撞在一起。
- 打开【运行日志】，看看最后几行报了什么。

## 网页 GM 打不开
- 点一下【GM 工具】，把它重新打开。

## 怪怎么打都打不死
- 说明游戏的补丁文件 A5.dll 丢了。点【开始游戏】时它会提醒你。

## 游戏里点了没反应
- 看一眼服务端那个窗口还在不在，关掉的话游戏就断了。"));
    }
}
