using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;

namespace DesktopNook.App;

// 「文件管理」入口已从导航条移入「更多」菜单：子菜单完整保留原先
// 左键打开文件管理器、右键跳到常用位置的全部能力。
public partial class MainWindow
{
    private MenuItem FileManagerMenu()
    {
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var menu=new MenuItem{Header="文件管理"};
        menu.Items.Add(Menu("打开文件管理器",()=>OpenExplorer(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("主文件夹",()=>OpenExplorer(home)));
        menu.Items.Add(Menu("此电脑",()=>OpenExplorer("shell:MyComputerFolder")));
        menu.Items.Add(Menu("桌面",()=>OpenExplorer(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))));
        menu.Items.Add(Menu("下载",()=>OpenExplorer(Path.Combine(home,"Downloads"))));
        menu.Items.Add(Menu("文档",()=>OpenExplorer(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))));
        menu.Items.Add(Menu("图片",()=>OpenExplorer(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("番茄桌面数据目录",()=>OpenExplorer(Store.DataDirectory)));
        return menu;
    }
    private void OpenExplorer(string? path)
    {
        try
        {
            var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};
            if(!string.IsNullOrWhiteSpace(path))start.ArgumentList.Add(path);
            Process.Start(start);
            SetStatus("已打开文件管理器");
        }
        catch(Exception ex){SetStatus("无法打开文件管理器："+ex.Message);}
    }
}
