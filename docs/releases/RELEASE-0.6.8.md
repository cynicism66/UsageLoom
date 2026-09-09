# UsageLoom 0.6.8

## 更新检查可靠性

- 优先读取 GitHub Release 下载区的 `update.json`，不消耗公共 REST API 的查询配额。旧版未附清单、清单不可达或无效时尝试 API。
- 两条路径共用版本、项目下载地址、文件大小和 SHA-256 校验，不因使用备用路径放宽校验。
- 成功检查的更新信息在本地缓存最多 7 天；检查失败时可继续尝试下载此前已知的新版本，并重新校验安装包。
- 网络或限流错误显示中英文提示；增加“打开发布页”入口。整个 GitHub 不可达时，备用机制无法代替网络连接。

## 升级与发布要求

- 下载附件：`UsageLoom-0.6.8-win-x64.msi`（安装版）或 `UsageLoom-0.6.8-win-x64.zip`（免安装版）。
- `update.json` 供应用检查版本并验证下载，`SHA256SUMS.txt` 供手动核对两个安装包。
- 137 项回归测试、窗口启动检查、MSI 结构与 ZIP 清单检查通过；未执行真实 MSI 跨版本安装验收。

0.6.7 受限流影响时，请手动下载并安装本版，保留 `%LOCALAPPDATA%\UsageLoom\user-data\`。退出旧版后使用 MSI 或解压 ZIP。

发布本版及以后版本时必须先生成 MSI、ZIP，再运行 `scripts/build-update-feed.ps1`，将 `artifacts/release-<版本>/update.json` 一同上传到对应 GitHub Release，最后设置为最新正式版。不要在生成清单之后替换安装包；修改包后须重新生成清单。

未发布到 GitHub 前，公共最新下载地址不会提供本地生成的清单。本次代码改动不会自动修改旧 Release。安装包仍未签名，摘要是完整性校验，不是独立代码签名。
