# 原生源码

当前源码版本为 1.2，使用 WPF / .NET 10，项目包含完整应用、迁移核心、测试、图标资源、安装器与发行脚本。

- [项目首页](../README.md)
- [使用说明](../docs/使用说明.md)
- [架构与数据](../docs/架构与数据.md)
- [开发、测试与发行](../docs/开发与构建.md)

从仓库根目录运行 `pwsh -NoProfile -File native/scripts/Build.ps1` 构建，产物位于 `native/artifacts/win-x64/DesktopNook.exe`。

构建结果、测试报告、本机配置与恢复数据不提交到 Git。运行正常应用会操作真实桌面，开发时请先使用专用 `--sandbox` 根目录。
