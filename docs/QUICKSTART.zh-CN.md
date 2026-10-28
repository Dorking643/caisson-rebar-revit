# 从空壳模型开始

[English](QUICKSTART.md) · [返回首页](../README.md)

本指南对应 **R36.Share1 / Revit 2025**。下列数值是用于学习界面操作的教学输入。三个交付 RVT 保持未配筋状态；请在工作副本中操作。

## 准备

1. 完整解压仓库或下载包，关闭 Revit，按 [安装说明](../installer/README.md) 安装插件。
2. 复制 `models/Wall.rvt` 或 `models/Slab.rvt` 到自己的工作目录，再用 Revit 2025 打开。
3. 从功能区进入 **Caisson Reinforcement → Model Creation → Wall** 或 **Slab**。
4. 单独井壁模型使用 `Wall`，单独底板模型使用 `Slab`；组合模型 `Combined.rvt` 可分别使用两个入口。保留宿主标记 `CJ-WALL`、`CJ-SLAB`。

## 示例一：沿井壁高度布置三个圆环

在 `Wall.rvt` 工作副本中打开 **Wall**。

1. 在左侧 **Bar Types** 选择 **HRB400 Φ16**。
2. 点击 **Cover**，将 **Inner / Outer / Top / Bottom** 均设为 **40 mm**，点击该栏的 **Apply**。
3. 点击工具栏 **Ring**，在中间 **Radial Section** 内单击，放置一个圆环；在右侧 **Placed Bars** 中选中它。
4. 在 **Position · mm** 输入下表坐标，点击该区的 **Apply**。
5. 在 **Distribution** 勾选 **Enable**，设置下表布置参数，再点击该区的 **Apply**。

| 区域 | 字段 | 教学输入 |
| --- | --- | ---: |
| Position · mm | x | 225 |
| Position · mm | y | 300 |
| Distribution | Direction | Along y |
| Distribution | Spacing | 150 |
| Distribution | Count | 3 |

井壁剖面坐标的原点在内壁顶角，x 向外、y 向下。本例内半径为 2250 mm，所以圆环中心线半径为 **2475 mm**；三圈相对井顶的 Z 坐标为 **−300、−450、−600 mm**。`Count` 包含最初放置的那一圈。平面中三圈同半径重叠，剖面中应看到三个位置。

等待界面检查完成，确认下方数量为 **3 bars**，然后点击 **Create Rebar**。完成后回到 Revit 检查工作副本中的三个圆环，并保存工作副本。

![井壁编辑器的教学输入预览](images/wall-editor-preview.png)

*R36.Share1 实际 WinForms 控件的离线预览，使用上述教学参数。*

## 示例二：在底板同一层布置三个同心圆环

在 `Slab.rvt` 工作副本中打开 **Slab**。

1. 在 **Bar Types** 选择 **HRB400 Φ16**。
2. 展开 **Cover**，将 **Top / Bottom / Side** 均设为 **40 mm**，点击该栏的 **Apply**。
3. 点击 **Ring**，在 **Plan** 中离开圆心的位置单击，放置一个圆环；在 **Placed Bars** 中选中它。
4. 在右侧 **Position · mm** 输入下表数值。底板这些参数修改后会自动更新预览，无需额外点击位置确认按钮。

| 字段 | 教学输入 |
| --- | ---: |
| Name | Tutorial Rings |
| Enabled | 勾选 |
| Elevation Z | −100 |
| Count | 3 |
| Radius | 500 |
| Radial step | 200 |

底板 Z 以中央顶面为 0，向上为正。本例应得到 **500、700、900 mm** 三个中心线半径，均位于 **Z = −100 mm**。平面中应看到三个同心圆环，下方数量应为 **3 bars / 0 pending**。

等待界面检查完成后点击 **Create Rebar**，再用 **Show Model** 查看；检查后使用 **Save Model** 保存当前工作副本，也可以通过 Revit 常规保存操作保存。

![底板编辑器的教学输入预览](images/slab-editor-preview.png)

*R36.Share1 实际 WinForms 控件的离线预览，使用上述教学参数。*

## 常用操作

- 鼠标滚轮缩放，中键拖动平移，双击画布适配视图。
- `Esc` 结束放置并切回选择；`Delete` 删除选中草稿或群组；`Ctrl+Z` 撤销编辑器中的修改。
- 修改保护层后应点击 **Apply**。按钮暂时不可用时，先查看窗口下方的检查状态和右侧提示。
- 教程示例可以只在一个宿主上完成，不需要把三个 RVT 合并。

本次已使用交付 DLL 对两组教学输入进行离线几何检查，各得到 3 条钢筋路径、0 个阻断项；未在交付 RVT 中写入钢筋。图片展示离线表单，原生 Revit 创建按上述步骤在工作副本中执行。[预览与输入检查记录](images/preview-verification.json)
