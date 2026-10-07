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
    //  天气预设(一份预设 = 一个文件;5 个 Section = XML 节点 = 面板分组)。
    //  【兼容约定】新增字段初始值必须是"关闭/恒等";已移除的字段保留占位而非删除(删字段会让旧 XML 的同名节点被静默丢弃)。
    // --------------------------------------------------------------------------------------------

    /// <summary>一套天气预设(总体 / 云联动遗留占位 / 雨 / 雾 / 雷)。存于 <c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c> ——
    /// **独立目录**,与云的 <c>UserData/VolkenConfig/</c> 分开(否则同名预设互相覆盖);两边预设名也互相独立。</summary>
    [Serializable]
    public class VolkenWeatherConfig
    {
        [Serializable]
        public class OverallSection
        {
            public bool enabled = false;

            public void CopyFrom(OverallSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
            }
        }

        // ③ 雨(占位)

        /// <summary>雨子系统参数;密度/域半径的标定基线与踩坑见 <c>docs/sp2-rain-particledomain-port-2026-09-28.md</c> 与 <c>docs/archive/weather-rain-fog-postmortem-2026-09-27.md</c>。</summary>
        [Serializable]
        public class RainSection
        {
            public bool enabled = false;

            /// <summary>粒子数量上限。**默认 100000 = SP2 出厂值**(<c>_particleAmount</c>;R=50 时密度 0.191/m³):粒子太少则每根雨丝被单独看清 = 细线"面条"感;掉帧就在面板把这一项拉下来。</summary>
            public float amount = 100000f;

            public float domainRadius = 50f;   // 米

            public bool adaptiveDomain = true;

            public float fallSpeed = 15f;   // 米/秒

            public float strength = 1f;   // 强度倍率;1 = 标定基准

            public float windInfluence = 1f;   // 0 = 纯竖直下落

            public float streakLength = 2.5f;   // 米

            /// <summary>雨丝宽度(米)。**默认 0.1 = SP2 出厂值**(<c>_streakThickness</c>);曾误用 0.05(比 SP2 细一半)→ 细亮线"面条"观感。</summary>
            public float streakWidth = 0.1f;

            public float volume = 0.5f;   // 雨声音量

            /// <summary>雨丝朝向速度源:true = 沿相对速度(下落−飞行器速度,SP2 AlignStreaks 语义);false = 恒径向"下"(永远竖直,不看速度);两者都是世界系朝向,与相机无关。</summary>
            public bool streamMode = true;

            public float stretchAmount = 0.045f;   // SP2 _stretchAmount;0 = 不拉伸

            /// <summary>拉伸上限(SP2 _stretchLimit):雨丝最长 = streakLength × 本值(默认 3.5 → 8.75m),嫌"太长像面条"就把这项或拉伸系数调小。</summary>
            public float stretchLimit = 3.5f;

            public float edgeFade = 0.2f;   // 域边界淡出宽度(占球域半径比例);0 = 关,默认 0.2 = 最外 20% 渐隐,隐藏边界与出域回收的突现

            public float softParticles = 1f;   // SP2 _InvFade;0 = 关(硬边),1 = SP2 默认

            public float tailFalloff = 0.9f;   // SP2 _falloff;0 = 不淡,1 = 尾端全透明

            public float brightness = 0f;   // = shader _Emission;0 = 不额外提亮

            // 纵深线索(治"快速缩放时像一层平面"):整片雨丝等长、等亮、平行 → 没有纵深
            public float distanceFade = 0.35f;    // 整段域内的距离衰减(0 = 关;近了亮远了暗)
            public float streakVariation = 0.5f;  // 逐粒长度/宽度倍率(0 = 全一样长)

            // JNO 能把镜头缩到整颗星球,不加限制会在太空里下雨。
            //  阈值是雨**自己的**配置项,**不读 CloudConfig.maxCloudHeight**。

            public float ceilingAltitude = 12000f;   // 米;0 = 关闭闸门(不限制),超过上限不再下雨

            public float ceilingBand = 0.4f;   // 上限处的淡出带宽(占上限比例 0.02~1);默认 0.4 = 顶部 40% 渐隐到 0

            // 水下闸门:相机沉到海平面以下(有水行星)时整片不画 —— 高度闸门管不到水下这半边。

            public bool underwaterGate = true;   // 海平面以下不画雨

            public float underwaterFade = 2f;    // 米;海平面 → 水面下此深度内线性淡出到 0(贴水面浮动时不会闪断)

            // false = 域内随机重生(默认,持续混合 = 连续雨帘);true = 确定性镜像,会零混合、周期团块。

            public bool respawnMirror = false;   // true = 确定性镜像(仅对照用)

            public void CopyFrom(RainSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                amount = s.amount;
                domainRadius = s.domainRadius;
                adaptiveDomain = s.adaptiveDomain;
                fallSpeed = s.fallSpeed;
                strength = s.strength;
                windInfluence = s.windInfluence;
                streakLength = s.streakLength;
                streakWidth = s.streakWidth;
                volume = s.volume;
                streamMode = s.streamMode;
                stretchAmount = s.stretchAmount;
                stretchLimit = s.stretchLimit;
                edgeFade = s.edgeFade;
                softParticles = s.softParticles;
                tailFalloff = s.tailFalloff;
                brightness = s.brightness;
                distanceFade = s.distanceFade;
                streakVariation = s.streakVariation;
                ceilingAltitude = s.ceilingAltitude;
                ceilingBand = s.ceilingBand;
                underwaterGate = s.underwaterGate;
                underwaterFade = s.underwaterFade;
                respawnMirror = s.respawnMirror;
            }
        }

        // ④ 雾(占位)

        /// <summary>雾子系统参数(占位,无消费者);重做时深度取本相机 <c>CloudRenderer.LinearSceneDepth</c>。</summary>
        [Serializable]
        public class FogSection
        {
            public bool enabled = false;

            /// <summary>黎明起雾:在日出/日落前后(按 <c>VolkenWeather.LocalSolarHour</c>)自动抬升雾密度。**局限**:分不出日出侧与日落侧,两侧对等触发。</summary>
            public bool dawnFog = false;

            public float baseHeight = 0f;   // 米,相对行星半径

            public float height = 800f;   // 雾层厚度(米)

            public float density = 0f;   // 1/米 量级;SP2 预设里是 0.01 这一档

            public float heightFalloff = 0.5f;   // 越大雾越贴着地面

            public float maxOpacity = 1f;

            public float startDistance = 0f;   // 相机前方多远开始起雾(米)

            public float colorBlend = 0.5f;   // 雾色向天空色混合的比例

            public void CopyFrom(FogSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                dawnFog = s.dawnFog;
                baseHeight = s.baseHeight;
                height = s.height;
                density = s.density;
                heightFalloff = s.heightFalloff;
                maxOpacity = s.maxOpacity;
                startDistance = s.startDistance;
                colorBlend = s.colorBlend;
            }
        }

        /// <summary>雷(闪电 + 雷声);五个 Section 里唯一在跑的子系统。</summary>
        [Serializable]
        public class LightningSection
        {
            public bool enabled = false;

            public float minDelay = 6f;   // 秒

            public float maxDelay = 45f;   // 秒

            public float targetRange = 3000f;   // 米;落雷点相对目标(地面)的随机偏移半径

            public float spawnRange = 4000f;   // 米;源点相对云层中高的随机偏移半径

            /// <summary>海拔上限(米;0 = 关闭闸门,不限制):**观测者**高于它就不再落雷 —— JNO 能把镜头缩到整颗星球,
            /// 不限制就会"在太空里劈雷"。语义与 <see cref="RainSection.ceilingAltitude"/> 一致:按观测者高度判定,
            /// 阈值是雷**自己的**配置项,**不读 CloudConfig**。</summary>
            public float ceilingAltitude = 12000f;

            public int arcs = 20;   // 主干分叉段数(SP2 arcs = 20/2;实际分叉点在 i < arcs-2)

            public float inaccuracy = 0.5f;   // 主干每段抖动幅度(SP2 inaccuracy)

            public int splits = 4;   // 每段生成的分叉数(SP2 splits = 4,即 0..4 共 5 条)

            public float width = 10f;   // 主干线段宽度(LineRenderer widthMultiplier;SP2 = 10)

            public float intensity = 1f;   // 闪电亮度基线(材质 _Intensity;SP2 每段随机 0~2)

            public float flashIntensity = 50f;   // 闪光瞬间亮度

            public float lightIntensity = 8f;   // 落雷点光源强度

            public float lightRange = 8000f;   // 落雷点光源照射半径(米)

            public float thunderDelay = 0.05f;   // 秒;落雷后到雷声的延迟

            public float thunderVolume = 0.65f;   // 0~1

            public float thunderDistanceAttenuation = 1f;   // 0 = 恒音量 + 恒 0.05s 延迟,1 = 按 1/距离衰减 + 真实声速延迟

            /// <summary>near / far 雷声素材的分界距离(米):落点到观测者 ≤ 本值播 near,否则播 far。默认 2000 = 与 targetRange(3000)同量级,近/远两种都真能听到。</summary>
            public float thunderNearDistance = 2000f;

            /// <summary>音速取不到时的兜底值(米/秒,默认 343):声速在"无物理大气"或"高度 ≥ 大气顶"时恒为 0,必须兜底而不是除 0。</summary>
            public float thunderFallbackSpeedOfSound = 343f;

            /// <summary>雷击"声源距离"里"云底距离"的混合比例(0~1):雷声由整条放电通道(云底↔落点)发出,远处先听到声程最短的那段(通常是云底端),
            /// 所以真实延迟比"到落点距离 / 声速"短 —— 0 = 纯落点距离,1 = 取 min(落点, 云底)。</summary>
            public float thunderSourceBlend = 0.5f;

            public void CopyFrom(LightningSection s)
            {
                if (s == null) return;
                enabled = s.enabled;
                minDelay = s.minDelay;
                maxDelay = s.maxDelay;
                targetRange = s.targetRange;
                spawnRange = s.spawnRange;
                ceilingAltitude = s.ceilingAltitude;
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
                thunderNearDistance = s.thunderNearDistance;
                thunderFallbackSpeedOfSound = s.thunderFallbackSpeedOfSound;
                thunderSourceBlend = s.thunderSourceBlend;
            }
        }

        // 实例(顺序 = XML 节点顺序 = 面板分组顺序)

        [XmlElement("Overall")]
        public OverallSection overall = new OverallSection();

        [XmlElement("Rain")]
        public RainSection rain = new RainSection();

        [XmlElement("Fog")]
        public FogSection fog = new FogSection();

        [XmlElement("Lightning")]
        public LightningSection lightning = new LightningSection();

        /// <summary>默认配置 = **完全关闭**(新增特性默认不改变现有画面)。</summary>
        public static VolkenWeatherConfig CreateDefault()
        {
            return new VolkenWeatherConfig
            {
                overall = new OverallSection { enabled = false },
                rain = new RainSection(),
                fog = new FogSection(),
                lightning = new LightningSection(),
            };
        }

        /// <summary>空节点兜底:XML 里缺了某一节(手改删掉 / 旧文件)时 XmlSerializer 会把它留成 <c>null</c> —— 补上默认实例,否则面板与读参数的地方会到处 NRE。</summary>
        public void EnsureSections()
        {
            if (overall == null) overall = new OverallSection();
            if (rain == null) rain = new RainSection();
            if (fog == null) fog = new FogSection();
            if (lightning == null) lightning = new LightningSection();
        }

        /// <summary>默认值升级:**只重写"仍是旧默认值"的字段**(用户手动改过的一律不动)。XML 反序列化对已存在的字段用存档值,新默认值不会自动生效 —— 必须有这一步。
        /// 当前升级项:<c>rain.streakWidth</c> 0.05→0.1、<c>rain.amount</c> 20000→100000、<c>rain.stretchLimit</c> →3.5。</summary>
        public void UpgradeUneditedDefaults()
        {
            EnsureSections();
            int upgraded = 0;
            if (Mathf.Abs(rain.streakWidth - 0.05f) < 1e-4f) { rain.streakWidth = 0.1f; upgraded++; }
            if (Mathf.Abs(rain.amount - 20000f) < 1f) { rain.amount = 100000f; upgraded++; }
            if (Mathf.Abs(rain.stretchLimit) < 1e-4f) { rain.stretchLimit = 3.5f; upgraded++; }   // 旧存档无此字段
            if (upgraded > 0)
            {
                Mod.Diag("VolkenWeatherConfig: 默认值升级 {0} 项(雨丝宽度→0.1、粒子数量→100000、拉伸上限→3.5;" +
                         "仅当字段仍是旧默认值时改写,手动改过的不动)", upgraded);
            }
        }

        /// <summary>把 XML 里可能出现的越界值收拢到安全区间(手改配置/旧文件都可能带脏值,而它们会直接进 shader 与 compute)。</summary>
        public void ClampAll()
        {
            EnsureSections();

            // ---- ③ 雨(占位) ----
            rain.amount = Mathf.Clamp(rain.amount, 0f, 400000f);
            rain.domainRadius = Mathf.Clamp(rain.domainRadius, 10f, 400f);
            rain.fallSpeed = Mathf.Clamp(rain.fallSpeed, 0.1f, 200f);
            rain.strength = Mathf.Clamp(rain.strength, 0f, 4f);
            rain.windInfluence = Mathf.Clamp01(rain.windInfluence);
            rain.streakLength = Mathf.Clamp(rain.streakLength, 0.05f, 50f);
            rain.streakWidth = Mathf.Clamp(rain.streakWidth, 0.001f, 2f);
            rain.volume = Mathf.Clamp01(rain.volume);
            rain.stretchAmount = Mathf.Clamp(rain.stretchAmount, 0f, 1f);
            rain.stretchLimit = Mathf.Clamp(rain.stretchLimit, 1f, 20f);
            rain.edgeFade = Mathf.Clamp(rain.edgeFade, 0f, 0.5f);
            rain.softParticles = Mathf.Clamp(rain.softParticles, 0f, 3f);
            rain.tailFalloff = Mathf.Clamp01(rain.tailFalloff);
            rain.brightness = Mathf.Clamp(rain.brightness, 0f, 3f);
            rain.distanceFade = Mathf.Clamp01(rain.distanceFade);
            rain.streakVariation = Mathf.Clamp01(rain.streakVariation);
            rain.ceilingAltitude = Mathf.Clamp(rain.ceilingAltitude, 0f, 500000f);
            rain.ceilingBand = Mathf.Clamp(rain.ceilingBand, 0.02f, 1f);
            rain.underwaterFade = Mathf.Clamp(rain.underwaterFade, 0.1f, 50f);

            // ---- ④ 雾(占位) ----
            fog.height = Mathf.Clamp(fog.height, 1f, 20000f);
            fog.density = Mathf.Clamp(fog.density, 0f, 1f);
            fog.heightFalloff = Mathf.Clamp(fog.heightFalloff, 0.001f, 10f);
            fog.maxOpacity = Mathf.Clamp01(fog.maxOpacity);
            fog.startDistance = Mathf.Max(0f, fog.startDistance);
            fog.colorBlend = Mathf.Clamp01(fog.colorBlend);

            // ---- ⑤ 雷 ----
            lightning.minDelay = Mathf.Max(0.05f, lightning.minDelay);
            lightning.maxDelay = Mathf.Max(lightning.minDelay, lightning.maxDelay);
            lightning.targetRange = Mathf.Max(1f, lightning.targetRange);
            lightning.spawnRange = Mathf.Max(1f, lightning.spawnRange);
            lightning.ceilingAltitude = Mathf.Clamp(lightning.ceilingAltitude, 0f, 500000f);
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
            lightning.thunderNearDistance = Mathf.Max(1f, lightning.thunderNearDistance);
            lightning.thunderFallbackSpeedOfSound = Mathf.Clamp(lightning.thunderFallbackSpeedOfSound, 1f, 5000f);
            lightning.thunderSourceBlend = Mathf.Clamp01(lightning.thunderSourceBlend);
        }

        /// <summary>拷贝全部字段(供"重置为默认"/复制记录时用)。</summary>
        public void CopyFrom(VolkenWeatherConfig source)
        {
            if (source == null) return;
            source.EnsureSections();
            EnsureSections();

            overall.CopyFrom(source.overall);
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

        // 文件 IO(按预设名存,与 Volken.Clouds.CloudConfig 同一套逻辑)

        /// <summary>天气预设根目录(相对 <c>persistentDataPath</c>)。**刻意与云的 <c>/UserData/VolkenConfig/</c> 分开**:两边预设同名,放同一个目录会互相覆盖,
        /// 而且天气文件会混进云的 <c>GetAllConfigNames</c>(它按 <c>*.xml</c> 枚举整个行星目录)。</summary>
        public const string CONFIG_FOLDER = "/UserData/VolkenWeatherConfig/";

        public const string DefaultConfigName = "Default";   // 清单里没登记预设名时用的名字(与云层一致)

        /// <summary>某行星的天气预设目录(不存在则创建 —— 有副作用)。</summary>
        public static string GetConfigFolderPath(string planetName)
        {
            string folderPath = Application.persistentDataPath + CONFIG_FOLDER + planetName;
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            return folderPath;
        }

        public static string GetConfigPath(string planetName, string configName)
        {
            return Path.Combine(GetConfigFolderPath(planetName), configName + ".xml");
        }

        /// <summary>该行星下这份预设文件是否**真实存在**。与 <see cref="GetConfigPath"/> 不同:不建目录、不写盘(查存在不该有副作用)。</summary>
        public static bool Exists(string planetName, string configName)
        {
            try
            {
                if (string.IsNullOrEmpty(planetName) || string.IsNullOrEmpty(configName)) return false;
                string path = Path.Combine(Application.persistentDataPath + CONFIG_FOLDER, planetName, configName + ".xml");
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>这颗行星已有的**天气预设名**列表(扫目录,与 <c>CloudConfig.GetAllConfigNames</c> 同逻辑);天气预设**独立**,与云的预设列表无关。</summary>
        public static List<string> GetAllConfigNames(string planetName)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(planetName)) return names;

            // 刻意不用 GetConfigFolderPath —— 它顺手建目录,而"查列表"不该有副作用
            string folder = Application.persistentDataPath + CONFIG_FOLDER + planetName;
            if (!Directory.Exists(folder)) return names;

            foreach (string f in Directory.GetFiles(folder, "*.xml"))
            {
                names.Add(Path.GetFileNameWithoutExtension(f));
            }
            return names;
        }

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

        /// <summary>读 <c>{行星}/{预设}.xml</c>;不存在 → 建一份默认(**全关**)并落盘,与云层同约定。读进来的实例一律过 <see cref="EnsureSections"/> + <see cref="ClampAll"/>,免脏值进 shader。</summary>
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
                    config.UpgradeUneditedDefaults();
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

