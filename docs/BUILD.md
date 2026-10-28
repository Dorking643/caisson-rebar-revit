# Building and checking the source

Run all commands from the repository root on Windows.

## Build the add-in

Install a .NET SDK that supports `net8.0-windows`. Locate `RevitAPI.dll` and `RevitAPIUI.dll` inside your Revit 2025 installation; these files are local build dependencies and are not distributed here.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -RevitApiDir "C:\Program Files\Autodesk\Revit 2025"
```

Set `-RevitApiDir` to the actual installation folder when different. The default output directory is `build`. The project produces `P7.CaissonWallUi.R36.Share1.dll`. Its target framework is `net8.0-windows`; the prepared binary was built with SDK 10.0.400.

The installer uses its checked-in payload and matching `payload-sha256.json`. Building the project does not automatically replace that payload. When preparing a modified binary, update both the payload and checksums together and record the changed version.

## Run checks without Revit

```powershell
dotnet run --project .\tests\Share1Checks\Share1Checks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Installer.ps1
```

`Share1Checks` checks user data paths and the empty-host default behavior. The installer test redirects `APPDATA` and `LOCALAPPDATA` to a fresh sandbox and simulates the Revit process check. It does not install the add-in into the actual user's Revit directory.

## Verify downloaded files

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1
```

This checks the files listed in `MANIFEST_SHA256.csv`. Once distribution files are intentionally edited, regenerate the manifest before packaging a new release:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-Manifest.ps1
```

Review the resulting file list. The manifest generator excludes the current build, local runtime, backup and environment-file patterns; review those exclusions when changing `.gitignore`. Build outputs are excluded from source control.

## 中文说明

从仓库根目录运行以上命令。编译需要本机 Revit 2025 API；路径/空壳入口检查和安装器隔离测试不需要启动 Revit。编译输出与已打包安装程序分开，替换安装 DLL 时应同步更新校验值及版本记录。
