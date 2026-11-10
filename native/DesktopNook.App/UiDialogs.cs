using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;
internal static class UiDialogs
{
    // rich=false 时保持原有外观，供 TextDialog / UiDialogs.Show 使用；rich=true 是「喂给番茄」弹窗的增强外壳。
    public static void Chrome(Window window,bool rich=false,string? subtitle=null)
    {
        var body=(UIElement)window.Content;window.Content=null;window.WindowStyle=WindowStyle.None;window.AllowsTransparency=true;window.Background=Brushes.Transparent;
        var dock=new DockPanel();var heading=new Grid{Margin=rich?new Thickness(20,13,16,0):new Thickness(20,16,14,0)};heading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var title=new TextBlock{Text=window.Title,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=(Brush)Application.Current.FindResource("WinText"),VerticalAlignment=VerticalAlignment.Center};
        if(rich)
        {
            var icon=new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")),Width=26,Height=26,Margin=new Thickness(0,0,10,0)};
            RenderOptions.SetBitmapScalingMode(icon,BitmapScalingMode.HighQuality);
            var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center};texts.Children.Add(title);
            if(subtitle is not null)texts.Children.Add(new TextBlock{Text=subtitle,FontSize=9.5,Foreground=(Brush)Application.Current.FindResource("WinTextSecondary"),Margin=new Thickness(0,2,0,0)});
            var titleRow=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            titleRow.Children.Add(icon);titleRow.Children.Add(texts);heading.Children.Add(titleRow);
        }
        else heading.Children.Add(title);
        // 两类弹窗共用同一个 28×28 圆角关闭按钮，避免标题栏出现两种尺寸。
        var close=new Button{Content="×",FontSize=17,Foreground=(Brush)Application.Current.FindResource("WinTextSecondary"),Style=(Style)Application.Current.FindResource("DialogCloseButton")};
        close.Click+=(_,_)=>window.DialogResult=false;Grid.SetColumn(close,1);heading.Children.Add(close);
        heading.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is not Control){try{window.DragMove();}catch(Exception){}}};
        DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);
        if(rich)
        {
            var divider=new Border{Height=1,Background=(Brush)Application.Current.FindResource("GlassStrokeSoft"),Margin=new Thickness(18,9,18,0)};
            DockPanel.SetDock(divider,Dock.Top);dock.Children.Add(divider);
        }
        dock.Children.Add(body);
        // 弹窗外壳与主面板共用玻璃渐变和半透明描边，浮在主窗口上方时像一片磨砂玻璃。
        var glassFill=(Brush)Application.Current.FindResource("GlassFill");
        var glassStroke=(Brush)Application.Current.FindResource("GlassStroke");
        var shell=new Border{CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),BorderBrush=glassStroke,Background=glassFill,Child=dock};
        if(rich)
        {
            window.Width+=20;window.Height+=20;
            shell.Margin=new Thickness(10);shell.BorderBrush=glassStroke;
            shell.Effect=new DropShadowEffect{BlurRadius=22,ShadowDepth=3,Opacity=.16,Color=Colors.Black};
        }
        window.Content=shell;
    }
    public static MessageBoxResult Show(Window owner,string text,string title,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None)
    {
        var window=new Window{Title=title,Owner=owner,Width=470,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,ResizeMode=ResizeMode.NoResize};
        var body=new StackPanel{Margin=new Thickness(22,16,22,20)};body.Children.Add(new ScrollViewer{MaxHeight=320,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=13,LineHeight=23,Foreground=(Brush)Application.Current.FindResource("WinText")}});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0)};
        if(buttons!=MessageBoxButton.OK){var cancel=new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0),MinWidth=80,Style=(Style)Application.Current.FindResource("WinSecondaryButton")};cancel.Click+=(_,_)=>window.DialogResult=false;actions.Children.Add(cancel);}
        var confirm=new Button{Content="确定",IsDefault=true,MinWidth=76,Style=(Style)Application.Current.FindResource("PrimaryButton")};confirm.Click+=(_,_)=>window.DialogResult=true;actions.Children.Add(confirm);body.Children.Add(actions);window.Content=body;Chrome(window);
        return window.ShowDialog()==true?MessageBoxResult.OK:MessageBoxResult.Cancel;
    }
}
