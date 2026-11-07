# 会话入口

> 核对日期：2026-10-14。本页只保留阅读顺序与工作边界，不复制实现台账。

开工先遵守根目录 [AGENTS.md](../AGENTS.md)；文档分工、替换 / 删除、篇幅预算和收尾检查只在那里维护。

## 阅读顺序

1. [架构与配置](architecture.md)：先确认模块所有者、初始化 / 场景顺序和 XML 语义。
2. 按任务读 [云](clouds.md) 或 [天气](weather.md)，设计另读 [云核心](cloud-core.md) / [地区天气](weather-regions.md)，再读对应源码；不要套用旧类型名、旧行号和历史方案。
3. [待办](backlog.md)：判断任务是待游戏验收、已知缺口还是尚未实现的方案。
4. [开发与验证](development.md)：使用当前工具、资源打包规则和适合改动的验证。

## 开发边界

- 直接在当前原工程工作；打包 / 部署按具体任务授权处理，不从历史 GPU 检查推断已安装版本。
- 云先初始化并负责行星环境；天气只订阅它，不恢复独立编排器 / 注册表。高度带统一 `CloudConfig.TryGetBand`。
- 云与天气参数、预设名独立。天气不隐式改云，新效果默认关闭或恒等。
- 每相机渲染资源和历史独立；主视图拥有共享雨诊断 / 环境淡出，PIP 不得覆盖雨声输入。
- 云重投影与雾的 GPU 投影 Y 约定不同；密度 / 合成 / 透明度 / 重定位契约见主题文档，不能为了简化统一替换。
- 正式代码不能依赖 VolkenTests。已删的 Editor 探针 / RainPreview / 旧开发命令不可当作验证入口。
- 不把“可编译 / 独立 GPU 通过”写成游戏画质、听感或 FPS 已验收。未解决事项保留在 backlog。

关键入口：`Assets/Scripts/Mod.cs`、`ModSettings.cs`、`Assets/Scripts/Volken/`、`Assets/ModData.asset` 和 `Assets/Content/Languages/{EN-US,zh-Hans,RU-RU}.xml`。真实本机 / 参考项目路径只读被忽略的 [LOCAL_PATHS.md](LOCAL_PATHS.md)。
