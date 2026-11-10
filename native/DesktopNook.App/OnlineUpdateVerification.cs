using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DesktopNook.App;

internal static class OnlineUpdateVerification
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(reply(request));}
    }
    internal static async Task<int> Run(string output,string candidate,bool live=false)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();string failure="";
        if(!OnlineUpdater.Enabled)
        {
            File.WriteAllText(Path.Combine(output,"online-update-verification.json"),JsonSerializer.Serialize(new{success=true,skipped=true,passed=0,checks=new[]{"在线更新未启用（未配置发布仓库），跳过专项验证"}},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        try
        {
            var version=LocalUpdater.Inspect(candidate).Version;long size=new FileInfo(candidate).Length;
            string digest=LocalUpdater.Hash(candidate),url=$"https://github.com/{OnlineUpdater.Repository}/releases/download/v{version.ToString(3)}/{OnlineUpdater.AssetName}";
            object Asset(string hash,string address)=>new{name=OnlineUpdater.AssetName,browser_download_url=address,size,digest="sha256:"+hash};
            string Feed(object asset)=>JsonSerializer.Serialize(new{tag_name="v"+version.ToString(3),draft=false,prerelease=false,body="更新验证",assets=new[]{asset}});
            string feed=Feed(Asset(digest,url));
            using var client=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest
                ?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(feed,Encoding.UTF8,"application/json")}
                :new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(candidate))}));
            var release=await OnlineUpdater.CheckAsync(client,new Version(0,7,1))??throw new Exception("未检测到更新。");checks.Add("最新发行版本与指定 EXE 识别");
            if(await OnlineUpdater.CheckAsync(client,version) is not null)throw new Exception("相同版本不应提示更新。");checks.Add("已是最新版时不重复更新");
            var file=await OnlineUpdater.DownloadAsync(client,release,output,null,CancellationToken.None);
            if(LocalUpdater.Hash(file)!=digest)throw new Exception("下载文件不一致。");checks.Add("流式下载、长度、SHA-256 与 EXE 产品版本联合校验");
            try{await OnlineUpdater.DownloadAsync(client,release with{Sha256=new string('0',64)},output,null,CancellationToken.None);throw new Exception("损坏文件未拒绝。");}
            catch(IOException){checks.Add("损坏下载拒绝执行并清理临时文件");}
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await OnlineUpdater.DownloadAsync(client,release,output,null,cancelled.Token);throw new Exception("取消无效。");}catch(OperationCanceledException){checks.Add("取消下载保留现有程序");}}
            using(var bad=JsonDocument.Parse(Feed(Asset(digest,"https://example.com/update.exe"))))
            {try{OnlineUpdater.Parse(bad.RootElement,new Version(0,1));throw new Exception("外部发布源未拒绝。");}catch(IOException){checks.Add("非专用仓库下载地址拒绝");}}
            if(Directory.EnumerateFiles(output,"*.partial",SearchOption.AllDirectories).Any())throw new Exception("失败的下载遗留不完整文件。");
            checks.Add("失败及取消不会留下可执行的半成品");
            if(live)
            {
                using var remote=OnlineUpdater.CreateClient();var published=await OnlineUpdater.CheckAsync(remote,new Version(0,7,1))??throw new Exception("线上未发现发行版。");
                var actual=await OnlineUpdater.DownloadAsync(remote,published,output,null,CancellationToken.None);
                if(LocalUpdater.Hash(actual)!=digest)throw new Exception("线上资产与本地产物不同。");checks.Add("真实 GitHub 公共接口与发行包下载校验通过");
            }
        }
        catch(Exception ex){failure=ex.ToString();}
        File.WriteAllText(Path.Combine(output,"online-update-verification.json"),JsonSerializer.Serialize(new{success=failure=="",passed=checks.Count,checks,failure},new JsonSerializerOptions{WriteIndented=true}));return failure==""?0:1;
    }
}
