# 项目缘起

UsageLoom 的出发点不是再做一个单一的 Token 计数器，而是把两种非常有价值、通常彼此分离的能力放到一起。

我很喜欢 [`zJay26/codex-usage`](https://github.com/zJay26/codex-usage) 的本地分析设计。它能帮助用户理解一台计算机上的 Token 分别花在了哪个模型、项目、Session、Agent 和日期，并提供谨慎的 API 等价费用说明，而不会把估算费用误解成订阅账单或账号额度。

我也很喜欢 [`Nirlep5252/CodexBarWindows`](https://github.com/Nirlep5252/CodexBarWindows) 的额度展示方式。它把 5 小时、每周等额度窗口的已用或剩余比例和重置时间展示得很直观，并体现了在统一界面中覆盖多个 AI 编程 Provider 的方向。

历史趋势的后续布局还参考了 [`jlcodes99/cockpit-tools`](https://github.com/jlcodes99/cockpit-tools) 公开界面中清晰的时间范围、摘要指标和趋势交互层级。UsageLoom 只借鉴这种信息组织思路，以 WinUI 3 独立实现；不包含该项目的源码、CSS 或品牌素材。

UsageLoom 希望把这些互补能力带到同一套体系中：

1. 回答“本机的 Token 和费用主要用到了哪里”；
2. 回答“当前账号的额度还剩多少、什么时候重置”；
3. 始终区分本地历史与账号实时额度，不把不同来源的数据混为一谈；
4. 通过 Provider Adapter 支持 Codex，并为 Claude、Cursor、Grok、OpenCode 等工具保留扩展空间；
5. 在独立 Windows 桌面与系统托盘中提供统一体验，标准化数据模型与 Provider 读取逻辑保持分离。已有插件和网页面板仅作为早期技术原型保留，不作为正式产品形态。

## 独立实现边界

这里所说的“结合”是产品目标、功能方向和交互理念的结合。UsageLoom 当前代码是独立实现，不是上述项目源码的合并、移植、fork 或再发行。

提及这两个项目是为了诚实说明设计灵感并表达感谢，不暗示原项目、其作者或维护者参与、赞助、支持或认可 UsageLoom，也不表示他们为 UsageLoom 承担责任。

如果未来需要直接使用其他项目的代码、素材或协议实现，必须先确认相应许可证和许可范围，并在[致谢与第三方声明](../THIRD_PARTY_NOTICES.md)中完整记录来源。
