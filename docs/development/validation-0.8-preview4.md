# 内置个人账户应用（0.8.0-preview.4）

日期：2026-11-03。

采用用户提供的公开 Client ID：f4c26517-4f12-4120-a3cc-5d0dac90eb57。默认应用匹配截图中的个人 Microsoft 账户类型，使用 consumers authority；自定义应用保留 common authority。系统浏览器、PKCE、localhost 回调、委托 Files.ReadWrite 及 DPAPI 缓存不变。

## 验证

- Windows 46 项、登录界面与语言 4 项通过。本次仅复测受影响用例；上一轮完整回归为 262 项，新增一项 audience 选择测试。
- 发布时注入实际 ID，检查生成的程序集元信息和 payload/release.json 一致。
- 发布载荷的 CLI --help 启动成功，显示 cloud-connect 可省略 --client-id。
- 安装包构建成功，未自动覆盖正式安装，未运行本版本安装生命周期验收。
- 真实授权、Graph 权限有效性、双机往返仍未验收。用户截图的 Redirect URIs 为空，需要后台添加移动和桌面平台回调 http://localhost。用户报告已授予读写权限；截图未展示 API 权限，仍需确认是 Microsoft Graph 委托 Files.ReadWrite。

## 发布物

artifacts/publish/HarborBackup-0.8.0-preview.4-20261103-114645/installers/HarborBackup-0.8.0-preview.4-Setup-x64.exe

安装包 53,058,260 字节（约 50.60 MiB）；安装载荷 182,021,576 字节（约 173.59 MiB）。完整私有运行时未裁剪。未签名。

SHA-256：0B2C1071742061CABE2A69E11650C4C50A74AE78F6E0D24DA9313A6C788C1C58
