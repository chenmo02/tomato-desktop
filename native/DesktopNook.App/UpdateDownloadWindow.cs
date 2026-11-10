using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNook.App;

internal sealed class UpdateDownloadWindow : Window
{
    internal string? DownloadedFile {get;private set;}
    private readonly CancellationTokenSource _cancel=new();
    internal UpdateDownloadWindow(Window owner,OnlineRelease release,string dataRoot)
    {
        Owner=owner;Title="下载更新";Width=430;SizeToContent=SizeToContent.Height;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(22,16,22,22)};
        var message=new TextBlock{Text=$"当前版本：{MainWindow.CurrentVersionLabel}\n目标版本：v{release.Version.ToString(3)}\n正在下载，完成后将自动重启。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new Thickness(0,0,0,16)};
        var bar=new ProgressBar{Minimum=0,Maximum=100,Height=8};
        var status=new TextBlock{Text="准备下载…",Margin=new Thickness(0,10,0,18),Foreground=Brushes.SlateGray,TextWrapping=TextWrapping.Wrap};
        var cancel=new Button{Content="取消",HorizontalAlignment=HorizontalAlignment.Right,MinWidth=78};cancel.Click+=(_,_)=>{_cancel.Cancel();DialogResult=false;};
        body.Children.Add(message);body.Children.Add(bar);body.Children.Add(status);body.Children.Add(cancel);Content=body;UiDialogs.Chrome(this);
        Closed+=(_,_)=>_cancel.Cancel();
        Loaded+=async (_,_)=>
        {
            try
            {
                using var client=OnlineUpdater.CreateClient();
                DownloadedFile=await OnlineUpdater.DownloadAsync(client,release,dataRoot,new Progress<double>(value=>{bar.Value=value;status.Text=value>=100?"正在校验更新文件…":$"已下载 {value:F0}%";}),_cancel.Token);
                if(!_cancel.IsCancellationRequested)DialogResult=true;
            }
            catch(OperationCanceledException){ }
            catch(Exception){if(!_cancel.IsCancellationRequested){status.Text="下载未完成，请检查网络后重试。当前版本可以继续使用。";cancel.Content="关闭";}}
        };
    }
}
