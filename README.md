# UsageLoom

UsageLoom 是一个以 Provider 为中心、优先在本地运行的 AI 用量与额度中心。它计划统一展示不同 AI 编程 Provider 的 Token 用量、额度窗口、重置时间和费用估算。

当前仓库处于早期开发阶段，`0.1.0` 是保留的 JavaScript 技术原型，提供第一个 Provider Adapter：Codex。正式产品目标是基于 C#、.NET 10 和 WinUI 3 的独立 Windows 桌面／系统托盘应用；Windows 客户端尚未实现，当前没有可安装的 Windows 正式版。

[源码仓库](https://github.com/cynicism66/UsageLoom) · [问题与建议](https://github.com/cynicism66/UsageLoom/issues) · [私密安全报告](https://github.com/cynicism66/UsageLoom/security/advisories/new)

> UsageLoom 是独立的开源项目，与 OpenAI 或其他 Provider 不存在官方隶属关系。Codex、OpenAI 以及其他产品名称和商标归各自权利人所有。

## 项目缘起

UsageLoom 受到两个公开 GitHub 项目的启发：

- [`zJay26/codex-usage`](https://github.com/zJay26/codex-usage)：我很喜欢它对本地 Codex 历史用量的精细归因设计，可以按模型、项目、Session、Agent 和日期分析 Token 去向，并谨慎说明 API 等价费用。
- [`Nirlep5252/CodexBarWindows`](https://github.com/Nirlep5252/CodexBarWindows)：我也很喜欢它对 5 小时、每周等额度窗口、剩余比例和重置时间的紧凑展示，以及向多个 AI 编程 Provider 扩展的方向。

UsageLoom 希望把这两类互补体验带到同一个工具中：既能回答“本机 Token 用到了哪里”，也能回答“当前账号还剩多少额度、什么时候重置”，并逐步发展成 Provider 中立的 AI 用量中心。

这里所说的“结合”是产品功能和设计理念的结合，不是把上述两个仓库的源码直接拼接。UsageLoom 采用独立实现；原项目及其维护者不参与 UsageLoom，也不表示他们赞助、支持或认可本项目。完整说明见[项目缘起](docs/PROJECT_ORIGINS.md)和[致谢与第三方声明](THIRD_PARTY_NOTICES.md)。

## 当前原型功能

- 通过本机 Codex CLI 的 app-server RPC 读取当前账号的实时额度窗口。
- 展示 5 小时、每周以及服务端返回的其他额度窗口、剩余比例和重置时间。
- 扫描本地 Codex JSONL 会话日志，按日期、模型、项目和 Agent 汇总 Token。
- 分别统计输入、缓存输入、缓存写入、输出和推理 Token。
- 根据可审计的价格目录估算 API 等价费用，并报告价格覆盖率。
- 提供只读 MCP 工具和自包含的 MCP Apps 仪表盘。
- 实时额度查询失败时，仅在明确标注来源的前提下使用本地 JSONL 快照作为回退。

## 隐私原则

UsageLoom 默认在本机运行：

- 不读取或保存 Codex `auth.json`、access token 或 refresh token。
- 不发起模型请求，不执行登录、退出、切换账号或其他账号变更操作。
- 不读取提示词、回复正文、推理文本或工具输出。
- 不启动 HTTP 服务，不监听端口，不依赖 CDN，不包含遥测。
- 日志扫描只解析 `session_meta`、`turn_context` 和 `event_msg.token_count` 记录。
- 默认不读取任务标题；只有用户明确请求标题或按标题筛选时才读取索引。
- 诊断信息在返回 MCP 客户端前会截断，并清理常见凭据、电子邮箱和用户目录。

完整说明见[隐私设计](docs/PRIVACY.md)和[安全政策](SECURITY.md)。

## 账号额度与本地用量不是同一件事

UsageLoom 会明确区分三类数据：

- **账号额度**：实时查询成功时，来自当前已登录的 Codex CLI app-server。
- **本地用量**：来自本机仍然存在的 JSONL 历史记录，可能包含此前登录过的其他账号产生的会话。
- **API 等价费用**：使用公开 API 单价计算的估算值，不是 ChatGPT/Codex 订阅账单，也不能用于反推订阅额度。

如果 Provider 不提供明确的 Token 额度上限，UsageLoom 不会猜测“还剩多少 Token”，只展示 Provider 实际返回的百分比和重置时间。

## MCP 工具

| 工具 | 用途 |
| --- | --- |
| `get_combined_report` | 合并实时额度与本地 Token 统计，并可渲染仪表盘 |
| `get_live_quota` | 获取实时额度；只有实时查询失败时才扫描本地日志作为回退 |
| `get_local_usage` | 按日期、模型、项目和 Agent 汇总本地用量 |
| `get_recent_sessions` | 查看最近或用量最大的会话；默认隐藏任务标题 |
| `diagnose_usage_sources` | 检查本地目录、扫描状态和隐私边界 |

## 当前原型环境要求

- Node.js 20 或更高版本
- 已安装 Codex CLI；实时额度查询还需要在 Codex CLI 中登录
- Windows、macOS 或 Linux；当前打包脚本使用 PowerShell

项目当前没有第三方 npm 运行时依赖。

## 本地开发

```powershell
npm install --ignore-scripts
npm run check
```

如需对自己的 Codex 安装执行只读集成检查：

```powershell
npm run smoke
```

`smoke` 会扫描本机 Codex 日志并尝试查询额度，但不会修改账号或会话数据。不要提交、发布或分享它的输出。CI 不运行该命令，因为 CI 环境中没有本地 Codex 数据源。

## 原型打包

```powershell
npm run package
```

产物写入 `dist/usage-loom-<version>.zip`。打包脚本采用逐文件白名单，只包含清单、源码、Skill、仪表盘、许可证和说明文档；不会包含 `.git`、测试、缓存、临时目录或本机数据。完整流程见[发布清单](docs/RELEASING.md)。

ZIP 是原型的 Codex 插件归档，不是 Windows 自包含便携版，也不是正式产品安装包；它不包含 Node.js 或 Codex CLI 的二进制文件。

## 项目结构

```text
.codex-plugin/          Codex 插件清单
assets/                 自包含的 MCP Apps 仪表盘
server/                 MCP Server 入口
skills/                 Codex Skill
src/                    Provider Adapter 和共享逻辑
test/                   Node.js 自动化测试
scripts/                校验、集成检查和打包脚本
docs/                   架构、隐私、缘起与发布文档
```

当前原型架构见[架构设计](docs/ARCHITECTURE.md)。近期开发重点是最小 WinUI 3 托盘、当前账号只读额度和正确的本地 Token 统计；首个 Windows 版本只完整支持 Codex，不管理登录或凭据。其他 Provider 留到后续，不作为当前已实现能力。

公开仓库保留源码、合成测试及面向用户和贡献者的说明文档；个人开发流水、会话交接和内部任务安排不属于构建依赖。无需这些本地材料即可运行项目自检和测试。

## 价格数据

价格目录最近一次人工核对日期为 **2026-09-04**，单位为 USD / 1M tokens。价格可能变化；每次发布新版本前都应重新核对 Provider 的官方来源。

- [OpenAI 模型价格比较](https://developers.openai.com/api/docs/models/compare)
- [ChatGPT Work 与 Codex Rate Card](https://help.openai.com/en/articles/20001415)

未知或没有公开价格的模型不会被强行套用价格，而是降低价格覆盖率。

## 已知限制

- 本地 JSONL 只能代表当前计算机上仍然存在的历史数据。
- 本地历史无法可靠地按照当前登录账号重新归属。
- Codex CLI 更新后，app-server RPC 或日志格式可能发生变化。
- 实时查询失败后的本地额度快照可能已过期，也可能不属于当前登录账号。
- 当前只实现 Codex Provider；其他 Provider 是后续方向，不属于 `0.1.0` 已完成功能。

## 参与贡献

请先阅读[参与贡献](CONTRIBUTING.md)。安全问题请遵循[安全政策](SECURITY.md)，不要在公开 Issue、Pull Request 或聊天中提交 Token、Cookie、完整日志或未经处理的用户路径。

## 许可证与致谢

本项目采用标准英文 [MIT License](LICENSE)。[MIT 许可证中文参考译文](LICENSE.zh-CN.md)仅用于帮助理解，如与英文原文存在差异，以英文原文为准。

项目背景见[项目缘起](docs/PROJECT_ORIGINS.md)，灵感来源和第三方边界见[致谢与第三方声明](THIRD_PARTY_NOTICES.md)。
