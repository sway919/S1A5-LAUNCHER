using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ServerUI.Views;

/// <summary>
/// 启动落地窗 —— 见 SplashWindow.xaml 顶部注释。
/// 不引用任何主题字典, 只按当前明暗主题写死两种配色, 保证宿主一起来就能显示。
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        Core.AppSys.BootMark("落地窗: 构造开始");
        InitializeComponent();
        Core.AppSys.BootMark("落地窗: XAML 解析完成");

        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri("pack://application:,,,/icon128.png", UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            Brand.Source = bi;
        }
        catch { }

        // 明暗跟随设置 (读的是启动器自己的配置文件, 不走主题字典)
        try
        {
            bool dark = Core.AppSys.Settings.UiTheme != "light";
            Background = Brush(dark ? "#12131A" : "#F5F6FA");
            TitleText.Foreground = Brush(dark ? "#F2F3F7" : "#1B1C22");
            SubText.Foreground = Brush(dark ? "#9AA0AE" : "#5C6270");
        }
        catch { }

        // 独立动画 (旋转), 被占住的 UI 线程不影响它
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        { RepeatBehavior = RepeatBehavior.Forever };
        SpinRt.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
