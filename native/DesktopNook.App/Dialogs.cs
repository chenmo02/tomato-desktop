using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNook.Core;

namespace DesktopNook.App;

// 弹窗与主窗口共用同一批画刷实例，深色/强调色切换时自动同步。
internal static class DialogPalette
{
    public static SolidColorBrush Get(string key) => (SolidColorBrush)Application.Current.FindResource(key);
}

internal sealed class TextDialog : Window
{
    private readonly TextBox _input;
    private readonly TextBlock _counter;
    private readonly Button _accept;
    public string Value => _input.Text.Trim();
    private static Style Look(string key)=>(Style)Application.Current.FindResource(key);
    private static SolidColorBrush Ink(byte r,byte g,byte b)=>new(Color.FromRgb(r,g,b));
    public TextDialog(string title,string label,string initial)
    {
        // 不再用 1.4 倍 LayoutTransform 放大整个弹窗，直接用与主面板一致的系统字号。
        Title=title;Width=380;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        FontSize=13;UseLayoutRounding=true;SnapsToDevicePixels=true;

        // 内容区：字段标签 → WinUI 输入框 → 说明与字符计数 → 次要/主操作按钮。
        var body=new StackPanel{Margin=new Thickness(24,10,24,22)};
        body.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=DialogPalette.Get("WinTextSecondary"),Margin=new Thickness(0,0,0,8)});
        _input=new TextBox{Text=initial,MaxLength=40,Tag="请输入"+label};
        _input.TextChanged+=(_,_)=>UpdateState();
        body.Children.Add(_input);
        var meta=new Grid{Margin=new Thickness(0,7,0,0)};
        meta.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        meta.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        meta.Children.Add(new TextBlock{Text="最多 40 个字符",FontSize=11,Foreground=DialogPalette.Get("WinTextTertiary")});
        _counter=new TextBlock{FontSize=11,Foreground=DialogPalette.Get("WinTextTertiary"),HorizontalAlignment=HorizontalAlignment.Right};
        Grid.SetColumn(_counter,1);meta.Children.Add(_counter);
        body.Children.Add(meta);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
        var cancel=new Button{Content="取消",IsCancel=true,Style=Look("WinSecondaryButton"),Margin=new Thickness(0,0,8,0)};
        _accept=new Button{Content="保存",IsDefault=true,Style=Look("WinPrimaryButton")};
        _accept.Click+=(_,_)=>{if(Value.Length>0)DialogResult=true;};
        buttons.Children.Add(cancel);buttons.Children.Add(_accept);body.Children.Add(buttons);
        Content=body;UiDialogs.Chrome(this);
        Loaded+=(_,_)=>{_input.Focus();_input.SelectAll();UpdateState();};
    }
    // 空值时直接禁用保存，不再让用户点了没反应；计数随输入实时更新。
    private void UpdateState()
    {
        _counter.Text=_input.Text.Length+" / 40";
        _accept.IsEnabled=Value.Length>0;
    }
}

internal sealed record DesktopEntry(string Path,string Name,string Source,bool Recycle);
internal sealed class CollectWindow : Window
{
    private readonly List<CheckBox> _checks=[];private readonly ComboBox _categories;
    private readonly TextBlock _count;private readonly Border _countBadge;private readonly Button _accept;
    public string CategoryId => (string)(_categories.SelectedValue??"uncategorized");
    public List<DesktopEntry> Selection => _checks.Where(c=>c.IsChecked==true).Select(c=>(DesktopEntry)c.Tag).ToList();
    private static Style Look(string key)=>(Style)Application.Current.FindResource(key);
    private static SolidColorBrush Brush(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));
    private void UpdateCount()
    {
        int count=Selection.Count;
        _count.Text="已选 "+count+" 项";
        _count.Foreground=count>0?DialogPalette.Get("WinText"):DialogPalette.Get("WinTextSecondary");
        _countBadge.Background=count>0?DialogPalette.Get("WinHover"):DialogPalette.Get("WinStrokeSoft");
        _accept.IsEnabled=count>0;
    }
    public CollectWindow(NookStore store,string selectedCategory)
    {
        Title="喂给番茄";Width=470.4;Height=515.2;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;Background=DialogPalette.Get("GlassFill");FontSize=10;
        UseLayoutRounding=true;SnapsToDevicePixels=true;
        // 只在本弹窗内替换滚动条样式，主窗口与其他对话框仍用 App.xaml 的全局样式
        Resources.Add(typeof(ScrollBar),Look("CollectScrollBar"));
        _accept=new Button{Content="喂给番茄",IsDefault=true,IsEnabled=false,MinWidth=108,Style=Look("CollectPrimaryButton")};
        _count=new TextBlock{Text="已选 0 项",FontSize=9.5,Foreground=DialogPalette.Get("WinTextSecondary"),VerticalAlignment=VerticalAlignment.Center};
        _countBadge=new Border{Background=DialogPalette.Get("WinStrokeSoft"),CornerRadius=new CornerRadius(5),Padding=new Thickness(9,3,9,3),Child=_count,VerticalAlignment=VerticalAlignment.Center};

        var root=new Grid{Margin=new Thickness(18,2,18,10),LayoutTransform=new ScaleTransform(1.4,1.4)};
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});

        var hintRow=new Grid{Margin=new Thickness(0,0,0,7)};
        hintRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});hintRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        hintRow.Children.Add(new TextBlock{Text="选择桌面图标，也可以直接拖给番茄。",FontSize=10.5,Foreground=DialogPalette.Get("WinTextSecondary"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});
        Grid.SetColumn(_countBadge,1);hintRow.Children.Add(_countBadge);root.Children.Add(hintRow);

        var list=new StackPanel();
        var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false,Padding=new Thickness(0,0,2,0),Style=Look("CollectScrollViewer")};
        scroll.Resources.Add(typeof(ScrollBar),Look("CollectScrollBar"));
        var listCard=new Border{Background=DialogPalette.Get("WinCardAlt"),BorderBrush=DialogPalette.Get("WinCardAlt"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(5),Child=scroll};
        Grid.SetRow(listCard,1);root.Children.Add(listCard);
        var fade=new Border{Height=22,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(6,0,6,6),CornerRadius=new CornerRadius(0,0,6,6),IsHitTestVisible=false,Visibility=Visibility.Collapsed,
            Background=new LinearGradientBrush(new GradientStopCollection{new(Colors.Transparent,0),new(Color.FromArgb(0xCC,255,255,255),1)},90)};
        Grid.SetRow(fade,1);root.Children.Add(fade);
        Action refreshFade=()=>fade.Visibility=scroll.ScrollableHeight>0?Visibility.Visible:Visibility.Collapsed;
        scroll.Loaded+=(_,_)=>refreshFade();scroll.SizeChanged+=(_,_)=>refreshFade();

        var entries=new List<DesktopEntry>();
        foreach(var desktop in store.DesktopRoots.Where(Directory.Exists))
        {
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(desktop))
                {
                    if((File.GetAttributes(path)&FileAttributes.Hidden)!=0)continue;
                    if(store.State.Items.Any(i=>string.Equals(i.OriginalPath,path,StringComparison.OrdinalIgnoreCase)))continue;
                    entries.Add(new(path,Directory.Exists(path)?System.IO.Path.GetFileName(path):new[]{".lnk",".url"}.Contains(System.IO.Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)?System.IO.Path.GetFileNameWithoutExtension(path):System.IO.Path.GetFileName(path),desktop,false));
                }
            }
            catch(UnauthorizedAccessException) {list.Children.Add(new TextBlock{Text="部分桌面目录无读取权限",FontSize=9.5,Foreground=Brush("#C08B3E"),Margin=new Thickness(8,6,8,6)});}
        }
        if(!store.State.Items.Any(i=>i.Kind=="recycle-bin")&&App.Recycle.IsVisible)entries.Add(new("","回收站","系统图标",true));
        foreach(var entry in entries.OrderBy(i=>i.Name,StringComparer.CurrentCulture))
        {
            var icon=new ShellIcon{IconPath=entry.Path,IsRecycleBin=entry.Recycle,Width=20,Height=20,Margin=new Thickness(0,0,9,0),VerticalAlignment=VerticalAlignment.Center};
            var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
            text.Children.Add(new TextBlock{Text=entry.Name,FontSize=10,Foreground=DialogPalette.Get("WinText"),TextTrimming=TextTrimming.CharacterEllipsis});
            text.Children.Add(new TextBlock{Text=entry.Source,FontSize=8.5,Foreground=DialogPalette.Get("WinTextSecondary"),Margin=new Thickness(0,1,0,0),TextTrimming=TextTrimming.CharacterEllipsis});
            var content=new Grid{HorizontalAlignment=HorizontalAlignment.Stretch};
            content.ColumnDefinitions.Add(new(){Width=GridLength.Auto});content.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            content.Children.Add(icon);Grid.SetColumn(text,1);content.Children.Add(text);
            var check=new CheckBox{Content=content,Tag=entry,Margin=new Thickness(0,1,0,1),VerticalContentAlignment=VerticalAlignment.Center,ToolTip=entry.Recycle?"系统回收站，不会清空内容":entry.Path,Style=Look("CollectCheck")};
            check.Checked+=(_,_)=>UpdateCount();check.Unchecked+=(_,_)=>UpdateCount();
            _checks.Add(check);list.Children.Add(check);
        }
        if(entries.Count==0)
        {
            var empty=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,24,0,24)};
            var glyph=new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")),Width=38,Height=38,Opacity=.75};RenderOptions.SetBitmapScalingMode(glyph,BitmapScalingMode.HighQuality);
            empty.Children.Add(glyph);
            empty.Children.Add(new TextBlock{Text="没有待收纳的桌面图标",FontSize=10.5,Foreground=DialogPalette.Get("WinTextSecondary"),HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,10,0,0)});
            list.Children.Add(empty);
        }

        var categoryPanel=new StackPanel{Margin=new Thickness(0,10,0,0)};
        categoryPanel.Children.Add(new TextBlock{Text="放入分类",FontSize=10,FontWeight=FontWeights.SemiBold,Foreground=DialogPalette.Get("WinText"),Margin=new Thickness(2,0,0,5)});
        _categories=new ComboBox{ItemsSource=store.State.Categories,SelectedValuePath="Id",SelectedValue=selectedCategory,ItemTemplate=(DataTemplate)Application.Current.FindResource("CollectCategoryTemplate"),Style=Look("CollectCombo")};
        TextSearch.SetTextPath(_categories,"Name");
        categoryPanel.Children.Add(_categories);Grid.SetRow(categoryPanel,2);root.Children.Add(categoryPanel);

        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(new Button{Content="取消",IsCancel=true,MinWidth=76,Margin=new Thickness(0,0,8,0),Style=Look("CollectSecondaryButton")});
        _accept.Click+=(_,_)=>{if(Selection.Count>0)DialogResult=true;};buttons.Children.Add(_accept);
        var footer=new Border{BorderThickness=new Thickness(0,1,0,0),BorderBrush=DialogPalette.Get("WinCardAlt"),Padding=new Thickness(0,10,0,0),Margin=new Thickness(0,10,0,0),Child=buttons};
        Grid.SetRow(footer,3);root.Children.Add(footer);
        Content=root;Height+=88;UiDialogs.Chrome(this,rich:true,subtitle:"把桌面图标，喂给番茄");
        UpdateCount();
    }
}
