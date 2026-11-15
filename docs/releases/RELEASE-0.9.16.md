# UsageLoom 0.9.16

## 更新内容

- **在 Claude 桌面版的 Code 标签页里也能读到 Claude 额度**：桌面版的 Code 标签页不运行 Claude Code 的状态栏命令，附带的状态栏快照脚本在那里不会更新。本仓库新增一个 Claude Code mod `usageloom-quota`：每轮对话后，Claude Code 把当前账号的 5 小时和每周额度（已用百分比与重置时间）交给 mod，mod 按状态栏脚本的同一格式写入同一个快照文件，UsageLoom 照常读取。
  - 在终端的 Claude Code 中运行一次即可安装，选择用户范围后，桌面版启动的会话也会使用它：

    ```
    claude plugin install usageloom-quota --marketplace cynicism66/UsageLoom
    ```

  - mod 只保存这两个窗口，不保存会话、项目、提示词、费用或账号信息，不联网，出错时不影响会话；可与状态栏脚本同时使用。卸载：`claude plugin uninstall usageloom-quota`。
- 设置“数据来源 → Claude → Claude Code 状态栏额度”下加上了这条安装命令。

## 验证范围与已知限制

已运行以下检查：
- mod：Claude Code 官方校验通过，2 项格式测试通过（ISO 时间转为秒、越界值丢弃、无效时间留空）；在本机 Claude 桌面版的 Code 标签页加载后，快照文件立即更新，两个窗口都带重置时间。
- 核心回归，Release 桌面构建、全页面界面冒烟与数据来源设置检查。

额度数据只在用 Pro／Max 订阅登录时提供。按用户范围从仓库安装的流程以发布后的实际安装为准。安装包结构和升级规则经自动检查；真实安装与从旧版升级未在实机验证。

## 升级

可在软件内检查更新。手动安装时，将同版本 MSI 和 `UsageLoom-install.cmd` 放在同一目录，从托盘退出 UsageLoom 后运行安装入口并确认 UAC。无需卸载或清理数据。
