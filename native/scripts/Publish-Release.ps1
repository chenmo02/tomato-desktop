param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$NotesFile,
    [string]$Repository='fangmingzhao69-glitch/desktop-nook-releases',
    [switch]$VerifyOnly
)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$Installer=(Resolve-Path -LiteralPath $Installer).Path
$NotesFile=(Resolve-Path -LiteralPath $NotesFile).Path
$exeInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo($Executable)
$setupInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo($Installer)
$version=([version]$exeInfo.FileVersion).ToString(3)
if($exeInfo.ProductName -ne '番茄桌面' -or $setupInfo.ProductName -ne '番茄桌面' -or ([version]$setupInfo.FileVersion).ToString(3) -ne $version){throw '程序与安装包版本或产品标识不一致。'}
$tag='v'+$version
$stage=Join-Path $PSScriptRoot ('..\artifacts\github-release\'+$tag)
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$binary=Join-Path $stage 'DesktopNook-win-x64.exe'
$setup=Join-Path $stage 'DesktopNook-Setup.exe'
Copy-Item -LiteralPath $Executable -Destination $binary -Force
Copy-Item -LiteralPath $Installer -Destination $setup -Force
$hashes=@($binary,$setup)|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_)}
$checksum=Join-Path $stage 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksum,$hashes,[Text.UTF8Encoding]::new($false))
$readme=@'
# 番茄桌面 · Windows 发布

将选中的桌面软件、文件和文件夹收纳到一个任务栏入口，按分类访问。

## 下载与使用

打开本仓库的 Releases，下载最新正式版：

- `DesktopNook-Setup.exe`：安装版，适合首次安装或覆盖升级。
- `DesktopNook-win-x64.exe`：免安装版，下载后直接运行。
- `SHA256SUMS.txt`：发布文件的 SHA-256 校验值。

支持 Windows x64，程序内含运行时。在线更新源当前停用，启动后不会后台检查新版本；请手动下载新版后使用“更多 → 从本地文件更新…”，校验通过后自动更新并重启。首次从旧版本升级，请覆盖安装一次。

分类、收纳内容及设置保留在本机；本仓库仅用于发布程序与更新说明，不包含用户数据。下载速度受网络与系统代理设置影响。

下载失败时可重试，也可手动下载后使用“更多 → 从本地文件更新”。
'@
$priorProxy=$env:HTTPS_PROXY
try {
    if([string]::IsNullOrWhiteSpace($env:HTTPS_PROXY)) {
        $site=[uri]'https://github.com'
        $proxy=[Net.WebRequest]::GetSystemWebProxy().GetProxy($site)
        if($proxy.AbsoluteUri -ne $site.AbsoluteUri){$env:HTTPS_PROXY=$proxy.AbsoluteUri}
    }
    & gh repo view $Repository --json name 2>$null | Out-Null
    if($LASTEXITCODE -ne 0) {
        & gh repo create $Repository --public --description '番茄桌面 Windows 正式版、安装包与在线更新发布仓库'
        if($LASTEXITCODE -ne 0){throw '无法创建发布仓库。'}
        $request=Join-Path $stage 'readme-request.json'
        $payload=@{message='Add release instructions';content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($readme))}|ConvertTo-Json
        [IO.File]::WriteAllText($request,$payload,[Text.UTF8Encoding]::new($false))
        & gh api "repos/$Repository/contents/README.md" --method PUT --input $request --silent
        if($LASTEXITCODE -ne 0){throw '无法写入发布说明。'}
    }
    $releases=& gh api "repos/$Repository/releases?per_page=100" | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw '无法读取发行版列表。'}
    $existing=$releases|Where-Object tag_name -eq $tag
    if($VerifyOnly -and -not $existing){throw '待核验的发行版不存在。'}
    if($existing -and -not $existing.draft -and -not $VerifyOnly){throw '该版本已公开，请使用新版本号，不覆盖已发布文件。'}
    # Publish only the three explicit files, after all uploads and hashes succeed.
    if(-not $existing) {
        & gh release create $tag $binary $setup $checksum --repo $Repository --draft --title ('番茄桌面 '+$version) --notes-file $NotesFile
        if($LASTEXITCODE -ne 0){throw '发行包上传未完成，草稿保留，可再次运行本脚本续传。'}
    } elseif($existing.draft -and -not $VerifyOnly) {
        foreach($file in @($binary,$setup,$checksum)) {
            if(-not ($existing.assets|Where-Object name -eq ([IO.Path]::GetFileName($file)))) {
                & gh release upload $tag $file --repo $Repository
                if($LASTEXITCODE -ne 0){throw '发行包续传失败，草稿保留。'}
            }
        }
    }
    $release=@(& gh api "repos/$Repository/releases" | ConvertFrom-Json)|Where-Object tag_name -eq $tag
    if($LASTEXITCODE -ne 0){throw '无法核对上传文件。'}
    foreach($file in @($binary,$setup,$checksum)) {
        $asset=$release.assets|Where-Object name -eq ([IO.Path]::GetFileName($file))
        $expected='sha256:'+(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
        if($asset.digest -ne $expected){throw '上传后 SHA-256 不一致，发行版保留为草稿。'}
    }
    if($VerifyOnly){Write-Output "发布文件校验通过：https://github.com/$Repository/releases/tag/$tag";return}
    & gh release edit $tag --repo $Repository --draft=false --latest
    if($LASTEXITCODE -ne 0){throw '文件已上传，但发行版尚未公开。'}
    Write-Output "https://github.com/$Repository/releases/tag/$tag"
} finally {$env:HTTPS_PROXY=$priorProxy}
