# ⚠ 本文档已被取代（2026-10-05）

**本文写的是「宿主内 `--group` 分组」方案** —— 那是拆分**初版**设计，但用户随后裁定了
**物理拆分**（每个子系统一个独立 `.csproj`），且粒度要求**更细**（接近一个 mode 一组）。

⇒ 请以以下两份为准：

| 用途 | 文档 |
|---|---|
| **实施方案**（结构、步序、风险、验收） | [`TEST_SPLIT_PLAN_2026-10-05.md`](TEST_SPLIT_PLAN_2026-10-05.md) |
| **组工程模板**（csproj / Program / 抽取规则 / 验收） | [`TEST_SPLIT_TEMPLATE.md`](TEST_SPLIT_TEMPLATE.md) |

本文件**保留**是因为它的**实测清单**（三宿主规模、子系统→mode 分组表、工具依赖表）仍然有效，
且"为什么按模块跑"的论证（避免改一点跑全项目）是本次拆分的动机来源。
⚠ 但它的**结论**（宿主内加 `--group`、暂不物理拆分）**已被推翻**，不要照它实施。

> 实测数据索引：`ServiceProbe` 577 KB / 46 mode、`UiTestHost` 238 KB / 17 组、`GainMapTestHost` 98 KB。
> 已完成：ServiceProbe 的 46 个 mode → **9 个子系统工程**（见 `TEST_SPLIT_PLAN` 的组表）。
