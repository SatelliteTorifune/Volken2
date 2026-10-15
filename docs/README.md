# Volken 开发文档

> 2026-10-14 按当前源码整理。文档按主题维护，历史日期记录已合并，完整沿革可从 Git 历史恢复。

## 阅读入口

| 文档 | 内容 |
|---|---|
| [AGENT_CONTEXT.md](AGENT_CONTEXT.md) | 新会话阅读顺序和开发约束 |
| [architecture.md](architecture.md) | 模组入口、模块边界、生命周期、配置与已删除接口 |
| [clouds.md](clouds.md) | 体积 / 轨道云、TSS、镜头历史、雷光与水面反射 |
| [weather.md](weather.md) | 预设同步、雨 / 碰撞 / 水花、两种雾、闪电与雷声 |
| [development.md](development.md) | 当前工具、诊断、历史验证证据与验证方法 |
| [backlog.md](backlog.md) | 游戏验收、已知缺口、未实现方案与性能候选 |

本机真实路径仅存于被 Git 忽略的 [LOCAL_PATHS.md](LOCAL_PATHS.md)，不提交。公开文档使用路径令牌；本机工具见 [VolkenTests](../Assets/Scripts/VolkenTests/README.md)。

## 使用约定

当前实现以源码和主题文档为准，未完成工作只维护在 backlog。

源码已实现、独立验证通过、用户游戏确认是不同状态。历史检查不能证明最新游戏包已更新，也不能代替当前画质 / 性能验收。

开工与文档规则统一见根目录 [AGENTS.md](../AGENTS.md)，新增内容前先按其分工更新现有段落。
