# 文档索引 / Documentation

当前产品使用 OneDrive 账户直连。先阅读当前指南与协议；版本验证和旧设计用于了解各次变更的背景与实际测试范围。

The current product uses direct OneDrive account transport. Start with the current guides and design; versioned records document earlier changes and their actual validation scope.

## 使用 / Using HarborBackup

- [项目介绍（中文）](../README.md) · [Project overview (English)](../README.en.md)
- [许可证 / License](../LICENSE) · [变更摘要 / Changelog](../CHANGELOG.md)
- [当前中文指南](user-guide/onedrive-account.zh-CN.md) · [Current English guide](user-guide/onedrive-account.en-US.md)
- [五步界面演示（中文）](user-guide/demo.zh-CN.md) · [Five-step UI walkthrough (English)](user-guide/demo.en-US.md)：实际控件、合成项目，无真实账户传输 / Actual controls with synthetic data; no real account transfer.
- [CLI 与确认清单 / CLI and selection files](../src/HarborBackup.Cli/README.md)
- [隐私 / Privacy](../PRIVACY.md) · [支持 / Support](../SUPPORT.md) · [安全问题 / Security reporting](../SECURITY.md)

## 贡献 / Contributing

- [从零开发 / Getting started](development/getting-started.md)：SDK、首次还原、运行、工具下载和安装包验证。
- [贡献规范 / Contribution guidelines](../CONTRIBUTING.md)
- [详细验证流程 / Verification workflow](development/verification.md)
- [脱敏配置示例 / Sanitized configuration examples](../config/examples/README.md)
- [公开发布检查表 / Public-release checklist](development/open-source-checklist.md)

## 当前设计 / Current design

- [账户传输、云端布局与恢复 / Account transport, cloud layout and recovery](architecture/onedrive-account-transport.md)
- [安装布局与工程边界 / Installation layout and project boundaries](architecture/storage-layout.md)

## 历史与证据 / History and evidence

- [版本验证记录 / Version validation records](development/validation-index.md)
- [0.4 历史资料 / 0.4 historical material](history/v0.4/README.md)

`user-guide/user-guide.*.md` 是已移除的 0.7 同步目录模式说明。0.8 数字版本目录、早期共享存储设计及旧验证记录也不代表当前协议；当前规则以账户传输设计为准。历史文档中的本地产物路径可能已按保留策略清理，记录本身不会因此变为当前版本的验证结论。

`user-guide/user-guide.*.md` describes the removed 0.7 sync-folder mode. The 0.8 numbered-content layout, earlier shared-storage designs and old validation records are historical. Local artifact paths in those records may expire under retention policies; their results apply only to the recorded version and scope.
