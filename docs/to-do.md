# Volken 待办清单(进行中)

> 状态:📋 活跃 backlog(未修复项登记于此;已修复项同步归档索引,详见 [README.md](README.md))
> 维护:新增/修复项请保持本节结构;修复后把条目移到「已修复」并附日期与根因简述。

## 高优先度
### 切换至有大气星球时config卡住
> 未知原因?等待更多复现和log

## 低优先度

### 星环渲染顺序错误
### Craft在高轨道时云层scale错误

## 已修复
### 水面覆盖云层
### 切换至无大气SOI时config未同步
### TSS 边缘拖影(网格2 快速拖动)
> 运动自适应调优:阈值 120→200、无云分支 0.75→0.85,残影降至可接受(轻微但可接受,2026-08-27)。
### 坐标原点重置时 TSS 云偏移
> 根因:SR2 浮动原点重置(RecenterReferenceFrame)使世界坐标两帧间整体平移,prevViewProjMat 失效 → 时序重投影错位。
> 处理:订阅 ModApi IGameView.ReferenceFrameRecentered,清空时序历史 + frameNumber=0 冷启动(CloudRenderer.cs,2026-08-27)。
### JNO 联机 mod 冲突(NRE 中断 SceneLoaded 事件链)
> 根因:JNO 的 MultiPlayerUI.OnSceneLoaded 对 null inspectorPanel 解引用抛 NRE → 事件链中断 → Volken 初始化被跳过(看不到云、自带云开关锁死)。
> 处理:JNO 侧 OnSceneLoaded 加 null 保护(手动应用);Volken 侧自愈曾加后撤除。详见 [archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md](archive/Volken-冲突排查-JNOmultiplayerTest-SceneLoaded事件链NRE-2026-08-27.md)。