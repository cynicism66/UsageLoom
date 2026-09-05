# 安全政策

## 支持的版本

当前项目尚无 Windows 正式发行版，安全修复以 `main` 分支的最新开发代码为对象。`0.1.x` 属于早期技术原型，其 Provider 接口仍可能发生变化。

## 报告安全漏洞

请不要为安全漏洞创建公开 GitHub Issue。本仓库已启用 GitHub Private Vulnerability Reporting（私密漏洞报告），请通过[私密安全报告入口](https://github.com/cynicism66/UsageLoom/security/advisories/new)联系维护者。

即使使用私密报告，也不要提交真实凭据或完整会话日志。请优先提供合成数据、脱敏错误和最小复现；普通功能问题与建议可通过[公开 Issues](https://github.com/cynicism66/UsageLoom/issues)提出。

## 敏感信息

任何 Issue、Pull Request、测试数据、截图或发布包都不得包含以下内容：

- OAuth access token 或 refresh token；
- API Key、Cookie、凭据文件或 `auth.json`；
- 完整的 Codex JSONL 会话日志；
- 未脱敏的任务标题或项目路径；
- 可识别个人身份的账号信息。

UsageLoom 的设计目标是只读且本地优先。任何需要修改账号、提取凭据、绕过 Provider 限额或规避服务条款的功能都不在项目范围内。

## 维护者发布检查

每次公开发布前，维护者应：

1. 运行 `npm run check`。
2. 检查发行包的逐文件清单。
3. 搜索凭据、Cookie、个人路径、真实任务标题和会话数据。
4. 核对第三方声明、许可证和价格来源。
5. 从发行包重新解压并完成一次独立校验。
6. 计算并发布 SHA-256 摘要。
