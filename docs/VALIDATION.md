# Validation record / 验证说明

Preparation date: **2026-10-29**. Software: **R36.Share1**, project version **36.1.0**.

## Checks performed

| Check | Result | Evidence |
| --- | --- | --- |
| Publication metadata | Author order, title, version, Apache-2.0 and CFF 1.2.0 schema verified | [`publication-metadata-checks.json`](../verification/publication-metadata-checks.json) |
| Source file transfer | 29 original source files retained byte for byte | [`repository-layout.json`](../verification/repository-layout.json) |
| Build from the new layout | 0 errors, 0 warnings; rebuilt DLL SHA-256 matches the installer payload | [`repository-layout.json`](../verification/repository-layout.json) |
| User data paths and empty-host defaults | 7 checks passed | [`layout-share1-checks.json`](../verification/software/layout-share1-checks.json) |
| Installer and uninstaller in a redirected sandbox | 15 checks passed | [`layout-installer-checks.json`](../verification/software/layout-installer-checks.json) |
| PowerShell syntax and ignore rules | Passed | [`repository-layout.json`](../verification/repository-layout.json) |
| Final shell models | Files unchanged from the saved/reopened model verification | [`models/`](../verification/models/) |
| Tutorial form previews | Actual distributed WinForms assembly; offline rendering and tutorial geometry checks | [`preview-verification.json`](images/preview-verification.json) |

## Model verification retained

| File | Physical concrete host elements | Native reinforcement bars | Previous design DataStorage |
| --- | ---: | ---: | ---: |
| `Wall.rvt` | 1 | 0 | 0 |
| `Slab.rvt` | 1 | 0 | 0 |
| `Combined.rvt` | 2 | 0 | 0 |

The model records include save/reopen checks, host-reader results, solid-boundary fingerprints, API-visible metadata checks, used-family string checks and file hashes. Model creation used neutral save and temporary paths. Those final RVT bytes were copied unchanged into this repository layout.

## Execution status

The source build and the two offline test commands were run locally after the directory restructure. The installer tests used redirected user-data directories and did not install into the real Revit add-in directory.

The supplied GitHub Actions workflow runs the two offline checks on Windows and does not build or launch Revit. No remote workflow execution has occurred during this local preparation. The editor pictures are offline form renders. A new live installation/launch/reinforcement-creation run has not been performed for this repository preparation.

## Optional live startup record

When the owner performs a real installation run, record the Revit build, add-in version and actual result for each step below. Unchecked items are not recorded as completed tests.

- [ ] Install with Revit closed and start Revit 2025.
- [ ] Confirm `Caisson Reinforcement > Model Creation > Wall / Slab` is available.
- [ ] Open a working copy of a supplied shell and open the matching editor.
- [ ] Follow the minimal example, then record the actual native-bar count and save/reopen result in the working copy.
- [ ] Keep the repository's distributed shell models unchanged.

中文：新目录的构建、7 项路径/空壳入口检查和 15 项安装器隔离检查已在本地执行。三份模型保持原验证字节；教程图片为交付 DLL 的离线真实表单预览。GitHub 在线运行与新一次 Revit 实机安装生成过程尚未执行。
