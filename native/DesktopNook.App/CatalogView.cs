using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNook.App;

// 「全部图标」页面：浏览开始菜单 / 系统应用 / 桌面快捷方式，勾选后复制进番茄桌面。
// 源文件（开始菜单、系统应用）永远不会被移动或删除。
// 打开时立即显示页面；系统应用枚举放在后台 STA 线程，图标只对滚动到视口内的卡片解码。
public partial class MainWindow
{
    private readonly List<CatalogCard> _catalogCards=[];
    private readonly HashSet<CatalogEntry> _catalogSelection=[];
    private List<CatalogEntry>? _catalogEntries;
    private bool _catalogLoaded;
    private bool _catalogLoading;
    private bool _catalogVisible;
    private DispatcherTimer? _catalogIconTimer;
    private sealed class CatalogCard
    {
        public required CatalogEntry Entry {get;init;}
        public required Border Card {get;init;}
        public required Border Badge {get;init;}
        public required TextBlock BadgeText {get;init;}
        public required ShellIcon Icon {get;init;}
        public required bool Collected {get;init;}
    }

    private void OnCatalogToggle(object sender,RoutedEventArgs e)
    {
        if(_catalogVisible)ShowIconsView();else ShowCatalogView();
    }
    internal void ShowIconsView()
    {
        _catalogVisible=false;_settingsVisible=false;
        _catalogIconTimer?.Stop();
        CatalogSurface.Visibility=Visibility.Collapsed;
        SettingsSurface.Visibility=Visibility.Collapsed;
        ContentToolbar.Visibility=Visibility.Visible;
        DropSurface.Visibility=Visibility.Visible;
        Render();
    }
    internal void ShowCatalogView()
    {
        _catalogVisible=true;_settingsVisible=false;
        DropSurface.Visibility=Visibility.Collapsed;
        ContentToolbar.Visibility=Visibility.Collapsed;
        CatalogSurface.Visibility=Visibility.Visible;
        SettingsSurface.Visibility=Visibility.Collapsed;
        CatalogCategory.ItemsSource=Store.State.Categories;
        CatalogCategory.SelectedValue=_activeCategory??"uncategorized";
        UpdateTabSelection();
        if(!_catalogLoaded)
        {
            // 先呈现页面骨架，COM 枚举不占用 UI 线程；第二次打开直接用缓存，瞬间完成。
            if(_catalogEntries is null)StartCatalogLoad();
            else {BuildCatalog();_catalogLoaded=true;}
        }
        Summary.Text=_catalogLoading?"全部图标 · 读取中":"全部图标 · "+_catalogCards.Count+" 个可添加";
        ApplyCatalogFilter();
        QueueVisibleIcons();
    }
    private void StartCatalogLoad()
    {
        if(_catalogLoading)return;
        _catalogLoading=true;
        _catalogCards.Clear();_catalogSelection.Clear();CatalogGroups.Children.Clear();
        CatalogGroups.Children.Add(new TextBlock{Text="正在读取应用列表…",FontSize=12,Foreground=Res("WinTextSecondary"),Margin=new Thickness(8,10,0,0)});
        UpdateCatalogCount();
        var store=Store;
        var worker=new Thread(()=>
        {
            List<CatalogEntry> entries;
            try{entries=AppCatalog.Load(store);}catch{entries=[];}
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>
            {
                _catalogLoading=false;
                _catalogEntries=entries;
                if(!_catalogVisible||_catalogLoaded)return;
                BuildCatalog();_catalogLoaded=true;
                Summary.Text="全部图标 · "+_catalogCards.Count+" 个可添加";
                ApplyCatalogFilter();
                QueueVisibleIcons();
            }));
        }){IsBackground=true,Name="DesktopNook catalog scan"};
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
    private void BuildCatalog()
    {
        _catalogCards.Clear();_catalogSelection.Clear();CatalogGroups.Children.Clear();
        // 开关已从底栏迁到「设置 → 行为」，偏好随 AppSettings 一起持久化与导入导出。
        bool showSystem=Settings.ShowSystemItems;
        var entries=(_catalogEntries??[]).Where(e=>showSystem||!e.Hidden).ToList();
        var collected=new HashSet<string>(Store.State.Items.Select(i=>i.OriginalPath),StringComparer.OrdinalIgnoreCase);
        var groupIndex=0;
        foreach(var group in entries.GroupBy(e=>e.Group))
        {
            var items=group.ToList();
            // 分组头：主色标题 + 细分隔线 + 计数徽标，组间留 20px 呼吸。
            // 7 列透明磁贴：默认无底色，避免与外层白色卡片叠色。
            var panel=new WrapPanel{ItemWidth=96,ItemHeight=100,Orientation=Orientation.Horizontal};
            var header=new Grid{Margin=new Thickness(2,groupIndex==0?2:20,2,10),Tag=panel};
            header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var title=new TextBlock{Text=group.Key,FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=Res("WinText"),VerticalAlignment=VerticalAlignment.Center};
            var line=new Border{Height=1,Background=Res("WinStrokeSoft"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,10,0)};
            var pill=new Border{Background=Res("WinAccentSoft"),CornerRadius=new CornerRadius(9),Padding=new Thickness(9,2,9,2),VerticalAlignment=VerticalAlignment.Center,
                Child=new TextBlock{Text=items.Count+" 个",FontSize=10,Foreground=Res("WinTextSecondary")}};
            Grid.SetColumn(title,0);Grid.SetColumn(line,1);Grid.SetColumn(pill,2);
            header.Children.Add(title);header.Children.Add(line);header.Children.Add(pill);
            foreach(var entry in items)
            {
                bool isCollected=collected.Contains(entry.Source);
                var (card,badge,badgeText,icon)=BuildCard(entry,isCollected);
                var record=new CatalogCard{Entry=entry,Card=card,Badge=badge,BadgeText=badgeText,Icon=icon,Collected=isCollected};
                card.Tag=record;
                card.MouseLeftButtonUp+=(_,_)=>ToggleCard(record);
                card.MouseEnter+=(_,_)=>HoverCard(record,true);
                card.MouseLeave+=(_,_)=>HoverCard(record,false);
                _catalogCards.Add(record);panel.Children.Add(card);
            }
            CatalogGroups.Children.Add(header);CatalogGroups.Children.Add(panel);
            groupIndex++;
        }
        UpdateCatalogCount();
    }
    private (Border,Border,TextBlock,ShellIcon) BuildCard(CatalogEntry entry,bool collected)
    {
        var icon=new ShellIcon{IconPath=entry.IconPath,Fast=true,FixedPixels=48,Deferred=true,Width=36,Height=36,Margin=new Thickness(0,4,0,8)};
        var name=new TextBlock{Text=entry.Name,FontSize=11,Foreground=Res(collected?"WinTextSecondary":"WinText"),TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=32,MaxWidth=84};
        var stack=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(4,6,4,0)};
        stack.Children.Add(icon);stack.Children.Add(name);
        // 状态标记固定在右上角：「+」只作淡提示，选中后变实心对勾。
        var badgeText=new TextBlock{Text=collected?"✓":"+",FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=Res("WinTextSecondary"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var badge=new Border{Width=18,Height=18,CornerRadius=new CornerRadius(9),Background=Brushes.Transparent,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,4,4,0),Visibility=collected?Visibility.Visible:Visibility.Collapsed,Child=badgeText};
        // 与主界面一致：磁贴默认透明，悬停 / 选中才浮出底色，避免与外层白卡叠色。
        var card=new Border{Width=88,Height=92,Background=Brushes.Transparent,BorderBrush=Brushes.Transparent,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Margin=new Thickness(4),Cursor=collected?Cursors.Arrow:Cursors.Hand,ToolTip=entry.Name+" · "+entry.Group+(collected?"（已收纳）":"")};
        var grid=new Grid();grid.Children.Add(stack);grid.Children.Add(badge);card.Child=grid;
        return (card,badge,badgeText,icon);
    }
    private void ToggleCard(CatalogCard record)
    {
        if(record.Collected)return;
        if(_catalogSelection.Add(record.Entry))PaintCard(record,true);
        else {_catalogSelection.Remove(record.Entry);PaintCard(record,false);}
        UpdateCatalogCount();
    }
    private void HoverCard(CatalogCard r,bool hover)
    {
        if(r.Collected||_catalogSelection.Contains(r.Entry))return;
        r.Card.Background=hover?Brush("#14000000"):Brushes.Transparent;
        r.Badge.Visibility=hover?Visibility.Visible:Visibility.Collapsed;
    }
    private static void PaintCard(CatalogCard r,bool selected)
    {
        r.Card.Background=selected?Res("WinAccentSoft"):Brushes.Transparent;
        r.Card.BorderBrush=selected?Res("WinAccent"):Brushes.Transparent;
        r.Badge.Background=selected?Res("WinAccent"):Brushes.Transparent;
        r.Badge.Visibility=selected?Visibility.Visible:Visibility.Collapsed;
        r.BadgeText.Text=selected?"✓":"+";
        r.BadgeText.Foreground=selected?Brushes.White:Res("WinTextSecondary");
    }
    private void UpdateCatalogCount()
    {
        int count=_catalogSelection.Count;
        CatalogCount.Text="已选 "+count+" 项";
        CatalogCount.Foreground=Res(count>0?"WinText":"WinTextSecondary");
        CatalogAccept.IsEnabled=count>0;
    }
    private void OnCatalogSearch(object sender,TextChangedEventArgs e)=>ApplyCatalogFilter();
    private void ApplyCatalogFilter()
    {
        var query=CatalogSearch.Text.Trim();
        foreach(var card in _catalogCards)
            card.Card.Visibility=query.Length==0||card.Entry.Name.Contains(query,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;
        foreach(var header in CatalogGroups.Children.OfType<Grid>())
            if(header.Tag is WrapPanel panel)
                header.Visibility=panel.Children.OfType<Border>().Any(b=>b.Visibility==Visibility.Visible)?Visibility.Visible:Visibility.Collapsed;
        QueueVisibleIcons();
    }
    // 「设置 → 行为 → 显示系统项目」变化（或导入配置包）后调用：
    // 页面可见时立即重建列表并尽量保留已选卡片；不可见时标记过期，下次打开按新偏好重建。
    internal void ApplySystemItemsPreference()
    {
        if(_catalogEntries is null||!_catalogVisible){_catalogLoaded=false;return;}
        var selected=_catalogSelection.ToList();
        BuildCatalog();
        foreach(var entry in selected)
        {
            var card=_catalogCards.FirstOrDefault(c=>c.Entry==entry&&!c.Collected);
            if(card is not null){_catalogSelection.Add(card.Entry);PaintCard(card,true);}
        }
        UpdateCatalogCount();
        Summary.Text="全部图标 · "+_catalogCards.Count+" 个可添加";
        ApplyCatalogFilter();
        QueueVisibleIcons();
    }
    private void OnCatalogScrollChanged(object sender,ScrollChangedEventArgs e)=>QueueVisibleIcons();
    private void OnCatalogSizeChanged(object sender,SizeChangedEventArgs e)=>QueueVisibleIcons();
    private void QueueVisibleIcons()
    {
        if(_catalogIconTimer is null)
        {
            _catalogIconTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(120)};
            _catalogIconTimer.Tick+=(_,_)=>{_catalogIconTimer.Stop();LoadVisibleIcons();};
        }
        _catalogIconTimer.Stop();_catalogIconTimer.Start();
    }
    private void LoadVisibleIcons()
    {
        if(!_catalogVisible)return;
        double height=CatalogScroll.ViewportHeight;
        if(height<=0)return;
        foreach(var card in _catalogCards)
        {
            if(card.Icon.IconRequested||card.Card.Visibility!=Visibility.Visible)continue;
            try
            {
                var rect=card.Card.TransformToAncestor(CatalogScroll).TransformBounds(new Rect(0,0,card.Card.ActualWidth,card.Card.ActualHeight));
                // 上下多留一屏缓冲，滚动时图标已经在队列里，不会看到明显空白。
                if(rect.Bottom<-160||rect.Top>height+160)continue;
                card.Icon.RequestIcon();
            }
            catch{/* 卡片尚未完成布局，下一轮滚动再试 */}
        }
    }
    private void OnCatalogAccept(object sender,RoutedEventArgs e)
    {
        var picked=_catalogSelection.ToList();if(picked.Count==0)return;
        _interactions++;
        try
        {
            var category=(string)(CatalogCategory.SelectedValue??"uncategorized");
            var cache=Path.Combine(Store.DataDirectory,"catalog-cache");
            int done=0;var errors=new List<string>();var collected=new List<string>();
            foreach(var entry in picked)
            {
                try
                {
                    var temp=Path.Combine(cache,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Source)))[..16]+".lnk");
                    AppCatalog.Materialize(entry,temp);
                    collected.Add(Store.CollectApp(temp,entry.Name,entry.Source,category).Id);
                    done++;
                }
                catch(Exception ex){errors.Add(entry.Name+"："+DescribeError(ex));}
            }
            _catalogSelection.Clear();_catalogLoaded=false;
            _activeCategory=category;
            if(collected.Count>0)RecordUndo("收纳 "+collected.Count+" 个应用",()=>
            {
                foreach(var id in collected)if(Store.State.Items.Any(i=>i.Id==id))Store.ArchiveItem(id);
            });
            ShowIconsView();
            SetStatus("已喂给番茄 "+done+" 个应用"+(errors.Count>0?" · 失败 "+errors.Count+" 项":""));
            if(errors.Count>0)UiDialogs.Show(this,string.Join("\n",errors),"部分应用未完成收纳",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        finally{_interactions--;}
    }
}
