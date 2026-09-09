# 发布清单

## Release 标题格式

GitHub Release 标题统一为 `UsageLoom <版本号>`，例如 `UsageLoom 0.4.5`。标题不追加中文副标题或更新摘要；功能说明、升级指南和已知限制放在下方发布正文中。

只有在公开仓库地址、维护者身份和安全联系方式确定后，才执行正式发布步骤。

Windows 桌面发布：运行核心回归、桌面冒烟，以及在 `legacy/codex-plugin` 中运行 `npm run check`。使用 build-windows.ps1 -Publish、build-msi.ps1、build-portable.ps1、build-update-feed.ps1 生成同版本产物；test-msi-package.ps1 验证安装结构。四个打包/检查脚本统一读取 App 项目的 Version，不再逐个修改脚本版本。发布说明放在 `docs/releases/RELEASE-版本号.md`。审查源码和压缩包，排除个人数据、凭据、内部计划、日志与 PDB。源码提交并打标签，MSI、ZIP、update.json 与 SHA256SUMS.txt 作为 GitHub Release 附件，不把二进制提交到 Git。未完成的人工验收必须在发布说明中披露。下文 Node.js 插件打包步骤均在 `legacy/codex-plugin` 中执行，仅适用于保留的技术原型。

## 首次公开提交之前

- 确认仓库名称和所有者。
- 如有需要，把通用的贡献者身份替换为维护者姓名或组织名称。
- 只有在对应 URL 真实存在后，才向 `package.json` 添加 `repository`、`homepage`、作者链接和安全联系方式。
- 确认 UsageLoom 的名称、说明和图标不会暗示任何 Provider 的官方认可。
- 检查实际待提交文件集合，确认内部规划、逐任务开发流水、会话交接和本机数据未进入暂存区；忽略规则不能代替逐文件检查。
- 从不包含本地内部文档的源码副本运行公开自检和测试，确保公开 README 链接不依赖被忽略的文件。
- 检查所有致谢和第三方许可证声明。
- 搜索整个项目树，确认不存在凭据、Cookie、个人路径、真实任务标题或会话数据。
- 确认所有面向用户、维护者和贡献者的说明文档均为中文；标准英文许可证原文除外。
- 确认 `LICENSE.zh-CN.md` 明确标注为非正式参考译文，并链接到英文 `LICENSE`。

## 每次发布之前

1. 更新 `CHANGELOG.md`，并同步修改 `package.json` 与 `.codex-plugin/plugin.json` 中的语义化版本号。
2. 依据各 Provider 的官方资料重新核对价格和生效日期。
3. 执行 `npm ci --ignore-scripts` 和 `npm run check`。
4. 如果 Codex Adapter 有变化，在本机运行 `npm run smoke`；不要保存或发布其输出。
5. 运行插件清单校验和 Skill 校验。
6. 执行 `npm run package`。
7. 使用 `tar -tf dist/usage-loom-<version>.zip` 检查压缩包中的每一个文件。
8. 将压缩包解压到一个新的临时目录，并从解压后的内容重新执行必要校验。
9. 确认压缩包中没有 `.git`、测试、缓存、日志、环境文件、凭据、本机数据或已有的 `dist/` 产物。
10. 确认压缩包同时包含英文 `LICENSE`、中文参考译文 `LICENSE.zh-CN.md`、项目缘起和第三方声明。
11. 计算 ZIP 的 SHA-256 摘要并记录结果。
12. 为经过审查的提交创建 `v<version>` 标签，把 ZIP 和校验值附加到 GitHub Release。

自动生成的 `dist/` 产物应继续被 Git 忽略，不应提交到仓库历史中。

## 发布后检查

- 从 Release 页面下载 ZIP，核对 SHA-256。
- 确认压缩包可以独立解压，且所有清单路径都存在。
- 确认安装说明、版本号和变更日志与发行包一致。
- 如果发现凭据或私密数据，立即撤下发行产物、轮换相关凭据，并按照安全响应流程处理。
