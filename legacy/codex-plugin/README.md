# UsageLoom JavaScript / MCP 原型

这里保留独立的 0.1.0 技术原型，不是 Windows 桌面程序的运行依赖，功能与计价实现不应视为桌面版的当前实现。

在本目录运行 `npm ci --ignore-scripts`、`npm run check`。仓库公共文档校验位于根目录 `scripts`，因此开发测试需要完整仓库。`npm run smoke` 会读取本机使用记录，仅在需要时手动运行，不公开其输出。

`npm run package` 将原型及必要公共文档打包到仓库 `artifacts/legacy`；拒绝静默覆盖已有包。插件包可以通过 `node server/index.mjs` 启动只读 MCP 服务。

插件清单、MCP 配置、HTML 面板、providers 和原型测试均留在此目录。Windows 用户请下载主仓库的 MSI 或 ZIP。
