# 致谢与第三方声明

UsageLoom 受到三个相互独立的公开 GitHub 项目启发。它们从本地用量、账号额度和趋势信息层级等方面提供了参考：

- [`zJay26/codex-usage`](https://github.com/zJay26/codex-usage) 启发了本地优先的 Codex JSONL 历史归因：按模型、项目、Session、Agent 和日期分析用量，并谨慎区分 API 等价费用估算、订阅账单与账号额度。经核对，该项目以 MIT License 发布。
- [`Nirlep5252/CodexBarWindows`](https://github.com/Nirlep5252/CodexBarWindows) 启发了紧凑的实时额度展示：5 小时、每周及其他额度窗口，剩余比例、重置时间，以及面向多个 AI 编程 Provider 的发展方向。
- [`jlcodes99/cockpit-tools`](https://github.com/jlcodes99/cockpit-tools) 的公开趋势界面启发了时间范围、摘要指标与历史图表的信息层级。UsageLoom 没有复制其源码、CSS 或品牌素材。

UsageLoom 希望把这些互补的产品理念带到同一种体验中：详细的本地用量归因，加上实时账号额度展示，并为未来的 Provider Adapter 提供中立的扩展路径。

## 独立实现

UsageLoom 是独立实现，不是上述任一项目的 fork、源码合并、移植或捆绑发行版。当前仓库不包含上述项目的源码或素材。致谢和灵感说明不表示原项目或其维护者参与、赞助、认可、支持 UsageLoom，也不表示他们为 UsageLoom 承担责任。

本仓库当前没有第三方 npm 运行时依赖。

Windows 桌面发行包包含 Microsoft .NET 10、Windows App SDK、Microsoft.Data.Sqlite 及 SQLitePCLRaw/SQLite 运行组件；这些组件不受 UsageLoom 的 MIT 许可证替代。随包提供的 THIRD-PARTY-LICENSES 目录保留依赖包提供的许可与声明。WiX 6 仅用于构建安装包。应用图标由本项目的 build-icon.ps1 绘制，不使用上述参考项目的品牌素材。

- Microsoft.Data.Sqlite 10.0.11：MIT，Copyright Microsoft Corporation。
- SQLitePCLRaw core/provider/bundle/lib 2.1.12：Apache-2.0，Copyright 2014-2024 SourceGear, LLC；许可证来自上游 v2.1.12 标签。
- SQLite 原生数据库引擎为 public domain；封装及发行构建遵循 SQLitePCLRaw 的许可声明。

在 **2026-09-04** 的审查中，未能从 `Nirlep5252/CodexBarWindows` 仓库确认明确许可证，因此本声明不对该项目的许可证作出判断，只把它列为产品设计灵感来源。如果 UsageLoom 将来需要使用上游源码或素材，必须先确认许可范围、满足署名要求，并在合并前更新本文件。

在 **2026-09-07** 的专项审查中，GitHub 未能为 `jlcodes99/cockpit-tools` 返回许可证信息，仓库根目录也未列出许可证。因此本次只参考公开界面的产品思路并独立实现；不把“公开可读”误写为允许复制或再发行。

今后新增 Provider Adapter 时，必须在发布前记录所有随附依赖、复制的素材、复制的源码片段、协议参考资料及其适用许可证。
