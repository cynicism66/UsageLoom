# 参与贡献

当前主产品为 WinUI 3 桌面客户端，另保留 JavaScript 技术原型。公开开发流程不依赖个人内部规划、开发流水或会话交接文件。

桌面源码在 `src/UsageLoom.*`，目录说明见 [目录指南](docs/REPOSITORY_LAYOUT.md)。在根目录执行 `./scripts/build-windows.ps1 -Test` 测试桌面核心。

下文 npm 命令均在 `legacy/codex-plugin` 中执行，仅针对历史 JavaScript/MCP 原型。`npm run validate` 用于公开项目自检；`npm run validate:local` 仅在本机存在内部开发文档时单独校验，公开源码副本缺少这些文档时会正常跳过，不是贡献或 CI 的前置条件。

感谢你帮助改进 UsageLoom。

## 开发环境

- Node.js 20 或更高版本
- PowerShell，用于执行打包脚本
- 大多数测试只需要合成测试数据，不要求真实的 Codex 登录

安装并检查项目：

```powershell
npm install --ignore-scripts
npm run check
```

只有在需要针对自己的 Codex 安装进行本地集成检查时，才运行：

```powershell
npm run smoke
```

该命令的输出可能包含本机环境信息，绝对不要提交、发布或分享其原始输出。

## 提交 Pull Request

- 把 Provider 专用代码与标准化报告模型隔离开。
- 保持只读、本地优先和无遥测的承诺。
- 行为发生变化时新增或更新测试。
- 不要加入凭据、真实会话日志、个人路径，或包含隐私数据的截图。
- 复制资产、源码片段或新增依赖时，更新 `THIRD_PARTY_NOTICES.md` 并确认许可证允许相应用途。
- 修改原型的 `legacy/codex-plugin/src/providers/codex/pricing.mjs` 时，同时更新价格来源和核对日期；桌面定价在 `src/UsageLoom.Core/Pricing.cs`。
- 新增 Provider 时，在 `docs/PRIVACY.md` 中说明其身份验证方式和数据边界。
- 面向用户、维护者和贡献者的说明文档统一使用中文；必要的技术专有名词可以保留英文。

## 提交与发布规范

- 提交信息应清晰描述变更目的，避免把不相关修改混在同一个提交中。
- 仓库只保存源码、测试和文档，不提交自动生成的 `dist/` 压缩包。
- 发布 ZIP 由 `npm run package` 生成，经过清单、隐私和安全检查后再附加到 GitHub Release。
- 发布前按照 `docs/RELEASING.md` 逐项核对，不要跳过第三方声明和敏感信息检查。
