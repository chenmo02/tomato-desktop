using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNook.Core;

namespace DesktopNook.App;

// 独立设置页：外观、行为、数据备份与关于。所有颜色取自 Application.Resources，
// 主题切换时同一批画刷实例会同步变色，无需重建设置页。
public partial class MainWindow
{
    internal AppSettings Settings = new();
    private UndoService? _undo;
    private AutoCollectService? _autoCollect;
    private HashSet<string> _brokenIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _settingsVisible;
    private bool _settingsLoaded;
    private bool _suppressSettings;
    private CheckBox? _accentCheck, _infoCheck, _startupCheck, _hotKeyCheck, _autoCollectCheck, _closeCheck, _autoHideCheck, _systemItemsCheck;
    private Button? _themeButton, _skinButton, _undoButton;
    private TextBlock? _themeLabel, _skinLabel;
    // 记录上一次已应用到「全部图标」列表的偏好，避免每次主题/开关变更都重建列表。
    private bool _systemItemsApplied;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);
    // 代码创建的控件用动态资源绑定：主题整条替换画刷时无需重建设置页。
    private static void Bind(FrameworkElement element, DependencyProperty property, string key)
        => element.SetResourceReference(property, key);

    // Win11 设置分组：标题在卡片外，卡片内是等高行与左右内缩的分隔线，圆角裁掉悬停底色。
    private FrameworkElement SettingsSection(string title, params FrameworkElement[] rows)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
        var header = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) };
        Bind(header, TextBlock.ForegroundProperty, "WinText");
        section.Children.Add(header);
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        Bind(card, Border.BackgroundProperty, "WinCardFill");
        Bind(card, Border.BorderBrushProperty, "WinStroke");
        var content = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            content.Children.Add(rows[i]);
            if (i < rows.Length - 1) content.Children.Add(SettingsDivider());
        }
        card.Child = content;
        section.Children.Add(card);
        return section;
    }

    private static Border SettingsDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(20, 0, 20, 0) };
        Bind(line, Border.BackgroundProperty, "WinStrokeSoft");
        return line;
    }

    // 每行左侧是 14px SemiBold 标题 + 12px 次要色说明，右侧控件垂直居中；行高由样式统一为 64px。
    private FrameworkElement SettingRow(string name, string description, FrameworkElement control)
    {
        var row = new Border { Style = Sty("WinSettingsRow") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var title = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        Bind(title, TextBlock.ForegroundProperty, "WinText");
        text.Children.Add(title);
        if (!string.IsNullOrEmpty(description))
        {
            var desc = new TextBlock { Text = description, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
            Bind(desc, TextBlock.ForegroundProperty, "WinTextSecondary");
            text.Children.Add(desc);
        }
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        row.Child = grid;
        return row;
    }

    private Button ActionButton(string text, RoutedEventHandler click, double width = 132)
    {
        var button = new Button { Content = text, Style = Sty("WinSecondaryButton"), MinWidth = width, FontSize = 12 };
        button.Click += click;
        return button;
    }

    // 主题选择用 WinUI 下拉选择器：当前值 + 右侧箭头，点击后弹出浅色/深色/跟随系统。
    private Button ThemeCombo()
    {
        var button = new Button { Style = Sty("WinSettingsCombo"), FontSize = 12 };
        var panel = new Grid { Width = 116 };
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _themeLabel = new TextBlock { Text = "跟随系统", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        Bind(_themeLabel, TextBlock.ForegroundProperty, "WinText");
        var chevron = new ContentControl { Style = Sty("WinGlyphChevronDown"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        panel.Children.Add(_themeLabel);
        Grid.SetColumn(chevron, 1);
        panel.Children.Add(chevron);
        button.Content = panel;
        button.Click += OnThemePick;
        return button;
    }

    // 皮肤选择与主题共用同一套 WinUI 下拉选择器：点击后即时写盘、重刷色板与窗口几何。
    private Button SkinCombo()
    {
        var button = new Button { Style = Sty("WinSettingsCombo"), FontSize = 12 };
        var panel = new Grid { Width = 116 };
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _skinLabel = new TextBlock { Text = "原生系统", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        Bind(_skinLabel, TextBlock.ForegroundProperty, "WinText");
        var chevron = new ContentControl { Style = Sty("WinGlyphChevronDown"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        panel.Children.Add(_skinLabel);
        Grid.SetColumn(chevron, 1);
        panel.Children.Add(chevron);
        button.Content = panel;
        button.Click += OnSkinPick;
        return button;
    }

    private void BuildSettings()
    {
        SettingsGroups.Children.Clear();
        _themeButton = ThemeCombo();
        _skinButton = SkinCombo();
        var appearance = new List<FrameworkElement>
        {
            SettingRow("皮肤", "原生系统贴近 Win11 控件；玻璃拟态使用半透明面板、模糊背景与柔和光晕", _skinButton),
            SettingRow("主题", "跟随 Windows 应用深浅设置，也可固定为浅色或深色", _themeButton)
        };
        _accentCheck = SettingCheckBox(appearance, "强调色跟随系统", "主操作按钮、链接与聚焦下划线使用系统强调色");
        _infoCheck = SettingCheckBox(appearance, "显示顶部提示条", "关闭后不再显示操作说明，可在设置里重新打开");
        SettingsGroups.Children.Add(SettingsSection("外观", appearance.ToArray()));

        var behavior = new List<FrameworkElement>();
        _startupCheck = SettingCheckBox(behavior, "开机自启", "登录 Windows 后自动在任务栏待命");
        _hotKeyCheck = SettingCheckBox(behavior, "全局热键 " + HotKeyService.Display, "任意界面下按热键即可唤起或收起面板");
        _autoHideCheck = SettingCheckBox(behavior, "失焦时自动收起", "默认关闭：点击桌面或其它窗口不会收起面板，必须手动点 × 或按热键");
        _autoCollectCheck = SettingCheckBox(behavior, "自动收纳新桌面图标", "每 15 秒检查桌面，新增项目自动收进「未分类」");
        _systemItemsCheck = SettingCheckBox(behavior, "显示系统项目", "「全部图标」中默认隐藏 Windows 工具、命令行、诊断组件等系统项目，开启后一并列出");
        behavior.Add(SettingRow("立即扫描桌面", "不等下一轮，马上收纳桌面上的新项目", ActionButton("立即扫描", OnScanNow)));
        _closeCheck = SettingCheckBox(behavior, "关闭时结束进程", "关闭按钮直接退出；关闭后面板只收起");
        SettingsGroups.Children.Add(SettingsSection("行为", behavior.ToArray()));

        var data = new List<FrameworkElement>
        {
            SettingRow("导出配置包", "把分类、图标、托管副本与偏好打包成一个 zip", ActionButton("导出…", OnExport)),
            SettingRow("导入配置包", "替换当前收纳数据，导入前会自动留一份备份", ActionButton("导入…", OnImport))
        };
        _undoButton = ActionButton("撤销", OnUndoButton, 116);
        data.Add(SettingRow("撤销上一步", "也可在任意界面按 Ctrl+Z；最近 20 步可撤销", _undoButton));
        data.Add(SettingRow("最近操作", "保留 30 天文字日志，只记录在本地", ActionButton("查看记录", OnShowLog)));
        data.Add(SettingRow("数据目录", "所有文件都在本机，不上传、不联网", ActionButton("打开目录", OnOpenData)));
        data.Add(SettingRow("失效入口", "检测快捷方式目标是否已丢失，失效图标会标灰", ActionButton("检查并清理", OnCleanBroken)));
        SettingsGroups.Children.Add(SettingsSection("数据与备份", data.ToArray()));

        var about = new List<FrameworkElement>
        {
            SettingRow("番茄桌面 " + CurrentVersionLabel, "纯本地运行；在线更新当前停用，可用「更多 → 从本地文件更新」", ActionButton("创建入口", OnCreateShortcuts)),
            SettingRow("退出并结束进程", "结束后台驻留，下次需手动启动", ActionButton("退出", (_,_) => Quit(), 116))
        };
        SettingsGroups.Children.Add(SettingsSection("关于", about.ToArray()));
        RefreshSettingsControls();
    }

    private CheckBox SettingCheckBox(List<FrameworkElement> rows, string name, string description)
    {
        var check = new CheckBox { Style = Sty("WinToggleSwitch") };
        check.Checked += OnSettingChanged; check.Unchecked += OnSettingChanged;
        rows.Add(SettingRow(name, description, check));
        return check;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        // 刷新控件状态会触发 Checked/Unchecked，这里必须抑制，否则会与 ApplySettings 互相递归。
        if (!_settingsLoaded || _suppressSettings) return;
        Settings = Settings with
        {
            AccentFollow = _accentCheck?.IsChecked == true,
            InfoBarDismissed = _infoCheck?.IsChecked != true,
            HotKey = _hotKeyCheck?.IsChecked == true,
            AutoCollect = _autoCollectCheck?.IsChecked == true,
            AutoHideOnDeactivate = _autoHideCheck?.IsChecked == true,
            ShowSystemItems = _systemItemsCheck?.IsChecked == true
        };
        App.Options.CloseEndsProcess = _closeCheck?.IsChecked == true;
        if (ReferenceEquals(sender, _startupCheck)) App.Options.SetEnabled(_startupCheck!.IsChecked == true);
        ApplySettings();
    }

    private void OnThemePick(object sender, RoutedEventArgs e)
    {
        var options = new List<object>();
        foreach (var (mode, label) in new[] { ("system", "跟随系统"), ("light", "浅色"), ("dark", "深色") })
        {
            var item = Menu(label, () => { Settings = Settings with { Theme = mode }; ApplySettings(); });
            item.IsCheckable = true; item.IsChecked = Settings.ThemeMode == mode;
            options.Add(item);
        }
        Popup((FrameworkElement)sender, options);
    }

    private void OnSkinPick(object sender, RoutedEventArgs e)
    {
        var options = new List<object>();
        foreach (var (mode, label) in new[] { (SkinService.Native, "原生系统"), (SkinService.Glass, "玻璃拟态") })
        {
            var item = Menu(label, () => { Settings = Settings with { Skin = mode }; ApplySettings(); });
            item.IsCheckable = true; item.IsChecked = SkinService.Normalize(Settings.Skin) == mode;
            options.Add(item);
        }
        Popup((FrameworkElement)sender, options);
    }

    private void RefreshSettingsControls()
    {
        if (_accentCheck is null) return;
        _suppressSettings = true;
        try
        {
            _accentCheck.IsChecked = Settings.AccentFollow;
            _infoCheck!.IsChecked = !Settings.InfoBarDismissed;
            _hotKeyCheck!.IsChecked = Settings.HotKey;
            _autoCollectCheck!.IsChecked = Settings.AutoCollect;
            _autoHideCheck!.IsChecked = Settings.AutoHideOnDeactivate;
            _closeCheck!.IsChecked = App.Options.CloseEndsProcess;
            _startupCheck!.IsChecked = App.Options.Enabled;
            _systemItemsCheck!.IsChecked = Settings.ShowSystemItems;
            if (_themeLabel is not null) _themeLabel.Text = Settings.ThemeMode switch { "light" => "浅色", "dark" => "深色", _ => "跟随系统" };
            if (_skinLabel is not null) _skinLabel.Text = SkinService.IsGlass(Settings) ? "玻璃拟态" : "原生系统";
            if (_skinButton is not null) _skinButton.ToolTip = BackdropService.Status;
            // 玻璃皮肤使用固定的番茄红强调色，跟随系统开关在该皮肤下无意义，置灰并说明原因。
            _accentCheck.IsEnabled = !SkinService.IsGlass(Settings);
            _accentCheck.ToolTip = _accentCheck.IsEnabled ? null : "玻璃拟态皮肤使用固定的番茄红强调色";
            if (_undoButton is not null) _undoButton.Content = _undo?.Next is string next ? (next.Length > 8 ? next[..8] + "…" : next) : "无可撤销";
        }
        finally { _suppressSettings = false; }
    }

    private void ApplySettings()
    {
        App.Options.WriteSettings(Settings);
        ThemeService.Apply(Settings);
        // 先切 DWM 系统背板（Acrylic / Mica），再让自绘背景层按新状态决定显隐。
        ApplyBackdrop();
        // 皮肤形态（圆角、光晕、背景层）同步生效；壁纸解码仍交给后台刷新路径。
        ApplySkinVisual(false);
        _glassWallpaper = null;
        RefreshGlassWallpaper();
        ApplyHotKey();
        _autoCollect?.SetEnabled(Settings.AutoCollect);
        InfoBar.Visibility = Settings.InfoBarDismissed ? Visibility.Collapsed : Visibility.Visible;
        UpdateTabSelection();
        Render();
        // 只有「显示系统项目」偏好真的变化（含导入配置包）时才刷新「全部图标」列表。
        if(_systemItemsApplied!=Settings.ShowSystemItems)
        {
            _systemItemsApplied=Settings.ShowSystemItems;
            ApplySystemItemsPreference();
        }
        RefreshSettingsControls();
    }

    private void ApplyHotKey()
    {
        if (!Settings.HotKey) { HotKeyService.Unregister(); return; }
        if (!HotKeyService.Register(this, () => Dispatcher.BeginInvoke(new Action(TogglePanel))))
            SetStatus("全局热键 " + HotKeyService.Display + " 已被其它程序占用，可在设置里关闭");
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        if (_settingsVisible) { ShowIconsView(); return; }
        if (!_settingsLoaded) { BuildSettings(); _settingsLoaded = true; }
        ShowSettingsView();
    }

    internal void ShowSettingsView()
    {
        // 无论从侧栏、更多菜单还是自检直接进入，都保证分组内容已构建。
        if (!_settingsLoaded) { BuildSettings(); _settingsLoaded = true; }
        _settingsVisible = true; _catalogVisible = false;
        CatalogSurface.Visibility = Visibility.Collapsed;
        ContentToolbar.Visibility = Visibility.Collapsed;
        DropSurface.Visibility = Visibility.Collapsed;
        SettingsSurface.Visibility = Visibility.Visible;
        Summary.Text = "设置 · 纯本地数据";
        UpdateTabSelection();
        RefreshSettingsControls();
    }

    private void OnScanNow(object sender, RoutedEventArgs e)
    {
        var count = _autoCollect?.ScanNow() ?? 0;
        Render();
        SetStatus(count > 0 ? "已自动收纳 " + count + " 个新项目到未分类" : "桌面上没有需要收纳的新项目");
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "导出番茄桌面配置包", Filter = "番茄桌面配置包 (*.zip)|*.zip", FileName = "番茄桌面-配置-" + DateTime.Now.ToString("yyyyMMdd") + ".zip" };
        if (picker.ShowDialog(this) != true) return;
        try { SetStatus(DataPortability.Export(Store, App.Options, picker.FileName)); }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "导入番茄桌面配置包", Filter = "番茄桌面配置包 (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (UiDialogs.Show(this, "导入会替换当前收纳数据，程序会先在数据目录里留一份完整备份。确认继续？", "导入配置包", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            var result = DataPortability.Import(Store, App.Options, picker.FileName);
            App.ReloadStore();
            Settings = App.Options.ReadSettings();
            ApplySettings();
            ShellIntegration.RefreshDesktop();
            SetStatus(result);
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void OnShowLog(object sender, RoutedEventArgs e)
    {
        _interactions++;
        try { UiDialogs.Show(this, _undo?.Recent() ?? "暂无操作记录", "最近操作", MessageBoxButton.OK); }
        finally { _interactions--; }
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Store.DataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void OnCreateShortcuts(object sender, RoutedEventArgs e) => Run(() => ShortcutInstaller.InstallAll(), "已创建桌面与开始菜单入口");

    private void OnCleanBroken(object sender, RoutedEventArgs e)
    {
        ScanBroken();
        var ids = _brokenIds.Where(id => Store.State.Items.Any(i => i.Id == id)).ToList();
        if (ids.Count == 0) { SetStatus("没有发现失效入口"); return; }
        if (UiDialogs.Show(this, "移除 " + ids.Count + " 个失效入口？恢复副本会保留，可按 Ctrl+Z 撤销。", "清理失效入口", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var snapshot = ids.Select(id => Store.State.Items.Single(i => i.Id == id)).ToList();
        foreach (var id in ids) Store.ArchiveItem(id);
        _undo?.Record("清理 " + ids.Count + " 个失效入口", () => Store.RestoreCatalog(snapshot));
        _brokenIds.Clear();
        Render();
        SetStatus("已移除 " + ids.Count + " 个失效入口，可按 Ctrl+Z 撤销");
    }

    private void ScanBroken()
    {
        try { _brokenIds = ShortcutHealth.Scan(Store); Render(); }
        catch { /* 单个快捷方式解析失败不影响面板使用 */ }
    }
}
