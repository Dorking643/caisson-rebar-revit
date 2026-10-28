# 安装与使用

版本：**R36.Share1**，面向 Windows 上的 **Revit 2025**。这是可解压安装的 Revit 插件包，不是独立运行软件。

## 安装

1. 先把整个交付压缩包解压到本地。
2. 关闭全部 Revit 窗口。
3. 双击本目录的 `Install.bat`，看到 `Installation complete` 即完成。
4. 启动 Revit 2025，打开示例模型。若 Revit 提示是否加载此插件，确认插件名称后允许加载。
5. 进入 `Caisson Reinforcement` → `Model Creation`，选择 `Wall`（井壁）或 `Slab`（底板）。

安装仅作用于当前 Windows 用户，无需管理员权限。程序复制到 `%LOCALAPPDATA%\CaissonRebar\Addins\R36.Share1`；插件清单写入 `%APPDATA%\Autodesk\Revit\Addins\2025`。安装完成后，原解压目录可以移动。

本包模型为未配筋的混凝土空壳。打开编辑窗口后，根据需要定义材料、钢筋形状、群组和布置参数，再执行检查与创建。软件不会自动载入原项目的底板配筋参数。使用模型前可另存工作副本。

## 命令行与故障排查

仅检查安装内容与目标位置，不写入文件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -DryRun
```

实际安装：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

- 提示 Revit 正在运行：关闭 Revit 后重试。
- 提示文件校验失败：重新完整解压交付包，不要单独替换 DLL。
- 提示已有部分或变更安装：关闭 Revit，执行 `Uninstall.bat` 后重新安装。
- 未显示功能区：确认使用 Revit 2025，并重启 Revit。

同一插件的旧清单会备份到 `%LOCALAPPDATA%\CaissonRebar\ManifestBackups`。本安装器不删除其他插件清单。若系统级目录另有同一插件，需由原安装方式处理重复安装。

## 卸载

关闭 Revit，双击 `Uninstall.bat`。卸载会移除本版本程序，并在有备份时恢复此前的插件清单。模型、草稿、日志和清单备份保留。若文件被其他程序占用，关闭占用程序后可重新运行卸载。

草稿与日志位于 `%LOCALAPPDATA%\CaissonRebar\R36.Share1`。这些运行后生成的文件可能含使用者自己的模型路径和参数，交付包不包含开发机运行日志。

## License / 许可

Copyright 2026 The CaissonRebar Authors. 本目录的项目自有内容统一采用 [Apache License 2.0](../LICENSE)，见 [版权声明](../NOTICE) 和 [许可范围](../docs/LICENSING.md)。
