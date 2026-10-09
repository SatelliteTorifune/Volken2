namespace Assets.Scripts
{
    using ModApi.Common;
    using ModApi.Settings.Core;

    public class ModSettings : SettingsCategory<ModSettings>
    {
        private static ModSettings _instance;

        public ModSettings() : base("Volken")
        {
        }

        public static ModSettings Instance => _instance ?? (_instance = Game.Instance.Settings.ModSettings.GetCategory<ModSettings>());

        public NumericSetting<int> NoiseMapIndex { get; set; }

        /// 海拔上限(m):低于它才应用水透明度覆盖(配合 AlterTransparency)。
        public NumericSetting<int> MinHeight { get; set; }

        public BoolSetting DevMode { get; set; }

        public BoolSetting AlterTransparency { get; set; }

        public BoolSetting ShowProfiler { get; set; }

        public BoolSetting WaterReflection { get; set; }

        public BoolSetting ExtraCameraClouds { get; set; }

        /// <summary>给附加世界相机(PIP 窗口等)也挂雨:每台相机一套雨实例(约 amount×32B 显存 + 一份满额 compute)。</summary>
        public BoolSetting ExtraCameraRain { get; set; }
        public BoolSetting ExtraCameraFog { get; set; }

        protected override void InitializeSettings()
        {
            // xmlName 是 XML 序列化的稳定存储 key,必须显式给定:不指定会从显示名派生,而显示名是本地化引用,
            // 切语言 → key 变 → 玩家已保存的设置被重置。这里沿用旧代码的派生键,改掉同样会丢设置。
            NoiseMapIndex = CreateNumeric<int>("{Volken.ModSettings.NoiseMapIndex}", 1, 5, 1, "noiseMap")
                .SetDescription("{Volken.ModSettings.NoiseMapIndexDesc}")
                .SetDisplayFormatter(x => x.ToString("F0"))
                .SetDefault(2);
            MinHeight = CreateNumeric<int>("{Volken.ModSettings.MinHeight}", 10, 100, 1, "minHeight")
                .SetDescription("{Volken.ModSettings.MinHeightDesc}")
                .SetDisplayFormatter(x => x.ToString("F0"))
                .SetDefault(10);
            // 开发设置保留 XML 键与读写兼容。
            DevMode = CreateBool("{Volken.ModSettings.ShowDevLog}", "ShowDevLog")
                .SetDescription("{Volken.ModSettings.ShowDevLogDesc}")
                
                .SetDefault(false);
            AlterTransparency = CreateBool("{Volken.ModSettings.AlterTransparency}", "AlterTransparency")
                .SetDescription("{Volken.ModSettings.AlterTransparencyDesc}")
                .SetDefault(false);
            WaterReflection = CreateBool("{Volken.ModSettings.WaterReflection}", "WaterReflection")
                .SetDescription("{Volken.ModSettings.WaterReflectionDesc}")
                .SetDefault(false);
            ExtraCameraClouds = CreateBool("{Volken.ModSettings.ExtraCameraClouds}", "ExtraCameraClouds")
                .SetDescription("{Volken.ModSettings.ExtraCameraCloudsDesc}")
                .SetDefault(true);
            ExtraCameraRain = CreateBool("{Volken.ModSettings.ExtraCameraRain}", "ExtraCameraRain")
                .SetDescription("{Volken.ModSettings.ExtraCameraRainDesc}")
                .SetDefault(true);
            ExtraCameraFog = CreateBool("{Volken.ModSettings.ExtraCameraFog}", "ExtraCameraFog")
                .SetDescription("{Volken.ModSettings.ExtraCameraFogDesc}")
                .SetDefault(false);
            ShowProfiler = CreateBool("{Volken.ModSettings.ShowProfiler}", "ShowProfiler")
                .SetDescription("{Volken.ModSettings.ShowProfilerDesc}")
                
                .SetDefault(false);
        }
    }
}
