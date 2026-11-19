# 0.5.1 输入框文字裁切修复

## 范围

仅修复桌面单行输入框的文字高度，不变更备份、配置、状态协议或元信息同步。此前目录重构已提交为 `4c8bbb0`。

## 原因与修复

圆角 InputFrame 去掉 TextBox 的原生边框后，仍依赖原生 AutoSize。单行原生控件的自动高度不足以容纳 GDI 绘制的占位文字；占位文字另有顶部偏移。回归测试中，10 pt 字体的原生客户区为 17 px，实际占位文本至少需要 21 px，因此字底被切掉。

单行 TextBox 关闭原生 AutoSize，由外层根据 TextRenderer 的实际单行文字高度、原生 PreferredHeight 和少量 DPI 留边计算输入区域。字体继承变化和句柄创建时重新测量；仍使用缓存的 GetPreferredSize，避免恢复此前 TableLayoutPanel 反复测量导致的设置页延迟。下拉框、日期框、数值框保留原生行为。

## 验证

- 新增 InputFrameTests，先复现失败再验证修复，覆盖 10、14、18 pt 及恢复 10 pt。
- HostTests 对真实运行记录、待处理问题等页面的中英文占位文本检查客户区高度及容器边界。
- Release 桌面测试 18/18 通过，包含设置页性能、轮询稳定性、语言切换和草稿保留；构建无警告。
- 渲染产物：`artifacts/ui-font-fix`。检查 10 pt 与 18 pt 输入文字截图。
- DrawToBitmap 的 WM_PRINT 不绘制原生占位文字，所以占位文字使用实际测量断言，截图使用相同字符的输入文字。不把这项验证宣称为真实显示器跨 DPI 切换验收。

## 发布

版本提升为 0.5.1，继续使用完整私有 .NET 运行时，不启用裁剪。不自动替换用户正式安装。

发布目录：`artifacts/publish/HarborBackup-0.5.1-20261028-213813`。
安装包：`installers/HarborBackup-0.5.1-Setup-x64.exe`。
隔离安装验证通过，报告：`artifacts/install-validation/e49b012c81774338929f4aeb7df5e5e1/validation.json`。验证实际安装后的 CLI 备份、覆盖保护、恢复与桌面私有运行时启动，以及程序运行时阻止升级、覆盖安装保留数据、卸载保留数据及重装读取数据。正式包与隔离包使用同一发布载荷；正式安装注册和快捷方式本轮未在用户环境重测。
