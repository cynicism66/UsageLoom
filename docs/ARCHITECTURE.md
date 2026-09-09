# 架构设计

UsageLoom 将 Provider 专用的数据读取逻辑与 Provider 中立的报告模型分离。

当前主产品已是 C#、.NET 10、WinUI 3 和 Windows App SDK 实现的 Windows 桌面／系统托盘应用。App 负责界面和生命周期，Core 负责解析、计价和估算，Storage 负责索引与持久化，详见[目录指南](REPOSITORY_LAYOUT.md)。

下文保留 JavaScript 原型架构作为历史参考，路径均相对于 `legacy/codex-plugin`；MCP 组件不是桌面程序的运行依赖。

项目方向受到 `zJay26/codex-usage`（详细的本地用量归因）和 `Nirlep5252/CodexBarWindows`（实时额度窗口与紧凑的多 Provider 展示）这两类互补体验启发，详见[项目缘起](PROJECT_ORIGINS.md)。这里描述的是产品目标之间的关系，不表示项目之间共享源码。

## 当前版本

`0.1.0` 版本包含一个 Provider Adapter：

```text
Codex CLI / JSONL 日志
        |
        v
Codex Adapter（src/providers/codex/）
        |
        v
标准化用量、额度窗口、重置时间和费用估算
        |
        +--> MCP 工具（server/index.mjs）
        +--> MCP Apps 仪表盘（assets/usage-dashboard.html）
```

当前实现有意保持只读。它不管理凭据，不切换账号，不发起模型请求，也不执行任何额度重置或消耗操作。

## 组件职责

| 组件 | 职责 |
| --- | --- |
| `src/providers/codex/usage.mjs` | 发现并解析本地 Codex JSONL，生成按日期、模型、项目、Agent 和 Session 归因的汇总 |
| `src/providers/codex/quota.mjs` | 调用本机 Codex app-server 获取实时额度，并在失败时提供有明确来源标记的本地快照回退 |
| `src/providers/codex/pricing.mjs` | 保存经过核对的模型单价，计算 API 等价费用和价格覆盖率 |
| `server/index.mjs` | 暴露只读 MCP 工具，把 Provider 数据整理为统一响应 |
| `assets/usage-dashboard.html` | 渲染自包含的 MCP Apps 仪表盘，不依赖外部 CDN |
| `skills/usage-monitor/SKILL.md` | 告诉 Codex 如何选择工具、解释数据并遵守隐私边界 |

## 多 Provider 方向

后续 Adapter 应实现一个精简的 Provider Contract，不把 Provider 凭据泄露到共享核心中。例如：

```js
{
  id: "codex",
  capabilities: ["local-history", "live-quota"],
  readUsage(options),
  readQuota(options),
  describePrivacy(),
}
```

共享层应标准化以下内容：

- Provider 和账号标签；
- 输入、缓存输入、缓存写入、输出和推理用量；
- 额度窗口和重置时间；
- 带有价格来源的 API 等价费用估算；
- 数据来源、更新时间和警告。

各 Provider Adapter 必须彼此隔离。Claude 或 Cursor Adapter 不应读取 Codex 文件；每个 Provider 都必须说明自己使用的是本地日志、本地 CLI、官方 API、浏览器会话还是其他凭据来源。

## 数据边界

- 原始提示词、回复正文、推理文本、工具输出和凭据不会进入标准化报告。
- 账号支持的实时额度和本机历史用量始终保存在不同字段中。
- Provider 专属限制和数据新鲜度通过警告明确表达，不能被隐藏成默认假设。
- 实时查询失败时，回退数据必须显式标记为本地快照，不能伪装成实时数据。
- 新增 Provider 不应要求重命名 UsageLoom 包、MCP 命名空间或仪表盘。

## 扩展原则

新增 Provider 时，应依次完成：

1. 定义能力和数据来源。
2. 实现只读 Adapter，并保持凭据边界。
3. 映射到标准化报告模型。
4. 为合成数据和错误路径补充测试。
5. 更新隐私、安全、第三方和价格来源文档。
6. 在仪表盘中只展示 Provider 实际提供的数据，不推测不存在的数值。
