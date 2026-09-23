# UsageLoom 0.8.30

## 更新内容

- Codex／Claude 切换按钮移至页面固定标题区；滚动概览、模型与项目或 Session 明细时仍可切换。删除重复的统计标题与说明，释放内容空间。
- Claude 概览现在按 Codex 的卡片结构展示本机 Token、Session、完成回复、缓存 Token、Token 趋势、模型分布、最近 Session 与 Token 构成。两种 AI 的趋势和模型图表使用相同样式。
- 未启用或未发现可识别的 Claude Code 本机日志时，统计数值显示为未取得；不会把缺失当作零，也不会从套餐额度百分比推算 Token。

## 使用与边界

Claude 概览的 Token 统计只覆盖设置中启用且当前仍存在的本机 Claude Code JSONL 日志，不代表 Claude Desktop Chat、Projects、Cowork 或个人 Pro 的全账号 Token。套餐额度仍由 Claude 桌面缓存单独显示，两种数据不相互换算。Session 仅使用匿名标识；不保存或展示聊天正文。

## 验证范围

运行核心回归和桌面界面冒烟测试，覆盖数据源切换、固定按钮、不同窗口宽度与无日志状态。尚未取得用户当前普通 Chat／Projects／Cowork 的真实 Token 日志，因此这些活动不显示 Token 明细。未在用户电脑执行覆盖安装。

## 升级

可在软件内检查更新。手动安装时，将同版本 MSI 和 `UsageLoom-install.cmd` 放在同一目录，从托盘退出 UsageLoom 后运行安装入口并确认 UAC。无需卸载或清理数据。
