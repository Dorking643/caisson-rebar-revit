# CaissonRebar — Parametric Reinforcement Modeling for Circular Caissons

[中文说明](README.zh-CN.md) · [Quick start](docs/QUICKSTART.md) · [Installation](installer/README.md) · [Build](docs/BUILD.md)

A C# Revit 2025 add-in for defining, previewing and creating reinforcement on circular caisson wall and slab hosts. The package includes the source, a per-user installer and three anonymized concrete shell models.

Chinese name: **CaissonRebar 圆形沉井钢筋参数化建模软件**. Scope: wall and slab reinforcement modeling for Revit 2025.

## Features

- Separate **Wall** and **Slab** editors under `Caisson Reinforcement > Model Creation`.
- Parameter editing for bar shapes, diameters and group placement, with graphical previews.
- Reading host geometry and creating native Revit reinforcement through the Revit API.
- Per-user installation, drafts and logs; no administrator installation is required.
- Concrete shell examples containing **zero reinforcement bars** and no previous reinforcement design records.

## Editor previews

These images were rendered directly from the distributed WinForms assembly using the tutorial inputs. They are offline form previews, not screenshots of a live Revit modelling run.

| Wall editor | Slab editor |
| --- | --- |
| ![Wall editor offline preview](docs/images/wall-editor-preview.png) | ![Slab editor offline preview](docs/images/slab-editor-preview.png) |

## Requirements

| Purpose | Requirement |
| --- | --- |
| Open the example models | Autodesk Revit 2025 on 64-bit Windows |
| Use the add-in | Revit 2025 and its .NET 8 desktop runtime environment |
| Build the source | A .NET SDK supporting `net8.0-windows`, plus the Revit 2025 API DLLs from a local Revit installation |

The main editor windows use Windows Forms. The project also references WPF for Windows desktop/Revit integration. Autodesk application binaries and API DLLs are not included.

## Get started

Repository: [Dorking643/caisson-rebar-revit](https://github.com/Dorking643/caisson-rebar-revit). To download the full repository, use **Code > Download ZIP**. GitHub downloads include the source, installer and example models.

1. Download or clone the repository, then extract it if using a ZIP download.
2. Close Revit and run [`installer/Install.bat`](installer/Install.bat).
3. Start Revit 2025 and open a working copy of [`models/Wall.rvt`](models/Wall.rvt), [`models/Slab.rvt`](models/Slab.rvt) or [`models/Combined.rvt`](models/Combined.rvt).
4. Open **Wall** or **Slab** from the add-in ribbon, enter the reinforcement parameters and inspect the preview before creating bars.

The [quick-start guide](docs/QUICKSTART.md) describes a small example. The [model guide](models/README.md) explains host marks and geometry. The examples do not automatically load an old project reinforcement layout.

## Repository contents

```text
src/                 Add-in source and project file
tests/               Checks that do not require a live Revit session
scripts/             Build, test and integrity commands
installer/           DLL, installation and uninstallation scripts
models/              Wall, slab and combined concrete shell RVT files
docs/                Usage, build, validation and publication documentation
verification/        Recorded software and model checks
.github/             Contribution and issue templates / repository automation
```

## Version and checks

The software is **R36.Share1**, with project version **36.1.0**. This repository preparation does not change the installed DLL or the example models. See the [changelog](CHANGELOG.md), [validation record](docs/VALIDATION.md) and [file manifest](MANIFEST_SHA256.csv).

Run the available checks from the repository root:

```powershell
dotnet run --project .\tests\Share1Checks\Share1Checks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Installer.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1
```

## Feedback

Use [GitHub Issues](https://github.com/Dorking643/caisson-rebar-revit/issues). Include the software/Revit versions, steps to reproduce and a small anonymized example. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Authorship, license and citation

**Authors, in order:** Guangyao Zhang, Bojin Hao, Wenliang Ma, Lingxin Zeng, Jiaomei Wang, Qiuya Liu, and Shangfei Yang. See [AUTHORS.md](AUTHORS.md).

**Copyright 2026 The CaissonRebar Authors.** Project-owned source code, compiled programs, installation scripts, example models, images and documentation are licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) and [license scope](docs/LICENSING.md).

To cite version **36.1.0**, use the root [CITATION.cff](CITATION.cff) or the [citation guide](docs/CITATION.md). The repository URL and publication date (2026-10-29) are recorded in the citation file. No software DOI has been assigned. Publication metadata is recorded in [PUBLICATION_METADATA.md](PUBLICATION_METADATA.md).

The [publication guide](docs/PUBLISHING.zh-CN.md) explains how to upload the files and attach a downloadable package to a GitHub Release.
