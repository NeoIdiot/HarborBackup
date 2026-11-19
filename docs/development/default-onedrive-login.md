# 默认 OneDrive 登录应用

## 方案与状态

采用 HarborBackup 自己的 Microsoft Entra 公共桌面应用。维护者注册一次，发布物携带公开 Client ID；普通用户点击“连接 OneDrive”通过系统浏览器授权。自定义应用移到高级设置。CLI cloud-connect 不再强制 --client-id。

已由用户提供 HarborBackup 应用 ID f4c26517-4f12-4120-a3cc-5d0dac90eb57，并配置到 packaging/windows/release.json。截图显示注册类型为个人 Microsoft 账户，默认应用使用 consumers 登录入口。用户已报告赋予读写权限，权限类型及 localhost 回调仍需在后台确认；真实授权、账户往返与双机验收尚未完成。

## 维护者配置

当前内置应用支持“个人 Microsoft 账户”，使用 consumers authority。自定义应用保留 common authority；发布参数替换内置 ID 时也须使用支持个人账户的注册，如需更改内置账户类型应同步修改并验证 AudienceFor。平台选择移动和桌面应用，重定向 URI 为 http://localhost，Graph 委托权限 Files.ReadWrite。不创建、收集或嵌入客户端密钥。

将公开应用 ID 写入 packaging/windows/release.json 的 oneDriveClientId，或发布时指定：
    ./scripts/release/Publish.ps1 -IsolatedProfile -OneDriveClientId <应用ID>

脚本将 ID 编译进 OneDrive 程序集元信息，并记录到安装根目录 release.json。运行时从程序集读取，用户修改 release.json 不会更改登录身份。缺失、无效或全零 ID 阻止正常发布；开发隔离验证可显式传入 -AllowUnconfiguredOneDrive。GUID 格式检查不等于微软已验证该应用注册有效。

直接 dotnet build 可用 -p:HarborBackupOneDriveClientId=<应用ID>。普通未配置开发构建允许编译与离线测试，但不能默认登录。

## 身份和恢复

连接成功后保存实际使用的 Client ID。后台任务与断开操作使用绑定时的身份及对应加密令牌缓存；升级更换默认 ID 不会静默更换既有账户身份。高级设置会显示与新默认 ID 不同的已有应用，可明确选择重新授权。切换应用前拒绝存在未完成事务的情况。

没有新增网络服务、rclone、WebView 或运行时依赖；保留当前 MSAL 浏览器 OAuth+PKCE、DPAPI 本机缓存及 Graph 传输。

## 验证范围

自动化验证默认/自定义选择、错误 ID、缺失默认配置、带事务的应用切换拦截，以及中英文新用户/已有账户界面。发布校验需验证缺失及无效 ID 的拒绝路径。获得真实应用 ID 后再进行真实登录和安装验收。

2026-11-03 离线验证：Release 构建零警告、零错误；Core 178、Windows 45、Desktop 26、Integration 13，共 262 项通过。桌面测试覆盖中英文新用户及既有自定义账户，控件截图位于 artifacts/ui-default-login。首次新增用例因夹具自动创建账户失败，修正其初始化为未连接后，桌面全套与集成测试通过。

发布脚本实际拒绝缺失 ID、非 GUID、全零 GUID；合成 ID 的程序集元信息注入与随后普通构建清除均已验证，未使用合成 ID 登录。未生成或安装新版安装包，未修改正式安装及真实账户数据。
