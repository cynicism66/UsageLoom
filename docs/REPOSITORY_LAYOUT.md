# 目录指南

```text
src/
  UsageLoom.App/          WinUI 应用入口、窗口与生命周期
    Views/               按职责拆分的 Dashboard partial 文件
  UsageLoom.Core/         解析、查询、计价、额度估算与协议
  UsageLoom.Storage/      SQLite、索引、归属与采样持久化
tests/
  UsageLoom.Core.Tests/   桌面核心回归测试
  project/               公共仓库与隐私规则测试
legacy/codex-plugin/     独立的 JavaScript/MCP 原型及测试
scripts/
  common/                共享构建配置（版本读取）
packaging/              Windows Installer 配置
assets/                 桌面图标与第三方许可
docs/
  releases/              各版本发布说明
  images/                文档演示截图
artifacts/              所有新构建、打包与验证产物（不提交）
.tools/                 本机 SDK、NuGet、WiX 缓存（不提交）
```

## 界面代码

`Dashboard.cs` 保留窗口构造、导航、日期状态和刷新协调；`Views` 中 Components、History、Quota、Capacity、Settings、About 分别承载相应界面实现。此次只按方法边界拆分，不改变算法和控件生命周期，所有文件仍组成同一个 Dashboard 类型。

## 构建与版本

桌面版本以 `src/UsageLoom.App/UsageLoom.App.csproj` 的 Version 为唯一打包版本来源。MSI、ZIP、更新清单和 MSI 检查脚本共同读取它；显式传入不同版本会报错。FileVersion 仍须保持单调递增。

正常 .NET 构建输出到 `artifacts/bin` 和 `artifacts/obj`，隔离构建使用带 `-isolated` 的对应目录。自包含发布仍输出到 `artifacts/win-x64`，MSI、ZIP 和更新清单继续使用原有输出路径，不改变安装及原位更新协议。

旧 `src/**/bin`、`src/**/obj`、历史包和诊断副本不会被此次重构删除。它们可能含有排查材料，应另行核对后清理；不要删除用户数据来整理工程。`.tools` 是可复用工具缓存，不是交付内容。

## 数据边界

真实用户数据仍在 `%LOCALAPPDATA%\UsageLoom\user-data\`；更新恢复材料仍在 `%LOCALAPPDATA%\UsageLoom\updates\`。本次不迁移或清理这两处，也不裁剪运行时 DLL。

## 第二阶段：发布包组件化

App 直接引用 Windows App SDK WinUI 组件，并显式引用 MSIX 构建工具。不再引用 SDK 总包，不带入应用未使用的 AI、ML、Widgets 等组件；仍保留完整的必要自包含运行时，未启用 .NET trimming 或单文件发布。

依据：[Microsoft Windows App SDK 1.8 的组件化说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)。组件版本与此前总包解析的 WinUI 版本一致，不借机升级 UI 框架版本。

`build-windows.ps1 -Publish` 先生成唯一的 `artifacts/publish-staging-*`，通过 `test-publish-layout.ps1` 后替换 `artifacts/win-x64`，旧输出保留为 `artifacts/publish-backup-*`。打包脚本同样检查必需文件、无用组件和更新清单；请勿往发布目录混入个人文件。离线环境已完成依赖还原后可用 `-NoRestore`，默认仍正常还原并执行 NuGet 审计。

可用 `UsageLoomBuildRoot` MSBuild 属性指定独立的构建缓存根目录，以验证更换依赖后的干净产物。安装目录布局不变，ZIP 更新按照旧清单备份并清除不再使用的程序文件。
