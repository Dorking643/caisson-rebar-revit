# GitHub 上传与版本发布

本包包含正式名称、七位作者署名、Apache-2.0 许可证、版权声明和根目录 `CITATION.cff`。项目仓库为 [Dorking643/caisson-rebar-revit](https://github.com/Dorking643/caisson-rebar-revit)。以下为后续版本维护和发布步骤；标签、Release 和线上检查状态以 GitHub 实际页面为准。

## 上传内容

将解压后仓库目录内的文件作为 GitHub 仓库根目录上传，保留 `.github`、`.gitignore` 和 `.gitattributes` 等隐藏项。使用 Git 客户端添加整个目录可以一并识别这些文件。不要只把整个 ZIP 作为源码上传。

源码、文档、安装脚本和三份空壳模型均已包含。Revit 自动备份、个人日志和编译缓存由忽略规则排除。

## 发布步骤

1. 使用现有 `Dorking643/caisson-rebar-revit` 仓库维护公开版本；保留现有 README、LICENSE 和忽略规则。
2. 仓库地址已写入 `CITATION.cff`。新增版本时同步更新软件版本、公开日期及实际下载地址。完整元数据见 [PUBLICATION_METADATA.md](../PUBLICATION_METADATA.md)。
3. 按 [编译与检查说明](BUILD.md) 执行适用检查。若对文件进行了修改，先执行 `scripts/Update-Manifest.ps1`，再执行 `scripts/Verify-Repository.ps1`。
4. 用自己的 Git 提交身份将目录内容提交、推送到目标仓库，等待仓库中的 GitHub Actions 检查结果。
5. 发布时将实际公开日期填入 `CITATION.cff` 的 `date-released` 字段；如获得 DOI，再添加 `doi`。提交最终元数据并重新更新清单、复核和打包，使 ZIP 与待发布提交一致。
6. 为当前项目版本创建 `v36.1.0` 标签；若标签已存在，核对版本，不覆盖旧发布。
7. 创建 **CaissonRebar v36.1.0** Release，附上对应完整 ZIP 和 `.sha256` 校验文件，发布说明可使用 [Release 草稿](releases/R36.Share1.md)。

GitHub Release 可附带二进制下载文件，见 [GitHub 官方说明](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。根目录 `CITATION.cff` 用于提供引用入口，见 [引用文件说明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-citation-files)。

## 当前检查记录

本地编译、软件与模型检查记录见 [VALIDATION.md](VALIDATION.md)。本轮仅更新发布元数据，源码、安装 DLL 和模型保持原验证字节。GitHub Actions 的线上结果以发布后实际运行日志为准。

本地分发 ZIP 不包含 `.git` 历史。维护仓库时使用你自己的账号、提交身份及仓库权限设置。
