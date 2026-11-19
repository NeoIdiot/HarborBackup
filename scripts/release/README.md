# release

职责和命令见 ../../docs/development/verification.md。生成文件全部写入 artifacts。

版本使用正式版本号（如 `0.9.0`），不添加 preview 后缀。更新时同步 `Directory.Build.props` 和 `packaging/windows/release.json`，重新还原依赖锁文件；发布清单、程序集及安装器使用相同版本。历史验收记录保留原始版本号。

正常发布需配置 HarborBackup 自己的公开 OneDrive Client ID，详见 ../../docs/development/default-onedrive-login.md。未配置开发验证需显式使用 -AllowUnconfiguredOneDrive；该构建不能默认登录。
