# API 等价费用的计算依据

UsageLoom 展示本机日志按公开 API 文本价格折算的参考金额，不是订阅实际账单、账号余额或可用额度。目录版本为 `2026-09-07.1`；下表在 2026-09-07 核对。核对日期不是官方生效日期，官方未明确给出的起止日期保持未知。

| 模型与官方来源 | 普通输入 | 缓存读取 | 缓存写入 | 输出 |
| --- | ---: | ---: | ---: | ---: |
| [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra) | 10 | 1 | 12.5 | 50 |
| [GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol) | 4 | 0.4 | 5 | 20 |
| [GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra) | 2 | 0.2 | 2.5 | 12 |
| [GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna) | 0.2 | 0.02 | 0.25 | 1.2 |
| [GPT-5.5](https://developers.openai.com/api/docs/models/gpt-5.5) | 5 | 0.5 | 未核实 | 30 |
| [GPT-5.4](https://developers.openai.com/api/docs/models/gpt-5.4) | 2.5 | 0.25 | 未核实 | 15 |
| [GPT-5.4 Mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini) | 0.75 | 0.075 | 未核实 | 4.5 |
| [GPT-5.3 Codex](https://developers.openai.com/api/docs/models/gpt-5.3-codex) | 1.75 | 0.175 | 未核实 | 14 |
| [GPT-5.2 Codex](https://developers.openai.com/api/docs/models/gpt-5.2-codex) | 1.75 | 0.175 | 未核实 | 14 |

单位：美元 / 百万 Token。当前目录用于重估已收集的历史，不宣称还原历史实际单价；价格变动需要新目录版本，不修改 Token 历史。Sol 页面承诺促销至少持续至 2026-11-21，此日期不是官方明确的终止日，超过它时软件保守要求复核。

## 公式与覆盖率

普通输入 = 输入 − 缓存读取 − 缓存写入。各类 Token 分别乘相应单价后除以一百万；推理已属于输出，不重复加价。缺失子分类价格不自动回退为普通输入价格。

定价覆盖率 = 已能定价的 Token / 全部有效 Token。它不表示账单完整性，也不表示特殊条件全部已知。全部缺价显示“暂不可估算”，部分缺价显示“部分估算”和缺口数量；真正的零单价与未知不同。

## 特殊条件

- Astra：单请求输入超过 272,000 时，输入及缓存 ×2，输出 ×1.5；已确认 Batch/Flex ×0.5，Fast ×2。
- Sol/Terra/Luna：单请求输入超过 272,000 时输入 ×2、输出 ×1.5；长上下文下缓存类别倍率未在当前核对范围内明确，因此该部分标为未定价，不猜测。
- GPT-5.4/5.5：官方描述为全会话的长上下文条件，必须有可靠的会话级判定，不能把本机累计输入当成单请求或全会话条件。
- GPT-5.4、5.4 Mini、5.5：明确使用区域处理端点时 ×1.1。不能根据用户所在国家或本地时区推断区域处理。
- 其他模型的非 Standard/Fast/区域倍率尚未核实则不套用；不将一种模型的规则泛化到全部模型。

解析器会在 `last_token_usage` 与本次增量一致时保存单请求输入规模，用于自动识别可验证的长上下文倍率；索引 v5 会从原始日志安全迁移这一字段。若日志不能可靠恢复单请求输入、服务档位或区域条件，界面显示基础价格估算及“条件未核实”，不声称已自动识别全部倍率。工具调用、图片和音频独立费用不在本目录内。

不同工具的估算价值可能不同，即使 Token 总数接近：价格目录的核对日期、长上下文判定、服务档位与缺价处理都会影响结果。UsageLoom 保留当前官方价格和已验证倍率，不以匹配其他工具的金额为目标。

实现与回归：[C# 计价](../src/UsageLoom.Core/Pricing.cs)、[合成测试](../tests/UsageLoom.Core.Tests/Program.cs)。旧 JavaScript 原型价格与此新 C# 目录暂分开，不能把旧原型视为已经实现上述全部条件。
