# build

职责和命令见 ../../docs/development/verification.md。生成文件全部写入 artifacts。

`artifact-retention.json` 设置自动产物保留批数（默认 2）。发布、打包、完整测试和安装验收成功后自动执行；另保留最近成功结果、最后包含正常安装包的发布批次及当前操作使用的批次。内部 `PreservePaths` 支持批次中的文件或子目录，保护其所属批次。

```powershell
./scripts/build/Clean-Artifacts.ps1 -WhatIf
# 临时只保留最近一批，额外保护的批次仍保留。
./scripts/build/Clean-Artifacts.ps1 -KeepCount 1
```

清理范围仍限于 publish、install-validation 和 test-results 的已知批次，不扩展到 NuGet、工具、诊断、截图、未知目录或运行数据。固定安装包入口 `artifacts/installers` 不在批次清理范围内；正常打包成功自动更新，也可通过 `./scripts/release/Export-Installer.ps1` 从已有正常包首次导出，再用 `Invoke-Item ./artifacts/installers` 打开。原 publish 树中的构建证据按保留策略管理。
