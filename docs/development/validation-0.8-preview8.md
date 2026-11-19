# 路径显示与高级工具（0.8.0-preview.8）

日期：2026-11-03。此前累计修改已先提交为 13114db。

## 修改

- 所有桌面端云端路径显示去掉分隔符两侧的排版空格，例如 OneDrive/HarborBackup/archives。
- 覆盖设置、账户浏览/目标预览、项目列表和编辑器、取回预览以及冲突详情。
- 文件夹名称中的实际空格保留，例如 OneDrive/Harbor Backup/metadata。
- 不修改配置中的真实路径、云端目录名、备份目标身份、基线及恢复事务。
  ConfigurationReader 中已有目标文本参与指纹，保持不变；桌面投影从账户及项目字段重新生成显示路径。
- 项目配置和系统配置 JSON 入口移入页面底部默认收起的高级工具，支持中英文展开/收起。
- 展开工具不保存或丢弃草稿；固定底栏帮助位置不变。

## 验证

Desktop 30 项回归测试通过。
包含：中英文及最小窗口、含空格云端仓库名、展开/收起保留草稿、配置编辑入口可见性、后台刷新不跳动。
截图 artifacts/ui-paths-advanced 已检查中文折叠和英文展开的最小窗口。
TRX：artifacts/test-results/paths-advanced/*_2026-11-03_20_53_22.trx。
没有改动正式配置，也没有自动安装或提交本轮修改。

## 发布物

artifacts/publish/HarborBackup-0.8.0-preview.8-20261103-205442/installers/HarborBackup-0.8.0-preview.8-Setup-x64.exe

安装包 53060897 字节，载荷 182058918 字节；完整私有运行时、不裁剪、未签名。
SHA-256：D9F750243DE6FF91FCE97B61A719387BE09D58931A4775BC863F9744316F518B。
发布 CLI 完整 --help 输出与退出码验证通过，未替换正式安装。
