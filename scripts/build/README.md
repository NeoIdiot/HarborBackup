# build

职责和命令见 ../../docs/development/verification.md。生成文件全部写入 artifacts。

`artifact-retention.json` 设置自动产物保留批数（默认 2）。发布、打包、完整测试和安装验收成功后自动执行；`Clean-Artifacts.ps1 -WhatIf` 可预览，去掉 `-WhatIf` 立即清理。NuGet、工具、诊断与截图不在自动删除范围内。
