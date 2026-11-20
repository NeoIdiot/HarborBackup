# release

职责和命令见 ../../docs/development/verification.md。生成文件全部写入 artifacts。

版本使用正式版本号（如 `0.9.0`），不添加 preview 后缀。更新时同步 `Directory.Build.props` 和 `packaging/windows/release.json`，重新还原依赖锁文件；发布清单、程序集及安装器使用相同版本。历史验收记录保留原始版本号。

正常发布需配置 HarborBackup 自己的公开 OneDrive Client ID，详见 ../../docs/development/default-onedrive-login.md。未配置开发验证需显式使用 -AllowUnconfiguredOneDrive；该构建不能默认登录。

正常 `Build-Installer.ps1` 成功后自动调用 `Export-Installer.ps1`，将校验后的包复制到固定入口 `artifacts/installers`。`-ValidationBuild` 不更新入口，原 publish 树及其报告保留为构建证据。

```powershell
# 首次整理已有包：选择 artifacts/publish 中最近的正常安装包报告。
./scripts/release/Export-Installer.ps1
# 或显式选择已有发布批次。
./scripts/release/Export-Installer.ps1 -PublishDirectory <发布目录>
Invoke-Item ./artifacts/installers
```

入口包含带版本名的 exe、原始 `package-report.json`、`SHA256SUMS.txt`、`latest.json` 和 README。导出保留报告中的原 source，不把旧包标成当前 HEAD 的产物。latest 按最近正常构建选择，显式指定时使用所选批次；来源与验收范围看报告及对应验证记录，不以入口名称推断。
