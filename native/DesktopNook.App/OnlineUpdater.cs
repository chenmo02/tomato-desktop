using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopNook.App;

internal sealed record OnlineRelease(Version Version,Uri Download,long Size,string Sha256,string Notes);

internal static class OnlineUpdater
{
    // 在线更新发布仓库（"owner/repo"）。当前留空 = 停用：程序不会发起任何检查请求，
    // 状态栏也不会再提示旧仓库的新版本。拿到用户指定的仓库地址后，只需把 Repository
    // 填成新的 "owner/repo"（若资产名不同再改 AssetName），Enabled 自动变为 true，
    // 启动后台检查与“更多 → 检查在线更新”随即恢复，其余代码无需改动。
    internal const string Repository="";
    internal const string AssetName="DesktopNook-win-x64.exe";
    internal static bool Enabled=>!string.IsNullOrWhiteSpace(Repository);
    internal static readonly Uri Latest=new($"https://api.github.com/repos/{Repository}/releases/latest");
    internal static HttpClient CreateClient()
    {
        // HttpClient uses the Windows proxy settings; no account token is shipped.
        var client=new HttpClient{Timeout=TimeSpan.FromMinutes(15)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopNook-Updater/0.8");
        return client;
    }
    internal static async Task<OnlineRelease?> CheckAsync(HttpClient client,Version current,CancellationToken token=default)
    {
        if(!Enabled)throw new IOException("在线更新未启用：尚未配置发布仓库地址。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response=await client.GetAsync(Latest,timeout.Token);response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));var root=json.RootElement;
        return Parse(root,current);
    }
    internal static OnlineRelease? Parse(JsonElement root,Version current)
    {
        if(root.GetProperty("draft").GetBoolean()||root.GetProperty("prerelease").GetBoolean())return null;
        if(!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v','V'),out var version))throw new IOException("发布版本号无效。");
        version=Normalize(version);
        if(version<=Normalize(current))return null;
        foreach(var asset in root.GetProperty("assets").EnumerateArray())
        {
            if(asset.GetProperty("name").GetString()!=AssetName)continue;
            var url=new Uri(asset.GetProperty("browser_download_url").GetString()!);
            string digest=asset.TryGetProperty("digest",out var hash)?hash.GetString()??"":"";
            long size=asset.GetProperty("size").GetInt64();
            if(!TrustedUrl(url)||!digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||digest.Length!=71||!digest[7..].All(Uri.IsHexDigit)||size<=0||size>512L*1024*1024)
                throw new IOException("发布文件缺少有效的下载地址、大小或 SHA-256 校验信息。");
            return new(version,url,size,digest[7..],root.TryGetProperty("body",out var body)?body.GetString()??"":"");
        }
        throw new IOException("新版本尚未提供 Windows x64 更新文件，请稍后再试。");
    }
    private static bool TrustedUrl(Uri uri)=>Enabled&&uri.Scheme==Uri.UriSchemeHttps&&uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)
        &&uri.IsDefaultPort&&string.IsNullOrEmpty(uri.UserInfo)&&uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/",StringComparison.Ordinal);
    private static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
    internal static async Task<string> DownloadAsync(HttpClient client,OnlineRelease release,string dataRoot,IProgress<double>? progress,CancellationToken token)
    {
        if(!Enabled)throw new IOException("在线更新未启用：尚未配置发布仓库地址。");
        if(!TrustedUrl(release.Download))throw new IOException("更新下载地址不属于番茄桌面发布仓库。");
        string directory=Path.Combine(dataRoot,"updates","download-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string partial=Path.Combine(directory,"candidate.partial"),file=Path.Combine(directory,"candidate.exe");
        try
        {
            using var response=await client.GetAsync(release.Download,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length&&length!=release.Size)throw new IOException("更新文件大小与发布记录不一致。");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                byte[] buffer=new byte[65536];long total=0;int count;
                while((count=await input.ReadAsync(buffer,token))>0)
                {
                    total+=count;if(total>release.Size)throw new IOException("更新文件超过预期大小。");
                    hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token);progress?.Report(total*100d/release.Size);
                }
                if(total!=release.Size)throw new IOException("更新下载不完整，请重试。");
            }
            if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件 SHA-256 校验失败，请重试。");
            token.ThrowIfCancellationRequested();File.Move(partial,file);
            if(LocalUpdater.Inspect(file).Version!=Normalize(release.Version))throw new IOException("更新程序版本与发布记录不一致。");
            return file;
        }
        catch{if(File.Exists(partial))File.Delete(partial);if(File.Exists(file))File.Delete(file);throw;}
    }
}
