# 设置入口直接显示（0.8.0-preview.9）

按用户最新要求，移除高级工具折叠按钮及展开/收起逻辑。页面底部直接展示 JSON 配置文件行，保留项目配置、系统配置两个入口；路径显示修复保持不变。
中英文帮助同步更新，删除不再使用的折叠按钮文案。

现有 LanguageSwitchPreservesDraftAndFilesAndRelayoutsSettings 回归通过，覆盖中英文、普通/最小窗口、草稿保留和直接显示配置入口。截图位于 artifacts/ui-settings-direct，已检查中文最小窗口底部。
TRX：artifacts/test-results/settings-direct/*_2026-11-03_21_03_02.trx。
本次只改可逆界面呈现，未重跑与修改无关的备份/云端测试。未改正式配置、未自动替换安装，也未新增提交。

发布包：artifacts/publish/HarborBackup-0.8.0-preview.9-20261103-210322/installers/HarborBackup-0.8.0-preview.9-Setup-x64.exe。
安装包 53067234 字节，载荷 182058406 字节，未签名。发布载荷 CLI 启动通过。
SHA-256：F2DFBF1DC6FBD04226C7443BBF5140A89358FDF532AA4A9C1739850C1397115F。
