# 雷电海拔限位:太空里不再劈雷(2026-10-02)

> 状态:✅ 已归档(海拔闸门与面板反馈已落地;§4 真机验收未确认)
> 2026-10-08 核对:`IsBelowCeiling`、`ceilingAltitude` 与手动触发返回值均在当前代码中。
> 日期:2026-10-02
> 症状由用户报告:**飞进轨道 / 太空后,脚下仍会劈雷**。雨早有 `rain.ceilingAltitude` 闸门挡这件事,雷电一直没有。

## 1. 为什么会"在太空里劈雷"

`LightningModule.CastRandomBolt()` 原本只检查三件事:`config` 非空、`lightning.enabled`、`_boltShader`。
**没有任何高度条件**。而它的几何是照"云底 → 地面"造的:

- `sourceAlt` = 云层带中高;**取不到云带时退回 `CameraAltitudeAsl + 2000f`**;
- `target` = 相机下方行星正球面上的点;
- 两点之间那条**几公里**长的放电通道与观测者高度**无关**。

于是观测者在 10 万米高的轨道上时,通道照样生成 —— 而且它离相机很远、`flashIntensity` 很高,在太空黑背景上格外显眼。这正是用户看到的现象。

雨那一侧早就有同一条闸门(`ceilingAltitude` 默认 12000m,相机海拔超上限就整片不绘制,见[雨计划](sp2-rain-particledomain-port-2026-09-28.md));雷电缺的就是它。

## 2. 设计决策(与雨对齐,但有三处不同)

| 决策 | 取值 | 理由 |
|---|---|---|
| 阈值归属 | 雷自己的 `lightning.ceilingAltitude` | 与雨一致:雨闸门**不读 `CloudConfig`**,雷也不读 —— 两套子系统各自独立(见[解耦](weather-cloud-decoupling-2026-10-01.md) §3.6) |
| 判定对象 | **观测者**(相机)海拔 | 要挡的是"玩家在高处还看见雷"。雷电物理上发生在云层里、与玩家高度无关,所以本闸门管的是**可见性**而不是物理 |
| 默认值 | **12000 m** | 与 `rain.ceilingAltitude` 同值,同一颗星球上"雨停了、雷也该停" |
| `0` 的语义 | 关闭闸门(不限制) | 与雨一致。老配置没有这个 XML 节点,反序列化后取**字段默认值 12000**,不会变成 0 |
| 取不到高度 | **放行** | 沿用雨的口径:不因为读不到值就把整个子系统关掉 |
| 淡出带宽 | **不做** | 雷是**离散事件**而非每帧绘制,没有"淡出"可言;越过上限就是"不该劈",一刀切 |

## 3. 改动点

| 文件 | 改动 |
|---|---|
| `VolkenWeatherConfig.cs` | `LightningSection.ceilingAltitude`(默认 **12000**)+ `CopyFrom` + `ClampAll`(0~500000) |
| `LightningModule.cs` | 新增 `IsBelowCeiling()` / `LogCeilingSkip()`(限频 5s);`StormLoop` 倒数结束处复核;`CastRandomBolt()` 首段复核。返回类型 `void` → **`bool`**(`false` = 本次没落雷) |
| `VolkenWeather.cs` | `TriggerLightning()` 同步返回 `bool` |
| `WeatherPanel.cs` | 新增滑块 `LightningCeilingAltitude`(0~200000,整数);「立刻劈一道」按返回值提示**已触发**或**海拔过高:未落雷** |
| 三语言 XML | 新增 `Volken.UI.LightningCeilingAltitude` 与 `Volken.UI.LightningTriggerBlocked` 各 1 条(EN-US / RU-RU / ZH-CN) |

**为什么把 `CastRandomBolt` 改成返回 `bool`**:手动「立刻劈一道」是本面板唯一有副作用的按钮。如果被闸门静默拦下却照弹"已触发闪电",那是**假成功提示** —— 改成返回 `bool`,让 UI 说实话。

## 4. 验收

面板「雷」分组里应出现 **雷电海拔上限(米;0=关闭闸门,不限制)**,默认 **12000**。

真机判据(尚未执行,需在 Unity 重编程序集后打包):

1. 地面正常雷暴 → 雷照旧;把滑块调到低于当前海拔 → 立刻不再劈;
2. 飞进轨道(>12000m)→ **不再出现落雷**,日志出现
   `Volken:LightningModule 海拔闸门 → 不落雷(观测者 XXXXXm ≥ 上限 12000m;太空/云层之上不劈雷)`;
3. 在上限之上点「立刻劈一道」→ 提示**海拔过高:未落雷**,且不产生新的 `Volken:LightningBolt` 日志;
4. 降回上限之内 → **自动恢复**落雷,不需要重开开关。

## 5. 未做

- **真机验收**:本节四条判据一条都还没跑过。
- 落点在行星正球面上(而非贴地形)的近似**不在本次范围**,条目仍在 [README](../README.md) §四之三「二期可选项」。
