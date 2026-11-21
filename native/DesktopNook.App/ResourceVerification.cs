using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

// 后台资源自检（--resource-self-test <output>）：纯内存验证，不读取或改动真实桌面。
// 覆盖四件事：解码线程不常驻、图标缓存按预算淘汰、空闲线程自动退出、自动收纳后台降频。
internal static class ResourceVerification
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        try
        {
            var before=ShellIntegration.Snapshot();
            Check(before.ActiveWorkers==0,"图标解码线程按需启动，空闲时不常驻");
            Check(before.CacheBudget>0&&before.CacheBytes<=before.CacheBudget,"图标缓存设有字节预算");
            Check(ShellIntegration.AppIcon is BitmapSource { PixelWidth: 192, IsFrozen: true },"小尺寸品牌标识共享缩小后的位图");

            // 8×8 的固定小位图：LRU 淘汰结果可精确预期，不依赖 AppIcon 的实际尺寸。
            var pixel=new byte[8*8*4];
            var sample=BitmapSource.Create(8,8,96,96,PixelFormats.Pbgra32,null,pixel,8*4);sample.Freeze();
            var cache=new IconCache();
            cache.SetBudget(8192);
            for(int i=0;i<60;i++)cache.Add("probe-"+i,sample);
            Check(cache.Count<60,"缓存条目数受预算约束，不再无界增长");
            Check(cache.Bytes<=cache.Budget,"缓存占用不超过预算");
            Check(cache.TryGet("probe-59",out _),"最近使用的条目仍在缓存中");
            Check(!cache.TryGet("probe-0",out _),"最久未用的条目已淘汰");
            cache.SetBudget(4096);
            Check(cache.Count==4&&cache.Bytes==4096,"下调预算时立即淘汰旧图标");
            for(int i=60;i<80;i++)cache.Add("probe-"+i,sample);
            Check(cache.Count==4&&cache.Bytes==4096,"解码结果继续入缓存时仍遵守较低的预算");
            cache.Clear();
            Check(cache.Count==0&&cache.Bytes==0,"缓存可以完全清空");

            // 真实队列：一个不存在的临时路径会让 Shell 调用立即失败，不会触碰桌面。
            ShellIntegration.SetIconCacheBackground(true);
            Check(ShellIntegration.Snapshot().CacheBudget==IconCache.BackgroundBudget,"面板收起时启用后台缓存预算");
            ShellIntegration.SetWorkerIdleForVerification(TimeSpan.FromMilliseconds(600));
            var probePath=Path.Combine(Path.GetTempPath(),"desktopnook-resource-probe-missing.lnk");
            ShellIntegration.EnqueueForVerification(new IconJob(probePath,"resource-probe",16,false));
            var deadline=DateTime.UtcNow.AddSeconds(8);
            while(DateTime.UtcNow<deadline&&ShellIntegration.Snapshot().ProcessedJobs<=before.ProcessedJobs)
                System.Threading.Thread.Sleep(20);
            Check(ShellIntegration.Snapshot().ProcessedJobs>before.ProcessedJobs,"按需启动的解码线程完成了探针任务");
            deadline=DateTime.UtcNow.AddSeconds(8);
            while(DateTime.UtcNow<deadline&&ShellIntegration.Snapshot().ActiveWorkers>0)
                System.Threading.Thread.Sleep(20);
            Check(ShellIntegration.Snapshot().ActiveWorkers==0,"解码线程空闲后自动退出");
            var after=ShellIntegration.Snapshot();
            Check(after.CacheBytes<=after.CacheBudget,"挂起态完成解码后仍遵守后台缓存预算");
            ShellIntegration.SetIconCacheBackground(false);
            Check(ShellIntegration.Snapshot().CacheBudget==before.CacheBudget,"面板恢复后还原前台缓存预算");

            var collector=new AutoCollectService(()=>throw new InvalidOperationException("资源自检不扫描桌面"),_=>{});
            Check(collector.ScanInterval==TimeSpan.FromSeconds(15),"面板可见时自动收纳保持 15 秒间隔");
            collector.SetBackgroundMode(true);
            Check(collector.ScanInterval==TimeSpan.FromSeconds(60),"面板收起后自动收纳降频到 60 秒");
            collector.SetBackgroundMode(false);
            Check(collector.ScanInterval==TimeSpan.FromSeconds(15),"面板恢复后自动收纳回到 15 秒");

            File.WriteAllText(Path.Combine(output,"resource.json"),JsonSerializer.Serialize(new
            {
                passed=true,checks,
                before=new{cacheCount=before.CacheCount,cacheBytes=before.CacheBytes,cacheBudget=before.CacheBudget,activeWorkers=before.ActiveWorkers},
                after=new{cacheCount=after.CacheCount,cacheBytes=after.CacheBytes,activeWorkers=after.ActiveWorkers,processedJobs=after.ProcessedJobs}
            },new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());
            File.WriteAllText(Path.Combine(output,"resource.json"),JsonSerializer.Serialize(new{passed=false,checks,error=error.Message},new JsonSerializerOptions{WriteIndented=true}));
            return 1;
        }
    }
}
