# 托盘与调度

程序采用 WinExe + WinForms ApplicationContext。手动启动打开主窗口并显示 NotifyIcon；--background 或登录启动只显示托盘。关闭主窗口隐藏到托盘，退出程序会停止本机自动备份。界面配置见 [主窗口说明](main-window.md)。

## 启动隔离环境

先按 [CLI 文档](cli.md) 准备独立源、模拟 OneDrive 根和配置，再运行：

```powershell
& .\src\HarborBackup.Tray\bin\Release\net10.0-windows\HarborBackup.Tray.exe `
  --config 'C:\HarborBackup-Demo\config\bakup-config.json' `
  --system-config 'C:\HarborBackup-Demo\config\system-config.json' `
  --onedrive-root 'C:\HarborBackup-Demo\cloud'
```

未传配置参数时，带 installed-mode.json 的安装版使用 `%LOCALAPPDATA%\HarborBackup\workspace`，开发/便携版使用 EXE 同目录；不会查找父目录。OneDrive 根依次由 --onedrive-root、本机目录偏好或本进程 OneDrive 环境变量提供。配置错误显示在主窗口，修复后自动重读。添加 --background 可测试隐藏启动。不要将示例路径直接改成生产路径来试运行。

诊断参数 --preferences 可指定独立的 UI 偏好文件，性能脚本用它避免写真实用户偏好。--shutdown 单独使用时，通过当前用户命名管道请求当前会话的实例安全退出，不强杀、不丢弃未保存草稿；发送成功不代表事务已结束，调用方仍须等待进程退出。

## 菜单

- 打开主窗口、当前状态和下次自动备份时间。
- 立即备份、打开项目页预览或取回更新。
- 完整哈希核查（只读）；核查并备份位于主窗口项目页。
- 恢复未完成事务、暂停后续自动备份。
- 退出并停止本机自动备份。

日志、普通归档、冲突、配置文件及登录启动管理集中在主窗口。

0.4.2 托盘菜单使用原生 ContextMenuStrip 配合轻量渲染：圆角外框、白色背景、轻量选中态，不绘制左侧灰色图标栏。保留独立勾选区域显示暂停状态，以及原生键盘/点击行为。高对比度环境使用系统菜单渲染，不增加外部 UI 依赖。

0.4.3 关闭托盘菜单的系统 DropShadow，去掉厚重投影；仍用细边框界定菜单范围，不绘制替代阴影。

图标区分空闲、处理中和待处理问题。主窗口给出项目动作和错误；本地处理完成始终不表示 OneDrive 已上传。正常完成不弹通知，错误/冲突提供通知并保留详情。

## 调度与并发

周期来自 schedule.intervalMinutes；缺省 10 分钟，现有配置 30 分钟就继续 30 分钟。首次触发等待 startDelayMinutes，缺省 2 分钟。运行超时使用 executionTimeLimitMinutes，缺省 120 分钟，在安全边界取消。

调度用单调时钟计时，墙上时间变化只影响展示时间。休眠后只消费一次到期触发；执行中到点跳过，不排队。手动执行不重置周期。每五秒在空闲时重读配置与本机暂停状态，周期变化从读取时重新计时。

暂停写入 state/devices/<设备ID>/control.json（schemaVersion=1），不写共享配置，不停止当前事务。手动命令仍可运行。恢复自动运行沿用原到期节奏，不主动补跑暂停期间的轮次。

托盘有独立本机会话实例 Mutex；任务使用兼容 Mutex Local\OneDriveDesktopProjectBackup，与 CLI、旧入口互斥。两者均不构成跨会话/跨设备锁。

重复手动启动通过当前用户、当前会话命名管道请求显示已有主窗口，不启动第二个调度器；后台重复启动直接退出。

退出先停止接受新操作，再请求取消，等待已开始的单文件事务完成；文件占用、慢盘和网络读取可能使等待变长。Windows 注销/强制终止不保证等待完成，下次写入或 recover 会根据现场恢复。首次启动不会停用旧计划任务。

## 登录启动

只有用户在设置页明确启用或关闭时，才修改当前用户 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 HarborBackup 项，命令中保存 EXE、--background 和配置绝对路径，不创建计划任务。不需要管理员权限。关闭登录启动删除此项。

移动发布目录后从新位置启动并重新启用。正式启用前完成 [切换流程](deployment.md)，确保旧周期触发器已停用。本轮测试未注册登录启动，也未注销或关闭真实用户会话。
