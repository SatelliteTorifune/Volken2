# Volken2 文档索引(docs/)

> 项目:Volken(SimpleRockets 2 / JNO 体积云 mod,Unity BIRP)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 关键文件 / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `docs/` 的导航页。**当前活跃文档:`to-do.md`(待办)、`Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md`(优化路线图)、`Volken-水体系统大修可行性分析-2026-09-02.md`(评估完成,实施未排期)**,其余已完成/历史文档已移入 [`archive/`](archive/)。
> 约定:方案/排查/分析单一主题一个文件,写清「状态 + 决策记录」,完成后移入 `archive/` 并在此更新索引;**完整文档写入规则见 [§四](#四文档写入规则维护约定)**。
> **调试日志路径**:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志)。

---

## 一、当前活跃(尚有未完成工作)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`to-do.md`](to-do.md) | **待办清单**(高/低优先度 + 已修复台账) | 📋 活跃 backlog | config 卡住(高)、星环渲染顺序/Craft 高轨道云 scale(低);已修复项附根因简述(水面覆盖云、TSS 拖影、原点重置偏移、JNO 冲突) |
| [`Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md`](Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md) | **体积云优化路线图**(借鉴 VolRe/KSA) | 📋 分析完成,T0 部分落地 | 13 项优化点按收益排名:T0 光照解耦(50→6 样本)/距离淡出,已被方案 C、轨道云部分消化;**Light Volume、PlaceRays 为长期项** |
| [`Volken-水体系统大修可行性分析-2026-09-02.md`](Volken-水体系统大修可行性分析-2026-09-02.md) | **水体大修可行性**(路线 A~E) | 📋 评估完成,实施未排期 | 建议先 A/B 零风险调参(运行时改参/水下观感),E 整换 shader 为数周级终局;反射云(方案 A)已落地 |

---

## 二、已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/Volken-方案A-stockDensityScale-自带云区域内密度缩放-2026-09-06.md`](archive/Volken-方案A-stockDensityScale-自带云区域内密度缩放-2026-09-06.md) | **方案 A:自带云区域内密度缩放** | ✅ 已实现归档(2026-09-06) | `stockDensityScale`(默认 1)只压区域内附加密度地板、`dist` 保足迹边缘;`scale<1` 把实心云拆成蓬松结构;翻案自阈值重映射(见 §9 历史) |
| [`archive/Volken-方案B-游戏自带云作为全球分布形状-2026-08-23.md`](archive/Volken-方案B-游戏自带云作为全球分布形状-2026-08-23.md) | **方案 B:游戏自带云 cubemap 作全球分布形状** | ✅ 已实现归档(2026-08-23) | `useStockCloudMap` 把 Clouds cubemap 接入 layers/shape 两处分布源;`stockMapStrength=0` 逐字节回退;缺层/无云星球逐带回退 |
| [`archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md`](archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md) | **方案 C:KSA 体积云技术移植(BIRP,时序超采样核心)** | ✅ 已实现归档(2026-08-24,2026-08-27 收工) | 低清全量 raymarch + 全清时序上采样 + 运动自适应;重投影 Y 镜像已修;坐标原点重置已修;JNO 冲突已定位 |
| [`archive/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail-2026-08-27.md`](archive/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail-2026-08-27.md) | **覆盖分解:biome × 旋转分布 × tiled detail** | ✅ 已实现归档(2026-08-27) | `coverage = cloudCoverage × F_biome × F_rotDist × F_tiledDetail`,三强度默认 0 = 逐字节一致 |
| [`archive/Volken-轨道云与过渡带交叉淡入-可行性分析-2026-08-27.md`](archive/Volken-轨道云与过渡带交叉淡入-可行性分析-2026-08-27.md) | **轨道 2D 云 + 过渡带交叉淡入** | ✅ 已实现归档(M0~M3,2026-08-27) | `OrbitClouds` 壳求交 + 同源密度采样,`orbitFade` 交叉淡入;海拔分派:低空体积云 / 高空 2D 云;夜间晨昏线已修 |
| [`archive/Volken-割裂线排查记录-运动残影TSS关-2026-08-25.md`](archive/Volken-割裂线排查记录-运动残影TSS关-2026-08-25.md) | **割裂线排查(TSS 关 + 运动残影)** | ✅ 已修复归档(2026-08-25) | 根因 = 重投影矩阵用逻辑投影,与射线重建 clip 约定差 Y 翻转 → 历史镜像采样;修复:`GL.GetGPUProjectionMatrix` |
| [`archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md`](archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md) | **JNO 联机 mod 冲突(NRE 中断 SceneLoaded 事件链)** | ✅ 已定位并修复归档(2026-08-27) | JNO `MultiPlayerUI.OnSceneLoaded` 对 null `inspectorPanel` 解引用抛 NRE → 事件链中断 → Volken 初始化被跳过;JNO 侧空值护栏(手动应用) |
| [`archive/Volken-实时反射适配分析-2026-09-02.md`](archive/Volken-实时反射适配分析-2026-09-02.md) | **实时反射适配分析**(水面平面反射 / 机体探头) | ✅ 分析完成,方案 A 已落地归档(2026-09-02) | 云只渲染在主相机 OnRenderImage;方案 A(水面反射合入云)已落地,方案 B(机体 cubemap 探头)后置未做 |

> ✅ 归档文档头部「状态:」为最终结论;文档内勾选项标记实际落地情况,未勾选项 = 待复跑/未排期项,按需复跑,勿当作当前待办执行。

---

## 三、决策速查(最新决策)

| 决策 | 结论 | 出处 |
|---|---|---|
| 方案编号体系 | 方案 A/B/C 同一套:方案 B = 自带云分布接入;方案 A = 自带云区域内密度缩放(前置 B);方案 C = KSA 时序超采样移植 | 各方案文档 |
| 自带云分布接入 | **✅ 方案 B 已实现**:游戏 Clouds cubemap 作全球分布形状,`stockMapStrength=0` 纯回退;缺层逐带回退 | [archive/Volken-方案B-…](archive/Volken-方案B-游戏自带云作为全球分布形状-2026-08-23.md) |
| 自带云区域内密度 | **✅ 方案 A 已实现**:`stockDensityScale` 区域内密度缩放(默认 1 恒等);**翻案说明**:初版阈值重映射 `stockCoverage/stockSoft` 会收缩足迹边缘,弃用改为密度缩放(§9 历史,勿复用) | [archive/Volken-方案A-…](archive/Volken-方案A-stockDensityScale-自带云区域内密度缩放-2026-09-06.md) |
| 时序超采样 | **✅ 方案 C 已实现**:低清每帧全量 raymarch + 全清时序上采样(Upscale),运动自适应 `tssBlend`;单 `historyTex`(无 flip/flop),TSS 关走运动残影路径 | [archive/Volken-方案C-…](archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md) |
| 重投影修复 | **✅ 已修复**:`prevViewProjMat` 用 `GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)` 与射线重建 clip 约定对齐(修 fresh + 时序两路径) | [archive/Volken-割裂线排查记录-…](archive/Volken-割裂线排查记录-运动残影TSS关-2026-08-25.md) §4.5 |
| 坐标原点重置 | **✅ 已修复**:订阅 `IGameView.ReferenceFrameRecentered`,清空时序历史 + `frameNumber=0` 冷启动 | [archive/Volken-方案C-…](archive/Volken-方案C-KSA体积云技术移植BIRP-2026-08-24.md) §12 |
| 覆盖分解 | **✅ 已实现**:覆盖 = `cloudCoverage × F_biome × F_rotDist × F_tiledDetail`,三强度默认 0 | [archive/Volken-覆盖分解-…](archive/Volken-覆盖分解-biome静态图x旋转分布图xtiledDetail-2026-08-27.md) |
| 轨道云 | **✅ 已实现**:海拔分派(低空体积云 / 高空 2D 壳着色)+ `orbitFade` 过渡带交叉淡入;2D 与体积云**同源密度采样**避免云形突变;夜晚侧晨昏线门控已修 | [archive/Volken-轨道云与过渡带交叉淡入-…](archive/Volken-轨道云与过渡带交叉淡入-可行性分析-2026-08-27.md) |
| 实时反射 | **✅ 方案 A(水面反射合入云)已落地**,方案 B(机体 cubemap 探头)后置;反射场景关 TSS、粗步长、低光样本 | [archive/Volken-实时反射适配分析-…](archive/Volken-实时反射适配分析-2026-09-02.md) §4 |
| JNO 冲突 | **✅ 根因定位**:JNO `OnSceneLoaded` NRE 中断事件链 → Volken 初始化被跳过;JNO 侧空值护栏(手动应用);Volken 侧自愈曾加后撤除,回纯事件驱动 | [archive/Volken-冲突排查-…](archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md) |

**当前待定(尚未拍板/未调研)**:
- **水体大修实施顺序**:评估完成(A/B 先行),未排期([Volken-水体系统大修可行性分析-2026-09-02.md](Volken-水体系统大修可行性分析-2026-09-02.md))。
- **优化点剩余项**:Light Volume(#11,长期)、PlaceRays(#10,长期)、噪声 mipmap/密度 LUT(#4/#5)、HDR RT(#6)、MV 4 次膨胀(#9)等,见 [Volken-体积云优化点分析-…](Volken-体积云优化点分析-VolRe与KSA借鉴-2026-08-28.md)。
- **方案 C 已知缺口**:N/S 风(非刚体 Y 旋转,云空间重投影近似未覆盖)、flip/flop 双缓冲(单缓冲当前可用)、TSS 开时 !isFresh 硬回退闪烁待用户实测确认。
- **to-do 高优先**:切换至有大气星球时 config 卡住(未知原因,等复现和 log)。

---

## 三之二、当前代码里的已知问题(复核)

> 这一节记录**已核实、但还没动手修**的问题,供下次开工直接取用。

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | **切换至有大气星球时 config 卡住**(高优先) | `to-do.md`;未知原因,等待更多复现和 log | 进有大气 SOI 时配置卡死,需复现后定位 |
| 2 | **星环渲染顺序错误**(低优先) | `to-do.md` | 星环相对云/水体层级错误 |
| 3 | **Craft 在高轨道时云层 scale 错误**(低优先) | `to-do.md` | 高轨道下云的缩放不符合预期 |
| 4 | **N/S 风重投影缺口**:云空间重投影只覆盖绕 Y 自转 + 东西风平移 | 方案 C §5 已知局限 / 割裂线 §7 遗留 | 强南北风时历史重投影失效 → 残影/鬼影;需完整 worldToCloud 矩阵 |
| 5 | **`CloudConfig.low/mid/highAltitudeThreshold` 死配置**(只定义+序列化,零消费) | 轨道云文档 §2/§4.3 | 与 KSA 的 start/end 一对设计不对应;**不可删除**(旧 XML 含节点,删除会导致 XmlSerializer 反序列化失败回退默认配置),仅弃用 |

---

## 四、文档写入规则(维护约定)

> 适用:所有 `docs/**` 下的 `.md`;`README.md` / `AGENT_CONTEXT.md` 本身也遵守 0 命名与 8 编码规则。
> 本规则仿照 JNO 联机 mod 的 `plans/` 目录约定(见 JNO `plans/README.md` §四)适配而来。

### 0. 命名规范
- 方案/排查/分析文档:`<主题>-YYYY-MM-DD.md`(中文主题 + 日期后缀;日期 = 创建/事件日期,补零)。**禁止无日期名**(历史教训:早期文档无日期,游离在索引外)。
- 索引/参考文档(`README.md`、`AGENT_CONTEXT.md`)不加日期;待办清单 `to-do.md` 作为活跃 backlog 也不加日期。
- 单一主题一个文件;已完成文档移入 `archive/` 子目录,文件名规则不变。

### 1. 状态与单一事实源
- **文档头部 blockquote 的「状态:」是唯一事实源**;README 索引行、决策速查表、待定段、AGENT_CONTEXT 里的进度都只是它的镜像。
- 状态词汇受控,禁止自造:📋 规划中 / 研究 / 评估中 → ✅ 已实现 / 已落地 → ✅ 已归档。同一主题同一时刻只能有一个状态。
- **改文档头部状态时,必须同一次改动里同步更新**:README 索引行(§一↔§二 分区移动)、决策速查表、待定段,以及 AGENT_CONTEXT 相关行。

### 2. 结构模板(新建文档建议)
- 头部 blockquote:`状态:` / `日期:`(或 `创建日期:`) / `关联:`(相关文档链接 + 一句关系说明) / 一句话主题定位。
- 正文顺序:动机 → 现状 → 方案 / 决策 → 实施记录(按日期分小节)→ 剩余项 / 回归判据。
- 结论先行;被取代的方案要标注「已被取代,保留作决策记录」(如方案 A §9 历史设计)。

### 3. 决策记录
- 格式:「【决策:YYYY-MM-DD】」;明确结论写进对应文档,并汇总到 README「决策速查」表(出处列 = 文档链接 + 小节号)。
- 翻案决策:保留旧记录,加「翻案说明」链接到新决策(如方案 A:阈值重映射 → 密度缩放)。

### 4. 交叉链接
- 一律相对路径;同目录内互链用裸文件名 `x.md`。
- root 引用归档:`archive/x.md`;归档引用 root:`../x.md`;**归档内互链:裸文件名(不要加 `../`)**。
- **移动 / 归档一个文档后,必须全仓库 grep 修正所有指向它的链接**(含文档正文与 README),并跑一次死链校验。
- 站内不写本机绝对路径(一律用令牌,见 §10「隐私红线」);源码引用用工程相对路径 `Assets/Scripts/...`。

### 5. 归档流程(完成一个主题后)
1. 头部状态改「✅ 已归档(原状态:…)」;
2. 未排期剩余项 / 待复跑项:文档内写明,并同步进 README「当前待定」段;
3. `git mv` 移入 `archive/`(保留历史)→ 按 §4 修正全部链接;
4. 更新 README:从 §一 移到 §二,同步决策速查表出处列;
5. 归档 ≠ 删除:仍被索引引用,按摘要可随时找回。

### 6. 已知问题与修复记录
- 已核实未修的问题 → 登记进 README「三之二」表(问题 / 证据 / 影响;证据给 `文件:行号` + 反编译出处)。
- 修复后 → 该行标记「✅ 已解决」并保留记录,不整行删除。

### 7. 与代码同改、同提交
- 文档与对应代码改动放同一次提交;禁止"改了一半不提交"。
- 涉及游戏内文案、命令、路径时,同步核对 `AGENT_CONTEXT.md` 关键路径表。

### 8. 编码与校验
- 一律 **UTF-8 无 BOM、行尾 LF**。
- **不要用会把非 UTF-8 字节替换成 `U+FFFD` 或加 BOM 的工具**:尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8`(加 BOM)与 `-replace`(处理含 `[`/反引号的 Markdown 链接会吃字符)。
- 改完用脚本自检(三者任一为 True 即有问题):

```powershell
$p = '<文件路径>'
$b = [IO.File]::ReadAllBytes($p); $t = [Text.Encoding]::UTF8.GetString($b)
$bom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
"BOM=$bom CRLF=$($t.Contains("`r`n")) U+FFFD=$($t.Contains([char]0xFFFD))"
```

### 9. 改后自检清单
- [ ] 所有 `](...)` 站内链接可解析(相对路径无死链)
- [ ] 无 BOM / 无 CRLF / 无 U+FFFD,行尾 LF
- [ ] 头部「状态:」与 README 索引行一致(§一/§二 分区正确)
- [ ] 新决策已进 README「决策速查」表(出处带链接)
- [ ] 归档文档已从 §一 移入 §二,且全仓库无指向旧位置的链接
- [ ] 涉及代码/文案的改动已与文档同批提交
- [ ] 无本机绝对路径 / 用户名 / IP(一律令牌化,见 §10)

### 10. 隐私红线(公开仓库强制)
- **本机绝对路径 / 用户名 / IP 一律不进被跟踪文档**:本机路径统一用 `<TOKEN>` 引用。令牌→真实路径的映射**只存在本机被 `.gitignore` 排除的 `docs/LOCAL_PATHS.md`**(严禁提交;公开仓库不含该文件)。
- **令牌速查**(真实值见本机 `docs/LOCAL_PATHS.md`,此处只给含义,不给真实路径):

| 令牌 | 含义 |
|---|---|
| `<PROJECT>` | 本工程目录 |
| `<USERPROFILE>` | 本机用户目录(日志等用,如 `<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`) |
| `<JNO_CODE>` | 反编译游戏源码根(只读) |
| `<JNO_D2>` | 解包工程(Unity 2022.3,水体分析用) |
| `<JNO_MP>` | JNO 联机 mod 工程(JNOmultiplayerTest) |
| `<VOLRE_REF>` | VolRe(KSP 体积云)参考源码 |
| `<KSA_REF>` | KSA 体积云参考源码 |

- 引用反编译源码时,保留「文件名 + 行号」(如 `` `CraftNode.cs:1235-1240` ``),**不要**写本机路径形式的站外链接。
- **上传前**:把文档内 `<TOKEN>` 之外的本机路径全部替换为令牌;`LOCAL_PATHS.md` 已被 `.gitignore` 排除,不会误提交。
