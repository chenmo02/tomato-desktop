using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

// 按钮前景色自检（--verify-button-contrast <output>）：逐一切换皮肤 / 主题，
// 确认次要按钮的文字始终跟随 WinText，深色下是浅色而不是系统默认的黑字。
// 纯资源探测，不创建主窗口、不读取或改动真实桌面数据。
internal static class ButtonContrastVerification
{
    private static string Hex(Color? c)=>c is Color v?$"#{v.A:X2}{v.R:X2}{v.G:X2}{v.B:X2}":"null";

    // 把深色玻璃皮肤下的真实按钮渲染成 PNG，作为「白字深底」的直观留档；
    // 渲染失败只影响留档，不改变颜色断言的结果。
    private static void SavePreview(string output,Color card)
    {
        try
        {
            var panel=new StackPanel{Orientation=Orientation.Horizontal,Background=new SolidColorBrush(card),Width=540,Height=88};
            var style=(Style)Application.Current.FindResource("WinSecondaryButton");
            foreach(var (label,enabled) in new[]{("立即扫描",true),("导出…",true),("无可撤销",false)})
            {
                var button=new Button{Style=style,Content=label,MinWidth=132,Height=32,Margin=new Thickness(12,0,0,0),IsEnabled=enabled};
                panel.Children.Add(button);
            }
            panel.Measure(new Size(540,88));panel.Arrange(new Rect(0,0,540,88));
            foreach(var button in panel.Children.OfType<Button>())button.ApplyTemplate();
            panel.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1080,176,192,192,PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(output,"dark-secondary-buttons.png"));png.Save(file);
        }
        catch{/* 留档失败不影响自检结论 */}
    }

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks=new List<string>();
        try
        {
            ThemeService.EnsureMutableResources();
            var cases=new[]
            {
                (Skin:SkinService.Native,Theme:"light",Label:"原生·浅色"),
                (Skin:SkinService.Native,Theme:"dark",Label:"原生·深色"),
                (Skin:SkinService.Glass,Theme:"light",Label:"玻璃·浅色"),
                (Skin:SkinService.Glass,Theme:"dark",Label:"玻璃·深色")
            };
            foreach(var c in cases)
            {
                ThemeService.Apply(new AppSettings{Skin=c.Skin,Theme=c.Theme});
                var text=((SolidColorBrush)Application.Current.Resources["WinText"]).Color;
                var card=((SolidColorBrush)Application.Current.Resources["WinCardFill"]).Color;
                // 次要按钮与设置页下拉选择器共用同一套外观，两者都必须跟随主题文字色。
                foreach(var styleKey in new[]{"WinSecondaryButton","WinSettingsCombo"})
                {
                    var style=(Style)Application.Current.FindResource(styleKey);
                    var probe=new Button{Style=style,Content="导出…"};
                    var fg=(probe.Foreground as SolidColorBrush)?.Color;
                    var brightness=fg is Color v?(v.R+v.G+v.B)/3:0;
                    bool dark=c.Theme=="dark";
                    bool ok=fg==text&&(!dark||brightness>180);
                    checks.Add($"{c.Label} / {styleKey}: WinText={Hex(text)} 按钮前景={Hex(fg)} 卡片底={Hex(card)} 亮度={brightness} → {(ok?"OK":"FAIL")}");
                    if(!ok)throw new Exception(c.Label+" "+styleKey+" 文字未跟随主题色："+Hex(fg));
                }
                if(c.Skin==SkinService.Glass&&c.Theme=="dark")SavePreview(output,card);
            }
            File.WriteAllText(Path.Combine(output,"button-contrast.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure="",checks},new JsonSerializerOptions{WriteIndented=true}));
            var stale=Path.Combine(output,"failure.txt");if(File.Exists(stale))File.Delete(stale);
            return 0;
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(output,"button-contrast.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure=ex.Message,checks},new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(output,"failure.txt"),ex.Message);
            return 1;
        }
    }
}
