# 源码与构建

本目录包含 R36.Share1 的 C# 源码，目标框架为 `net8.0-windows`，主要界面为 WinForms，使用 Revit 2025 API。项目启用 Windows Desktop 的 WPF 引用以支持 Revit 界面配合。

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -RevitApiDir "C:\Program Files\Autodesk\Revit 2025"
```

需要 Windows、支持 .NET 8 目标框架的 SDK，以及本机 Revit 2025 安装目录中的 `RevitAPI.dll` 和 `RevitAPIUI.dll`。如果 Revit 安装在其他位置，修改 `-RevitApiDir`。输出位于仓库根目录的 `build`。

无需编译即可使用 [installer](../installer/README.md) 中已编译的对应插件。

## 目录对应

| 目录 | 内容 |
| --- | --- |
| `src/CaissonWall.UiReview.Revit2025` | 界面、参数、几何、宿主读取和钢筋创建 |
| `scripts` | 构建与隔离安装器检查 |
| `tests/Share1Checks` | 运行目录及空壳默认入口检查 |
| `verification/software` | 已完成的软件检查记录 |

## 本地检查

从仓库根目录运行：

```powershell
dotnet run --project .\tests\Share1Checks\Share1Checks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Installer.ps1
```

这些离线检查不需要安装 Revit。安装器检查把 APPDATA、LOCALAPPDATA 重定向到独立测试目录，并模拟 Revit 进程状态。原 Windows 用户的插件目录不会被修改，测试目录保留在 `build/installer-tests`。

`.github/workflows/checks.yml` 使用相同的两组离线检查。工作流不构建依赖 Revit API 的主插件，也不在 GitHub 托管运行器中启动 Revit。该工作流已准备，尚未上传仓库或在 GitHub 上执行。

## 与 R36 的关系

R36.Share1 将草稿与日志改为当前用户的 `%LOCALAPPDATA%\CaissonRebar\R36.Share1`，去除原项目默认参数及个人目录，将相关导入标识改为通用标识。核心几何计算保留，具体文件比对见 `verification/software/release-verification.json`。

## License / 许可

Copyright 2026 The CaissonRebar Authors. 本目录的项目自有内容统一采用 [Apache License 2.0](../LICENSE)，见 [版权声明](../NOTICE) 和 [许可范围](../docs/LICENSING.md)。
