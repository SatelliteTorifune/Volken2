# Volken2 文档索引(docs/)

> 新会话先读 [AGENT_CONTEXT.md](AGENT_CONTEXT.md)。本索引只保留入口、待办与维护规则;实现细节归主题文档。
> 2026-10-08 更新:雨碰撞 / 水花修复获用户真机确认并归档。**其他记录的归档状态不代替各自验收证据**;剩余回归见 §四之三。

## 一、当前活跃(已动手)

当前无活跃实施文档;本轮雨功能工作已收束,后续专项回归与未排期工作见 §四之三。

## 二、提案 · 待拍板(proposals/)

| 文档 | 状态 / 范围 |
|---|---|
| [云带区间步进](proposals/cloud-band-interval-raymarch-2026-10-03.md) | 📋 评估中:区间求交集中 raymarch 预算,未动手 |
| [云优化路线图](proposals/cloud-optimization-roadmap-2026-08-28.md) | 📋 规划:部分已被 TSS / 轨道云覆盖,剩余项未排期 |
| [雨 / 雷性能审计](proposals/rain-lightning-perf-audit-2026-10-02.md) | 📋 规划:C# 优化已有实现,GPU 归约与分叉合批未排期 |
| [剩余热点与代价](proposals/hotpath-optimization-backlog-2026-10-02.md) | 📋 规划:B/C/D 组待取舍,A 组按既有决定不做 |
| [水体大修](proposals/water-system-overhaul-2026-09-02.md) | 📋 规划:A~E 路线待选,未排期 |

## 三、已归档(archive/)

历史正文保留当时的诊断、参数和证据;头部核对说明与后续实施记录优先于早期方案。未完成验收不得因归档而勾选完成。

| 文档 | 已落地结论 / 保留价值 |
|---|---|
| [雨重做](archive/sp2-rain-particledomain-port-2026-09-28.md) | 雨视觉 / 雨声 / 透明度 / GPU 碰撞与水花已落地;无标签 shader 捕获修复获用户真机确认,135 项 GPU 验证通过;性能和专项矩阵未排期 |
| [天气与云解耦](archive/weather-cloud-decoupling-2026-10-01.md) | 生命周期与高度带去重已落地;A2 / D 未排期,回归待验证 |
| [场景门控](archive/monobehaviour-scene-gate-2026-10-02.md) | 常驻停表、相机扫描与渲染集合优化已落地;待回归 |
| [雨开关与切场景](archive/rain-toggle-scene-switch-2026-10-02.md) | 实例加载、重挂、设计器场景门控已修;待回归 |
| [预设下拉](archive/weather-preset-dropdown-stale-2026-10-04.md) | 选项过期重建与缺失文件护栏已落地;待回归 |
| [附加相机雨](archive/extra-camera-rain-2026-10-04.md) | 每相机实例和主视图诊断量归属已落地;待 PIP 回归 |
| [雷声真实化](archive/thunder-realism-2026-09-28.md) | 距离 / 声速 / near-far 与残留、紫红修复;当前部署和完整听感回归未确认 |
| [闪电固定端点](archive/lightning-fixed-position-diagnosis-verify-2026-10-02.md) | 最终根因是分叉第 0 点未赋值;已补 `SetPosition(0, from)`,待回归 |
| [雷电海拔限位](archive/lightning-altitude-ceiling-2026-10-02.md) | 上限与手动触发反馈已落地;待回归 |
| [天气母计划](archive/sp2-weather-port-2026-09-27.md) | 历史方案、反编译和素材底账;后续雨计划与解耦记录已接管当前工作 |
| [雨滴落点重复](archive/rain-spawn-hash-precision-2026-10-04.md) | 整数 hash 修复;含真机唯一率 0.932 / 最大重复 5 的证据 |
| [雨雾移植复盘](archive/weather-rain-fog-postmortem-2026-09-27.md) | 旧实现失败原因与诊断方法;朝向结论须结合雨计划 §10.3 |
| [方案 A:密度缩放](archive/stock-density-scale-2026-09-06.md) | 自带云区域内密度控制,保留足迹边缘 |
| [方案 B:云分布](archive/stock-cloud-distribution-2026-08-23.md) | 自带 cubemap 提供全球分布 |
| [方案 C:TSS](archive/ksa-temporal-upscale-port-2026-08-24.md) | 时序上采样、运动自适应、浮动原点清历史 |
| [覆盖分解](archive/coverage-decomposition-2026-08-27.md) | coverage 乘 biome / 旋转分布 / tiled detail 因子 |
| [轨道云](archive/orbit-clouds-crossfade-2026-08-27.md) | 2D 云与体积云按海拔交叉淡入 |
| [重投影割裂线](archive/seamline-reprojection-2026-08-25.md) | GPU 投影 Y 约定修复 |
| [水面反射](archive/reflection-adaptation-2026-09-02.md) | 水面反射云已落地;机体探头适配未做 |
| [联机场景事件 NRE](archive/jno-sceneloaded-nre-2026-08-27.md) | 第三方事件处理器异常中断初始化链的证据 |

## 四、决策速查

| 约束 | 权威出处 |
|---|---|
| 生命周期归 `VolkenClouds`;高度带走 `CloudConfig.TryGetBand`,不恢复已撤抽象层 | [解耦](archive/weather-cloud-decoupling-2026-10-01.md) §2.1、§8 |
| 雨 / 雷独立启停,无全局天气值;云 / 天气预设各自存储 | [解耦](archive/weather-cloud-decoupling-2026-10-01.md) §8 |
| XML 是跨场景配置来源;Flight→Flight 同行星早退保留内存状态 | [雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §3 |
| 常驻按飞行状态停表;雨挂载和绘制另查活体场景状态 | [场景门控](archive/monobehaviour-scene-gate-2026-10-02.md) §1、[雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §4 |
| 每相机资源独立,共享雨诊断只归主视图写 | [附加相机雨](archive/extra-camera-rain-2026-10-04.md) §2 |
| 下拉过期须重建面板;加载不存在的预设应失败 | [预设下拉](archive/weather-preset-dropdown-stale-2026-10-04.md) §1 |
| 雨采用世界系构轴;自适应域只改半径,不自动扩容补密度 | [雨计划](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.3、§10.5 |
| 重生随机数用整数 hash;禁无界浮点 `frac` hash | [落点重复](archive/rain-spawn-hash-precision-2026-10-04.md) §2 |
| 资产路径和清单 GUID 都须同步;清单就绪不代表部署成功 | [解耦](archive/weather-cloud-decoupling-2026-10-01.md) §8、[雷声](archive/thunder-realism-2026-09-28.md) §5.3 |

## 四之二、当前代码里的已知问题

| # | 问题 / 证据 | 状态 |
|---|---|---|
| 1 | 星环与云 / 水渲染顺序,原用户问题记录 | 待复现;本次未做运行时核实 |
| 2 | Craft 高轨道云 scale,原用户问题记录 | 待复现;本次未做运行时核实 |
| 3 | N/S 风未纳入云空间重投影;[方案 C](archive/ksa-temporal-upscale-port-2026-08-24.md) §5、§12 | 未修;另有双缓冲与非新采样硬回退缺口 |
| 4 | `low/mid/highAltitudeThreshold` 已删除,旧“不可删”告警错误;[解耦](archive/weather-cloud-decoupling-2026-10-01.md) §8 | ✅ 已更正;旧 XML 未知节点由 `XmlSerializer` 忽略 |
| 5 | 自适应雨域变大但粒子数固定,密度变稀;`RainParticles.OnPreCull` | 已知代价;不恢复渲染回调内自动重建 GPU buffer |

## 四之三、待办清单(backlog)

待办只保留动作与来源;已落地修复台账合并在 §三,具体判据留在主题文档。

**已收束**:[雨记录](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.8 的水花不可见问题已获用户真机确认;不再列为活跃故障。密度标定、性能测量和以下专项回归保留为未排期工作。

**尚未完成的专项回归**(未排期;不扩大本次用户确认的验收范围):

| 范围 | 要验证的行为 | 判据 |
|---|---|---|
| 雨与配置 | 复杂材质与场景矩阵 / 透明度边界 / 软粒子 / 海拔 / 水下 / 重定位 / XML 往返 / 切场景 | [雨计划](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.5、§10.7、§10.8、[雨开关](archive/rain-toggle-scene-switch-2026-10-02.md) §2、§4 |
| Inspector | 冷启动列表完整、缺失文件不新建、另存为可见 | [下拉](archive/weather-preset-dropdown-stale-2026-10-04.md) §2 |
| PIP | 相机有雨,主视图雨声不被覆盖,参数同步 | [附加相机雨](archive/extra-camera-rain-2026-10-04.md) §4 |
| 场景 / 解耦 | 非飞行停表、离场收雨雷、环境抑制不改用户开关 | [场景门控](archive/monobehaviour-scene-gate-2026-10-02.md) §4、[解耦](archive/weather-cloud-decoupling-2026-10-01.md) §6 |
| 闪电 | 分叉起点、暂停 / 重定位、海拔上限与手动反馈 | [固定端点](archive/lightning-fixed-position-diagnosis-verify-2026-10-02.md) §5、[海拔限位](archive/lightning-altitude-ceiling-2026-10-02.md) §4 |
| 雷声 / 资源 | 已部署包素材加载、声速延迟、near-far 听感和多声部 | [雷声](archive/thunder-realism-2026-09-28.md) §5.3、§5.4、§6 |

**资源核对(2026-10-08)**:`_otherAssets` 共 **36** 条,GUID 全可解析;含 **6 条雨声 + 9 条雷声**,以及新增碰撞 compute / 深度 shader / 水花 shader。三个碰撞 / 水花资源已核实入包,修复后的游戏效果获用户确认;未做整包逐文件一致性审计。

**未排期 / 低优先度**:

- 解耦 A2 相机海拔去重、D 行星映射双写者:[解耦](archive/weather-cloud-decoupling-2026-10-01.md) §8;F 的全局天气值通道已被后续决策撤销。
- 雨 / 雷 GPU 性能与剩余热点:见 §二;拖 UI 的 A 组维持不做。
- 死代码候选 `DepthCapture` 和未启用星环补丁:[场景门控](archive/monobehaviour-scene-gate-2026-10-02.md) §3;本次未删代码。
- 注释中的阶段号、日期与过期描述:按 §五.10 清理;旧占比只代表 2026-10-02,不当作当前实测。
- 二期可选项:雨图集、雾、落雷贴地形、联机表现、机体反射探头;雨碰撞 / 水花本轮已收束,见雨记录 §10.8。
- 新功能保持可关闭且默认关闭;天气联动云须用户另行提出,不能恢复旧占位字段或天气值方案。
- 日志已收口:雨 / 雨声自动心跳、调参回显、逐次雷声记录已移除;雨 GPU 诊断通过面板按钮手动触发,错误告警保留。见 [雨记录](archive/sp2-rain-particledomain-port-2026-09-28.md) §10.9。

## 五、文档写入规则(维护约定)

### 0. 命名与位置

- 主题用 `<topic>-YYYY-MM-DD.md`(英文 kebab-case),正文中文,单一主题一个文件。索引 / 上下文 / 本地路径表不带日期;脚本放根目录 `tools/`。

### 1. 状态与单一事实源

- 头部 `状态:` 是唯一事实源;使用 📋 规划 / 评估中、🚧 实施中、⏸ 暂停、✅ 已实现 / 已归档。状态变更同步索引、待办和上下文。
- 实现与验收分开:源码存在、历史编译成功、历史真机证据、本次复验不能互相替代;未知须注明未确认。

### 2. 结构、决策记录与“不复制正文”

- 头部写状态、日期、关联和定位;正文按动机 → 现状 → 决策 → 实施记录 → 剩余项 / 回归判据组织。
- 决策用 `【决策:YYYY-MM-DD】`;翻案保留历史范围并链到最终结论。索引只写摘要和入口,不抄参数表、论证与调试流水。

### 3. 交叉链接

- 用相对路径;移动后全仓检查 Markdown 链接、代码注释和正文路径,同步入链与出链。
- 文档引用用“链接 + 小节名 / 编号”;只有源码用 `文件:行号`。重编号须保留旧 → 新对照并迁移所有引用,包括省略写法。

### 4. 三区流转

- 根目录放正在推进的主题;`proposals/` 放未拍板 / 未排期方案;`archive/` 放已落地实现记录及被取代方案。
- 实现已收束而待验收的记录可归档,头部须注明未验收,在 §四之三保留入口;未排期剩余项也须保留。移动后检查旧路径消失、新内容完整。

### 5. 已知问题

- 登记 §四之二并附代码或运行证据;只有历史报告而未复验须注明。修复后保留结论,进入归档台账或标记已解决。

### 6. 与代码同改、同提交

- 行为、文案、命令、路径变化时同步对应文档;提交时成组包含相关改动。文档整理可独立提交,不要夹带行为修改。

### 7. 编码与校验

- `.md` / `.cs` 使用 UTF-8 无 BOM、LF,禁止 U+FFFD;避免 PowerShell 5.1 默认编码与误处理 Markdown 的正则替换。
- 读取失败立即停止;命令中的路径用 ASCII 或枚举出的 `.FullName`。恢复 / 重写后重新检查编码与内容。

### 8. 改后自检清单

- 必跑 [check-docs.ps1](../tools/check-docs.ps1):`powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 -Root docs`,退出码 0。
- 人工复核状态 / 分区 / 待验收一致,路径与章节引用有效,新旧文件不并存,关键事实未丢失。脚本不能代替这些判断。
- 删除 / 移动 / 批量改写后检查实际文件和退出码,不能只凭命令无报错判断成功。

### 9. 隐私红线(公开仓库强制)

- 公开文档不写本机绝对路径、用户名或 IP;真实值只在被忽略的 [LOCAL_PATHS.md](LOCAL_PATHS.md),严禁提交。
- 令牌:`<PROJECT>`、`<USERPROFILE>`、`<JNO_CODE>`、`<JNO_D2>`、`<JNO_MP>`、`<VOLRE_REF>`、`<KSA_REF>`、`<SP2_D4>`、`<SP2_CODE>`、`<SP2_GAME>`、`<RVM_EA>`。反编译证据写文件名和行号。

### 10. 代码注释(规范条文)

- 留职责、不变量 / 契约、单位 / 范围 / 默认值、失败降级和必要反例;删复述名称、阶段号、日期、踩坑史、方案论证、调试流水。历史只放文档指针。
- 类注释 ≤3 行,方法通常 1 行(必要说明 ≤3 行);shader / compute 头部可集中写不变量。参考占比:C# 5~8%,>15% 应检查;shader / compute ≤12%。
- 过期注释按缺陷处理;代码 `TODO` 登记 §四之三。只清注释不得改代码或字符串;用 [strip-code-comments.ps1](../tools/strip-code-comments.ps1) `-Old <快照> -New <改后>` 自证退出码 0。

### 11. 反模式与预防

- 改名前留快照,改后检查旧文件消失;表格保留表头 / 分隔行;临时文件不留 docs;避免重复文档与圈号。
- 精简前设可核对目标,改后检查事实、标识符、证据和隐式契约,不能只比较标题。无必要不删主题原始证据;大段历史留归档。
- 预算遵循脚本:README ≤300 行 / 单行 ≤400 字符;AGENT_CONTEXT ≤200 行 / 单行 ≤500 字符;其他文档单行 ≤1200 字符。
- 外部路径、临时文件、打包产物等事实必须实测并带日期,只记一处;历史记录不能写成未经核对的当前状态。
- 批量整理按“移动修链接 → 精简 → 校验”分步核对;不要混入代码行为修改。机械阈值变化时同步规则与脚本。
