# 应用内更新 / In-app updates

从 0.6.8 起优先读取最新 Release 附件 `update.json`，不依赖公共 API 配额；API 仅作备用。已验证的元数据在本地保留最多 7 天，检查失败时仍可尝试已知版本下载。设置提供发布页链接。两条网络路径都不可达时不会误报“已是最新版”。发布者需在生成 MSI/ZIP 后运行 `scripts/build-update-feed.ps1`，上传对应清单。

设置中的「软件更新」从 0.6.10 起支持选择检查频率：从不自动检查、每 1/6/12 小时、每天（默认）、每 3 天或每周，选择后立即保存。仅在软件运行时按距离上次检查的间隔检查（包括失败的检查），也可随时手动检查。旧版关闭自动检查的设置继续保留。检查只请求公共发布信息，不发送账号、Token、日志或统计数据。关闭自动检查不会影响手动更新。

发现新版后显示发布说明。点击「下载更新并重启」并确认后，下载适合当前运行位置的 Windows x64 ZIP 或 MSI，显示下载进度，检查长度和 GitHub 资产 SHA-256 摘要，再正常退出并升级。没有摘要、版本或下载地址不匹配时拒绝安装。摘要用于完整性校验，并不等同于独立代码签名；安全性仍依赖 GitHub 仓库和发布账号。

- MSI：通过 Windows 卸载注册信息及安装路径识别，调用 Windows Installer 的升级事务，保留原安装位置。Windows 可能要求权限确认；取消或失败不会被报告为成功。安装回滚由 Windows Installer 处理。
- ZIP：独立 PowerShell 更新程序等待当前进程正常退出，再获取应用互斥锁。只替换 `update-manifest.txt` 中的程序文件，旧清单中已废弃的程序文件在备份后移除。其他文件保留，新包与非程序文件同名则停止。文件替换异常时恢复旧程序备份。
- `%LOCALAPPDATA%\UsageLoom\user-data\` 不属于更新目标，统计数据库、设置和周估算历史不随程序替换。
- 下载、旧程序备份、MSI 日志和 `result.txt` 保存在 `%LOCALAPPDATA%\UsageLoom\updates\<任务 ID>\`。本版不自动删除这些恢复材料，确认更新正常后可手动清理对应任务目录。清理前须确认没有更新正在进行。
- 正常替换完成后重新打开主窗口。更新失败时尽可能重新打开旧程序，设置和托盘提示更新结果。断电或强制终止更新程序不能保证自动回滚；可从任务目录的 `backup` 恢复免安装版。新版本启动后的功能故障不属于文件替换回滚范围。

完全退出应用时不会检查更新。首次获得此功能仍需手动安装包含此功能的新版本；旧版无法自行增加更新入口。开发构建不包含发布清单时，不支持免安装原位更新，须使用正式打包版本。

## English

From 0.6.10, Settings → Software updates offers automatic checks every 1/6/12 hours, daily (default), every 3 days, weekly, or never. Changes save immediately. Checks run only while the app is running; intervals start at the last attempt, including failures. Previously disabled automatic checks remain disabled. Manual checks remain available. No account or usage data is sent. Confirm “Download update and restart” to download the matching x64 asset, verify its size and GitHub SHA-256 digest, exit normally, upgrade and restart. The digest is an integrity check, not an independent code signature.

MSI installations use Windows Installer's upgrade transaction at the registered installation location. Portable builds use a separate helper, an application mutex and an explicit file manifest; unrelated files are preserved, conflicts abort the update, and replacement failures restore a backup. User data remains in its existing directory. Backups and logs remain under `%LOCALAPPDATA%\UsageLoom\updates\` for recovery. Power loss and post-launch application bugs are not covered by automatic file-replacement rollback. The first update-capable version must be installed manually.
