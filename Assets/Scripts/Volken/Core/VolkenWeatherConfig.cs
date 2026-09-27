using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Assets.Scripts;
using UnityEngine;
using Application = UnityEngine.Application;


namespace Volken.Weather
{
    
    // --------------------------------------------------------------------------------------------
    //  天气预设(一份预设 = 一个文件;5 个 Section = XML 节点 = 面板分组,三者同名同序)
    //
    //  【兼容约定】XML 反序列化对新字段自动取 C# 字段初始值 —— 因此新增字段只要
    //  **字段初始值 == 关闭/恒等**,就不会改变老玩家的行为(与 CloudConfig 同约定)。
    //  已被移除的子系统字段**保留占位**而不是删除:删字段会让旧 XML 的同名节点被静默丢弃,
    //  将来重做雨/雾时玩家手调过的参数就找不回来了。
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// 一套天气预设的参数。字段按「总体 / 云层联动 / 雨 / 雾 / 雷」五块分组,
    /// 每块的子类在 XML 里就是独立节点,与面板上的分组一一对应。
    ///
    /// 【存法与切换】一份预设 = 一个文件
    /// <c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c>,与
    /// <see cref="Volken.Clouds.CloudConfig"/> 完全同构,但**预设名与云层彼此独立** ——
    /// 每颗行星在 <c>&lt;PlanetConfig WeatherConfigName&gt;</c> 上单独记一个天气预设名,
    /// 可以自由新建/保存/读取,不影响云(反之亦然)。
    ///
    /// 【设计取向 —— 与 SP2 的根本差异,别搞混】
    /// SP2 有一套**全局天气档位系统**(<c>WeatherTypes</c>:Clear/Few/Broken/Overcast/Rainy/Stormy/Heavy…),
    /// 由它统一决定云层、雨、雾等各子系统的预设与阈值。
    /// **Volken 刻意不要这套东西**:这里的"预设"只是**玩家自己命名的一整套逐项参数**
    /// (性质与云层预设相同),不是 SP2 那种固定档位;面板上就是一排直接的数值滑块,
    /// 没有"天气档位"这一层间接。
    /// 所以这里**没有引用任何 SP2 的天气常量**:默认值都是字面量,阈值也各自独立成字段。
    /// </summary>
    [Serializable]
    public class VolkenWeatherConfig
    {
        /// <summary>
        /// 「天气值」这个连续标度的默认值(0.25)。
        ///
        /// 【注意:这不是 SP2 的"少云档"】Volken 里它只是"天气值"这个 0~3 连续量的一个**默认起点**,
        /// 没有任何档位含义;玩家可以把它设成任意值。原先它取自 SP2 的 <c>WeatherTypes.Few</c>,
        /// 那个类型已按用户要求删除。
        /// </summary>
        public const float DefaultWeatherValue = 0.25f;

        // ================= ① 总体 =================

        /// <summary>总体:总开关 / 动态天气 / 天气节奏。</summary>
        [Serializable]
        public class OverallSection
        {
            /// <summary>该行星是否启用天气。默认关 —— 新增特性默认不改变现有画面。</summary>
            public bool enabled = false;

            /// <summary>是否启用动态天气(关掉则天气值恒为 <see cref="fixedWeatherValue"/>)。</summary>
            public bool dynamicWeather = true;

            /// <summary><see cref="dynamicWeather"/> 关闭时使用的固定天气值。</summary>
            public float fixedWeatherValue = DefaultWeatherValue;

            /// <summary>起始天气值(进入该行星时的初值)。</summary>
            public float initialWeatherValue = DefaultWeatherValue;

            /// <summary>
            /// 随机天气目标的**取值上限**(下限恒为 0)。
            /// 原先这是 SP2 的 <c>[0, 3]</c> 硬编码区间,现在是玩家可调的字段。
            /// </summary>
            public float maxWeatherValue = 3f;

            /// <summary>每次随机天气的持续时长下限(秒)。SP2 默认 480。</summary>
            public float minDuration = 480f;

            /// <summary>每次随机天气的持续时长上限(秒)。SP2 默认 960。</summary>
            public float maxDuration = 960f;

            /// <summary>天气值淡变速度(单位/秒,SP2 的 <c>_weatherFadeSpeedMultiplier</c> = 0.001)。</summary>
            public float fadeSpeed = 0.001f;

            /// <summary>淡变更新间隔(秒,SP2 的 <c>WeatherUpdateInterval</c> = 0.5)。</summary>
            public float updateInterval = 0.5f;

            /// <summary>天气随时间的推进倍率(SR2 时间加速时天气也跟着快进;1 = 与游戏时间同速)。</summary>
            public float timeScaleFollow = 1f;

            /// <summary>是否启用"黎明起雾"特例(SP2:TimeOfDay 在 5~6 且天气 &lt; 0.5 → 强制转雾)。</summary>
            public bool foggyDawn = true;

            public void CopyFrom(OverallSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                dynamicWeather = s.dynamicWeather;
                fixedWeatherValue = s.fixedWeatherValue;
                initialWeatherValue = s.initialWeatherValue;
                maxWeatherValue = s.maxWeatherValue;
                minDuration = s.minDuration;
                maxDuration = s.maxDuration;
                fadeSpeed = s.fadeSpeed;
                updateInterval = s.updateInterval;
                timeScaleFollow = s.timeScaleFollow;
                foggyDawn = s.foggyDawn;
            }
        }

        // ================= ② 云层联动 =================

        /// <summary>
        /// 云层联动:由**天气值**去改云层表现的机制。
        ///
        /// 【默认全关,且当前实现里没有消费者】
        /// 2026-09-27 的决定是"天气不联动云层"(理由见 <see cref="VolkenWeather"/> 里的说明),
        /// 曾经的三个 gain 字段与实现一起被移除。这里按用户要求把**机制窗口与字段保留成占位**:
        ///   - 面板上会显示这一组,但整组禁用 + 灰字说明"待实现";
        ///   - 字段默认 0 = 恒等,即使将来接通也不会改变现有画面;
        ///   - 恢复实现时只需在 <see cref="VolkenWeather"/> 里重新加回消费点,配置层不用动。
        /// </summary>
        [Serializable]
        public class CloudLinkageSection
        {
            /// <summary>是否启用云层联动(总闸)。</summary>
            public bool enabled = false;

            /// <summary>天气值 → 云覆盖度增益(0 = 恒等)。</summary>
            public float coverageGain = 0f;

            /// <summary>天气值 → 云色暗化增益(0 = 恒等)。</summary>
            public float darkenGain = 0f;

            /// <summary>天气值 → 云风速增益(0 = 恒等)。</summary>
            public float windGain = 0f;

            public void CopyFrom(CloudLinkageSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                coverageGain = s.coverageGain;
                darkenGain = s.darkenGain;
                windGain = s.windGain;
            }
        }

        // ================= ③ 雨(占位) =================

        /// <summary>
        /// 雨:占位字段 + 总开关。
        ///
        /// 【2026-09-27:实现已整体移除】<c>Rain.cs</c> / <c>RainParticles.compute</c> /
        /// <c>RainParticles.shader</c> 与雨声素材已 stash 到 <c>%TEMP%\volken-rain-fog-stash</c>
        /// (复盘见 <c>docs/Volken-天气雨雾移植失败教训-2026-09-27.md</c>)。
        /// **这些字段没有任何消费者** —— 改它们不会有雨。保留是为了:
        ///   1. 面板分组结构完整;
        ///   2. 重做雨时参数位与玩家已经手调过的值都还在。
        /// 面板上这一组整体禁用并标注"待实现"。
        ///
        /// 重做时的标定基线(来自 EVE 的 rain-Kerbin 实测):密度 ≈ 0.139 个/m³、
        /// 域半径与"相机速度 × 停留时长"同量级 —— 见计划 §10.2c2 / 复盘 §5.1。
        /// </summary>
        [Serializable]
        public class RainSection
        {
            /// <summary>是否启用雨(占位:无消费者)。</summary>
            public bool enabled = false;

            /// <summary>
            /// 触发雨的天气值下限(占位:无消费者)。
            /// 原先写死为 SP2 的 <c>WeatherTypes.RainTrigger = 2.25</c>,现改成独立字段。
            /// </summary>
            public float triggerValue = 2.25f;

            /// <summary>粒子数量上限。</summary>
            public float amount = 20000f;

            /// <summary>粒子域半径(米)。</summary>
            public float domainRadius = 50f;

            /// <summary>域半径是否随相机速度自适应放大。</summary>
            public bool adaptiveDomain = true;

            /// <summary>下落速度(米/秒)。</summary>
            public float fallSpeed = 15f;

            /// <summary>雨的强度倍率(1 = 标定基准)。</summary>
            public float strength = 1f;

            /// <summary>受风影响的程度(0 = 纯竖直下落)。</summary>
            public float windInfluence = 1f;

            /// <summary>雨丝长度(米)。</summary>
            public float streakLength = 2.5f;

            /// <summary>雨丝宽度(米)。</summary>
            public float streakWidth = 0.05f;

            /// <summary>雨声音量。</summary>
            public float volume = 0.5f;

            public void CopyFrom(RainSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                triggerValue = s.triggerValue;
                amount = s.amount;
                domainRadius = s.domainRadius;
                adaptiveDomain = s.adaptiveDomain;
                fallSpeed = s.fallSpeed;
                strength = s.strength;
                windInfluence = s.windInfluence;
                streakLength = s.streakLength;
                streakWidth = s.streakWidth;
                volume = s.volume;
            }
        }

        // ================= ④ 雾(占位) =================

        /// <summary>
        /// 雾:占位字段 + 总开关。
        ///
        /// 【2026-09-27:实现已整体移除】<c>FogRenderer.cs</c> / <c>HeightFog.shader</c> 已 stash(同上)。
        /// **这些字段没有任何消费者。** 重做雾时深度来源仍然取
        /// <c>CloudRenderer.LinearSceneDepth</c>(该只读口**保留着**,只是暂时没有消费者)。
        /// </summary>
        [Serializable]
        public class FogSection
        {
            /// <summary>是否启用高度雾(占位:无消费者)。</summary>
            public bool enabled = false;

            /// <summary>雾底高度(米,相对行星半径)。</summary>
            public float baseHeight = 0f;

            /// <summary>雾层厚度(米)。</summary>
            public float height = 800f;

            /// <summary>雾密度(1/米 量级,SP2 预设里是 0.01 这一档)。</summary>
            public float density = 0f;

            /// <summary>高度衰减系数(越大雾越贴着地面)。</summary>
            public float heightFalloff = 0.5f;

            /// <summary>最大不透明度封顶。</summary>
            public float maxOpacity = 1f;

            /// <summary>相机前方多远开始起雾(米)。</summary>
            public float startDistance = 0f;

            /// <summary>雾色向天空色混合的比例。</summary>
            public float colorBlend = 0.5f;

            public void CopyFrom(FogSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                baseHeight = s.baseHeight;
                height = s.height;
                density = s.density;
                heightFalloff = s.heightFalloff;
                maxOpacity = s.maxOpacity;
                startDistance = s.startDistance;
                colorBlend = s.colorBlend;
            }
        }

        // ================= ⑤ 雷 =================

        /// <summary>雷:唯一在跑的子系统(闪电 + 雷声)。</summary>
        [Serializable]
        public class LightningSection
        {
            /// <summary>是否启用雷电。</summary>
            public bool enabled = false;

            /// <summary>
            /// 触发雷击的天气值下限(SP2 的 <c>lightningStorm</c> 语义:preset=="Stormy" 且混合权重 &gt; 0.5;
            /// 数值上对应 <c>WeatherTypes.Stormy = 2.5</c>)。**这里做成可调字段**,与"逐项设置"的取向一致。
            /// </summary>
            public float stormValue = 2.5f;

            /// <summary>两次雷击的间隔下限(秒)。</summary>
            public float minDelay = 6f;

            /// <summary>两次雷击的间隔上限(秒)。</summary>
            public float maxDelay = 45f;

            /// <summary>落雷点相对目标(地面)的随机偏移半径(米)。</summary>
            public float targetRange = 3000f;

            /// <summary>雷击源点相对云层中高的随机偏移半径(米)。</summary>
            public float spawnRange = 4000f;

            /// <summary>主干分叉段数(SP2 的 arcs = 20/2,实际分叉点在 i &lt; arcs-2)。</summary>
            public int arcs = 20;

            /// <summary>主干每段抖动幅度(SP2 的 inaccuracy = 0.5)。</summary>
            public float inaccuracy = 0.5f;

            /// <summary>每段生成的分叉数(SP2 的 splits = 4,即 0..4 共 5 条)。</summary>
            public int splits = 4;

            /// <summary>主干线段宽度(LineRenderer widthMultiplier,SP2 = 10)。</summary>
            public float width = 10f;

            /// <summary>闪电亮度基线(材质 _Intensity,SP2 每段随机 0~2)。</summary>
            public float intensity = 1f;

            /// <summary>闪光瞬间亮度(SP2 的 flashIntensity = 50)。</summary>
            public float flashIntensity = 50f;

            /// <summary>落雷点光源强度。</summary>
            public float lightIntensity = 8f;

            /// <summary>落雷点光源照射半径(米)。</summary>
            public float lightRange = 8000f;

            /// <summary>落雷后到雷声的延迟(秒,SP2 = 0.05)。</summary>
            public float thunderDelay = 0.05f;

            /// <summary>雷声音量(0~1)。</summary>
            public float thunderVolume = 0.65f;

            /// <summary>雷声是否按距离衰减(0 = 恒音量,1 = 按 1/距离 衰减)。</summary>
            public float thunderDistanceAttenuation = 0.6f;

            public void CopyFrom(LightningSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                stormValue = s.stormValue;
                minDelay = s.minDelay;
                maxDelay = s.maxDelay;
                targetRange = s.targetRange;
                spawnRange = s.spawnRange;
                arcs = s.arcs;
                inaccuracy = s.inaccuracy;
                splits = s.splits;
                width = s.width;
                intensity = s.intensity;
                flashIntensity = s.flashIntensity;
                lightIntensity = s.lightIntensity;
                lightRange = s.lightRange;
                thunderDelay = s.thunderDelay;
                thunderVolume = s.thunderVolume;
                thunderDistanceAttenuation = s.thunderDistanceAttenuation;
            }
        }

        // ================= 实例(顺序 = XML 节点顺序 = 面板分组顺序) =================

        [XmlElement("Overall")]
        public OverallSection overall = new OverallSection();

        [XmlElement("CloudLinkage")]
        public CloudLinkageSection cloudLinkage = new CloudLinkageSection();

        [XmlElement("Rain")]
        public RainSection rain = new RainSection();

        [XmlElement("Fog")]
        public FogSection fog = new FogSection();

        [XmlElement("Lightning")]
        public LightningSection lightning = new LightningSection();

        // ================= 工具 =================

        /// <summary>默认配置 = **完全关闭**。新增特性默认不改变现有画面。</summary>
        public static VolkenWeatherConfig CreateDefault()
        {
            return new VolkenWeatherConfig
            {
                overall = new OverallSection
                {
                    enabled = false,
                    dynamicWeather = true,
                    fixedWeatherValue = DefaultWeatherValue,
                    initialWeatherValue = DefaultWeatherValue,
                },
                cloudLinkage = new CloudLinkageSection(),
                rain = new RainSection(),
                fog = new FogSection(),
                lightning = new LightningSection(),
            };
        }

        /// <summary>
        /// 空节点兜底:XML 里若缺了某一节(手改删掉了整段 / 旧文件),
        /// XmlSerializer 会把它留成 <c>null</c> —— 这里补上默认实例,
        /// 否则面板与读参数的地方会到处 NRE。
        /// </summary>
        public void EnsureSections()
        {
            if (overall == null) overall = new OverallSection();
            if (cloudLinkage == null) cloudLinkage = new CloudLinkageSection();
            if (rain == null) rain = new RainSection();
            if (fog == null) fog = new FogSection();
            if (lightning == null) lightning = new LightningSection();
        }

        /// <summary>
        /// 把 XML 里可能出现的越界值收拢到安全区间(手改配置/旧文件都可能带脏值,
        /// 这些值会直接进 shader 与 compute,必须挡在门口)。
        /// </summary>
        public void ClampAll()
        {
            EnsureSections();

            // ---- ① 总体 ----
            overall.maxWeatherValue = Mathf.Clamp(overall.maxWeatherValue, 0f, 10f);
            overall.minDuration = Mathf.Max(1f, overall.minDuration);
            overall.maxDuration = Mathf.Max(overall.minDuration, overall.maxDuration);
            overall.fadeSpeed = Mathf.Max(1e-5f, overall.fadeSpeed);
            overall.updateInterval = Mathf.Max(0.01f, overall.updateInterval);
            overall.timeScaleFollow = Mathf.Clamp(overall.timeScaleFollow, 0f, 20f);

            // ---- ② 云层联动 ----
            cloudLinkage.coverageGain = Mathf.Clamp(cloudLinkage.coverageGain, -1f, 1f);
            cloudLinkage.darkenGain = Mathf.Clamp(cloudLinkage.darkenGain, 0f, 1f);
            cloudLinkage.windGain = Mathf.Clamp(cloudLinkage.windGain, -1f, 1f);

            // ---- ③ 雨(占位) ----
            rain.triggerValue = Mathf.Clamp(rain.triggerValue, 0f, 10f);
            rain.amount = Mathf.Clamp(rain.amount, 0f, 400000f);
            rain.domainRadius = Mathf.Clamp(rain.domainRadius, 10f, 400f);
            rain.fallSpeed = Mathf.Clamp(rain.fallSpeed, 0.1f, 200f);
            rain.strength = Mathf.Clamp(rain.strength, 0f, 4f);
            rain.windInfluence = Mathf.Clamp01(rain.windInfluence);
            rain.streakLength = Mathf.Clamp(rain.streakLength, 0.05f, 50f);
            rain.streakWidth = Mathf.Clamp(rain.streakWidth, 0.001f, 2f);
            rain.volume = Mathf.Clamp01(rain.volume);

            // ---- ④ 雾(占位) ----
            fog.height = Mathf.Clamp(fog.height, 1f, 20000f);
            fog.density = Mathf.Clamp(fog.density, 0f, 1f);
            fog.heightFalloff = Mathf.Clamp(fog.heightFalloff, 0.001f, 10f);
            fog.maxOpacity = Mathf.Clamp01(fog.maxOpacity);
            fog.startDistance = Mathf.Max(0f, fog.startDistance);
            fog.colorBlend = Mathf.Clamp01(fog.colorBlend);

            // ---- ⑤ 雷 ----
            lightning.stormValue = Mathf.Clamp(lightning.stormValue, 0f, 10f);
            lightning.minDelay = Mathf.Max(0.05f, lightning.minDelay);
            lightning.maxDelay = Mathf.Max(lightning.minDelay, lightning.maxDelay);
            lightning.targetRange = Mathf.Max(1f, lightning.targetRange);
            lightning.spawnRange = Mathf.Max(1f, lightning.spawnRange);
            lightning.arcs = Mathf.Clamp(lightning.arcs, 4, 64);
            lightning.inaccuracy = Mathf.Clamp(lightning.inaccuracy, 0f, 2f);
            lightning.splits = Mathf.Clamp(lightning.splits, 0, 8);
            lightning.width = Mathf.Clamp(lightning.width, 0.1f, 200f);
            lightning.intensity = Mathf.Max(0f, lightning.intensity);
            lightning.flashIntensity = Mathf.Max(0f, lightning.flashIntensity);
            lightning.lightIntensity = Mathf.Clamp(lightning.lightIntensity, 0f, 100f);
            lightning.lightRange = Mathf.Clamp(lightning.lightRange, 1f, 1e7f);
            lightning.thunderDelay = Mathf.Clamp(lightning.thunderDelay, 0f, 10f);
            lightning.thunderVolume = Mathf.Clamp01(lightning.thunderVolume);
            lightning.thunderDistanceAttenuation = Mathf.Clamp01(lightning.thunderDistanceAttenuation);
        }

        /// <summary>拷贝全部字段(供"重置为默认"/复制记录时用)。</summary>
        public void CopyFrom(VolkenWeatherConfig source)
        {
            if (source == null) return;
            source.EnsureSections();
            EnsureSections();

            overall.CopyFrom(source.overall);
            cloudLinkage.CopyFrom(source.cloudLinkage);
            rain.CopyFrom(source.rain);
            fog.CopyFrom(source.fog);
            lightning.CopyFrom(source.lightning);
        }

        public VolkenWeatherConfig Clone()
        {
            var c = CreateDefault();
            c.CopyFrom(this);
            return c;
        }

        // ================= 文件 IO(按预设名存,与 Volken.Clouds.CloudConfig 同一套逻辑) =================

        /// <summary>
        /// 天气预设根目录(相对 <c>persistentDataPath</c>)。
        /// **刻意与云的 <c>/UserData/VolkenConfig/</c> 分开**:两边预设同名,
        /// 放同一个目录会互相覆盖,而且天气文件会混进云的
        /// <c>GetAllConfigNames</c>(它按 <c>*.xml</c> 枚举整个行星目录)。
        /// </summary>
        public const string CONFIG_FOLDER = "/UserData/VolkenWeatherConfig/";

        /// <summary>清单里没登记预设名时用的名字(与云层一致)。</summary>
        public const string DefaultConfigName = "Default";

        /// <summary>某行星的天气预设目录(不存在则创建)。</summary>
        public static string GetConfigFolderPath(string planetName)
        {
            string folderPath = Application.persistentDataPath + CONFIG_FOLDER + planetName;
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            return folderPath;
        }

        /// <summary>某行星某预设的天气文件路径。</summary>
        public static string GetConfigPath(string planetName, string configName)
        {
            return Path.Combine(GetConfigFolderPath(planetName), configName + ".xml");
        }

        /// <summary>
        /// 这颗行星已有的**天气预设名**列表(扫目录,与 <c>CloudConfig.GetAllConfigNames</c> 同逻辑)。
        /// 天气预设是**独立**的 —— 这份列表与云的预设列表没有任何关系。
        /// </summary>
        public static List<string> GetAllConfigNames(string planetName)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(planetName)) return names;

            // 注意:这里刻意不用 GetConfigFolderPath —— 它顺手建目录,而"查列表"不该有副作用
            string folder = Application.persistentDataPath + CONFIG_FOLDER + planetName;
            if (!Directory.Exists(folder)) return names;

            foreach (string f in Directory.GetFiles(folder, "*.xml"))
            {
                names.Add(Path.GetFileNameWithoutExtension(f));
            }
            return names;
        }

        /// <summary>把这份天气参数写成 <c>{行星}/{预设}.xml</c>。</summary>
        public void SaveToFile(string planetName, string configName)
        {
            try
            {
                string filePath = GetConfigPath(planetName, configName);
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                XmlSerializer serializer = new XmlSerializer(typeof(VolkenWeatherConfig));
                using (FileStream stream = new FileStream(filePath, FileMode.Create))
                {
                    serializer.Serialize(stream, this);
                }
                Mod.Log($"Weather config '{configName}' saved to: {filePath}");
            }
            catch (Exception e)
            {
                Mod.Log($"Failed to save weather config '{configName}': {e.Message}");
            }
        }

        /// <summary>
        /// 读 <c>{行星}/{预设}.xml</c>;不存在 → 建一份默认(**全关**)并落盘,与云层同约定。
        /// 读进来的实例一律过一遍 <see cref="EnsureSections"/> + <see cref="ClampAll"/>,
        /// 以免手改/旧文件带脏值进 shader。
        /// </summary>
        public static VolkenWeatherConfig LoadFromFile(string planetName, string configName)
        {
            string filePath = GetConfigPath(planetName, configName);

            if (!File.Exists(filePath))
            {
                Mod.Log($"Weather config '{configName}' not found at {filePath}. Creating default config.");
                VolkenWeatherConfig created = CreateDefault();
                created.SaveToFile(planetName, configName);
                return created;
            }

            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(VolkenWeatherConfig));
                using (FileStream stream = new FileStream(filePath, FileMode.Open))
                {
                    VolkenWeatherConfig config = serializer.Deserialize(stream) as VolkenWeatherConfig;
                    if (config == null) return CreateDefault();
                    config.EnsureSections();
                    config.ClampAll();
                    Mod.Log($"Weather config '{configName}' loaded from: {filePath}");
                    return config;
                }
            }
            catch (Exception e)
            {
                Mod.Log($"Failed to load weather config '{configName}': {e.Message}. Using default config.");
                return CreateDefault();
            }
        }
    }
}


