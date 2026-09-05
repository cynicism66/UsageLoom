# 致谢与第三方声明

UsageLoom 受到两个相互独立的公开 GitHub 项目启发。它们分别解决了 AI 编程工具用量可见性的两个互补问题：

- [`zJay26/codex-usage`](https://github.com/zJay26/codex-usage) 启发了本地优先的 Codex JSONL 历史归因：按模型、项目、Session、Agent 和日期分析用量，并谨慎区分 API 等价费用估算、订阅账单与账号额度。经核对，该项目以 MIT License 发布。
- [`Nirlep5252/CodexBarWindows`](https://github.com/Nirlep5252/CodexBarWindows) 启发了紧凑的实时额度展示：5 小时、每周及其他额度窗口，剩余比例、重置时间，以及面向多个 AI 编程 Provider 的发展方向。

UsageLoom 希望把这些互补的产品理念带到同一种体验中：详细的本地用量归因，加上实时账号额度展示，并为未来的 Provider Adapter 提供中立的扩展路径。

## 独立实现

UsageLoom 是独立实现，不是上述任一项目的 fork、源码合并、移植或捆绑发行版。当前仓库不包含上述项目的源码或素材。致谢和灵感说明不表示原项目或其维护者参与、赞助、认可、支持 UsageLoom，也不表示他们为 UsageLoom 承担责任。

本仓库当前没有第三方 npm 运行时依赖。

在 **2026-09-04** 的审查中，未能从 `Nirlep5252/CodexBarWindows` 仓库确认明确许可证，因此本声明不对该项目的许可证作出判断，只把它列为产品设计灵感来源。如果 UsageLoom 将来需要使用上游源码或素材，必须先确认许可范围、满足署名要求，并在合并前更新本文件。

今后新增 Provider Adapter 时，必须在发布前记录所有随附依赖、复制的素材、复制的源码片段、协议参考资料及其适用许可证。
