# 番茄桌面 <small>DesktopNook</small>

<img src="native/DesktopNook.App/Assets/AppIcon.png" width="108" align="right" alt="番茄桌面图标">

**只收纳你选中的桌面图标，其余照旧留在桌面。** 番茄桌面是一个运行于 Windows 的桌面收纳工具：把快捷方式、文件或文件夹「喂给番茄」，之后从任务栏上的同一个入口按分类打开；需要时，一键把项目移回桌面。

![版本](https://img.shields.io/badge/version-1.2-blue) ![平台](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6?logo=windows) ![框架](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet) ![许可证](https://img.shields.io/badge/license-not%20specified-lightgrey)

[Releases](https://github.com/chenmo02/tomato-desktop/releases) · [使用说明](docs/使用说明.md) · [架构与数据](docs/架构与数据.md) · [开发与构建](docs/开发与构建.md) · [反馈问题](https://github.com/chenmo02/tomato-desktop/issues)

---

## 为什么用它

桌面图标越堆越多，但全盘整理又容易把常用的东西弄丢。番茄桌面只搬运你明确选中的项目：

1. **收纳** — 拖入桌面项目，或点「＋ 喂给番茄」多选，指定分类。
2. **访问** — 点击任务栏入口打开面板，按分类或搜索找到项目，单击即可启动。
3. **还原** — 右键「移回桌面」、把图标拖出面板，或用「全部移除」批量恢复；出错还能按 `Ctrl+Z` 撤销。

快捷方式只移动对应的 `.lnk` / `.url` 入口，软件本体保持原位；普通文件和文件夹会连同内容一起托管，收纳期间的修改随移回操作保留。

## 主要功能

- **收纳与分类**：拖入 / 多选收纳、自定义分类（新建、重命名、拖动排序）、名称搜索、「全部移除」批量移回；同名目标自动改名，不覆盖。
- **快速启动与系统菜单**：开始菜单式图标网格，单击启动，右键整理、移回桌面或调用 Windows Shell 命令；支持收纳和移回系统回收站图标。
- **窗口与布局**：80% / 100% / 125% / 150% 预设比例、自由拖动边框、锁定宽高比、尺寸与位置记忆、随时贴回任务栏；缩放窗口时图标与名称尺寸保持不变，只调整每行数量。
- **外观**：Windows 11 风格面板，跟随系统深浅主题与强调色，也可固定；「原生系统」（Mica）与「玻璃拟态」（Acrylic）两套皮肤即时切换。主界面图标按 2× 物理像素超采样后高质量缩小，避免圆边与渐变处的锯齿杂边；偏好随配置包导入导出。
- **可撤销与可恢复**：收纳、移回、分类与排序改动进入最近 20 步撤销栈，操作日志保留 30 天；启动时校验失效快捷方式，可在设置里批量清理。
- **自动化与偏好**：全局热键 `Ctrl+Alt+T`、可选开机自启、自动收纳新桌面图标（每 15 秒扫描，默认关闭）、失焦自动收起（默认关闭）。
- **本地更新**：「更多 → 从本地文件更新…」校验版本、产品标识与文件后在原位置替换并重启，保留分类、收纳内容和设置。

完整交互说明见 [使用说明](docs/使用说明.md)。

## 快速开始

### 1. 获取程序

支持 **Windows 10 / 11 x64**。发行版是自带 .NET 桌面运行时的单文件程序，普通用户无需安装 .NET SDK。

| 文件 | 用途 |
| --- | --- |
| `DesktopNook-Setup.exe` | 安装版；首次安装或覆盖升级，会创建桌面与开始菜单快捷方式 |
| `DesktopNook-win-x64.exe` | 免安装版；建议放在固定目录后运行 |
| `SHA256SUMS.txt` | 发行文件的 SHA-256 校验值 |

请从本仓库的 [Releases](https://github.com/chenmo02/tomato-desktop/releases) 下载，附件以页面实际内容为准。若 Releases 暂时没有附件，可按下方「从源码构建」自行生成单文件程序。**Code → Download ZIP 得到的是源码，需要自行构建，不能直接运行。**

### 2. 首次使用

1. 运行安装版或免安装版；
2. 右键任务栏上的「番茄桌面」图标，选择「固定到任务栏」；
3. 点击该入口打开或收起面板，把要整理的项目拖进来。

> 面板默认**不会**因点击桌面或其它窗口而收起，需点 ×、按热键或再点任务栏按钮；如需「点外部即收起」，到「设置 → 行为 → 失焦时自动收起」开启。

### 3. 常用操作

| 操作 | 方式 |
| --- | --- |
| 唤起 / 收起面板 | `Ctrl+Alt+T` |
| 撤销上一步 | `Ctrl+Z` |
| 收纳项目 | 拖入项目，或「＋ 喂给番茄」多选 |
| 归类 | 把图标拖到左侧分类行 |
| 移回桌面 | 右键「移回桌面」或拖出面板；「全部移除」批量恢复 |
| 打开搜索结果 | 在「搜索图标」框输入名称后按回车 |
| 外观 / 行为 / 备份 | 侧栏底部「设置」或「更多 → 打开设置」 |

## 从源码构建

需要 Windows、**.NET 10 SDK** 和 **PowerShell 7**；构建安装包另需 Unicode NSIS，发布到 GitHub 另需已登录的 GitHub CLI。

```powershell
git clone https://github.com/chenmo02/tomato-desktop.git
cd tomato-desktop
pwsh -NoProfile -File native/scripts/Build.ps1
```

产物为含运行时的 x64 单文件程序 `native/artifacts/win-x64/DesktopNook.exe`。脚本会先生成多尺寸图标并运行核心检查；只需重复打包时可加 `-SkipTests`。

只编译开发版本：

```powershell
dotnet build native/DesktopNook.App -c Release
```

**调试请务必使用隔离目录**，正常运行会操作真实桌面与当前用户数据：

```powershell
$profile = Join-Path $env:TEMP ('DesktopNook-dev-' + [guid]::NewGuid().ToString('N'))
dotnet run --project native/DesktopNook.App -c Release -- --sandbox $profile
```

核心检查与安装包：

```powershell
dotnet run --project native/DesktopNook.Tests -c Release -- native/test-results/core.json
makensis /V2 native/scripts/Installer.nsi   # 需要 Unicode NSIS，输出 native/artifacts/DesktopNook-Setup.exe
```

完整的测试、安装器与发行步骤见 [开发与构建](docs/开发与构建.md)。原生 UI 自检会短暂操控鼠标，必须在专用隔离目录中运行。

## 数据与备份

> **重要**：收纳会移动真实桌面文件。请通过「移回桌面」或卸载恢复流程取回内容，不要直接删除数据目录、托管目录或跨卷托管内容。

- 分类、收纳索引与恢复清单默认保存在 `%LOCALAPPDATA%\DesktopNook`（`state.json`、`migration.json`、`vault/`、`updates/`）。
- 跨卷托管的文件可能位于原桌面同卷的隐藏 `.DesktopNookVault`，只备份 JSON 或默认数据目录不足以覆盖全部实际内容。
- 窗口、主题、自启与关闭行为等偏好保存在当前 Windows 用户设置；「设置 → 数据与备份」可导出 / 导入 zip 配置包，导入前会自动备份。
- 所有数据仅保存在本机，不上传、无云端或跨设备同步；卸载时会先尝试恢复已收纳项目，有失败项时保留程序以便重试。

细节见 [架构与数据](docs/架构与数据.md)。

## 文档与仓库结构

| 文档 | 内容 |
| --- | --- |
| [使用说明](docs/使用说明.md) | 界面、收纳与移回、设置项、窗口大小、更新与卸载 |
| [架构与数据](docs/架构与数据.md) | 模块职责、数据目录、偏好存储、更新与验证范围 |
| [开发与构建](docs/开发与构建.md) | 编译、沙箱调试、测试、安装包与发行流程 |
| [原生源码说明](native/README.md) | `native/` 目录入口 |

```text
README.md
docs/                     使用说明 · 架构与数据 · 开发与构建
native/
  DesktopNook.App/        WPF 界面、Shell 集成、皮肤与背板、窗口、更新
  DesktopNook.Core/       收纳迁移、状态模型、恢复清单与文件流
  DesktopNook.Tests/      文件系统与故障恢复检查
  scripts/                构建、图标生成、NSIS 安装器、发行脚本
  releases/               版本更新说明
  Directory.Build.props
```

仓库只跟踪源码、必要的应用图标和文档；`bin/`、`obj/`、`artifacts/`、测试报告、本机分类、文件及恢复备份均不纳入 Git。

## 项目状态与已知限制

- 当前源码版本 **1.2**，基于 WPF / .NET 10（`net10.0-windows`），面向 Windows 10 / 11 x64。
- **在线更新当前停用**：`native/DesktopNook.App/OnlineUpdater.cs` 的 `Repository` 为空，启动后不会发起任何检查请求。待本仓库开始发布后，把它设为 `chenmo02/tomato-desktop`（并确保发布资产名为 `DesktopNook-win-x64.exe`）即可恢复后台检查与手动检查；恢复后下载需能访问 GitHub。
- 公共桌面快捷方式与被占用文件受 Windows 权限限制，失败时保留原项目并提示；符号链接、目录联接及未落地的云占位内容暂不按普通文件夹收纳。
- 图标清晰度受原应用资源限制；混合 DPI 多屏的完整组合尚未实机验收。
- 玻璃拟态皮肤依赖 Windows 11 22621+ 且系统「透明效果」开启，不满足时自动回退到按窗口位置对齐的模糊壁纸层。

## 反馈

请到 [Issues](https://github.com/chenmo02/tomato-desktop/issues) 反馈，附上软件版本、Windows 版本、屏幕缩放比例和复现步骤。不要公开上传整个数据目录或含个人路径的原始状态文件。

## 许可证

仓库尚未添加 `LICENSE` 文件，源码许可证待定；使用前请先与维护者确认。
