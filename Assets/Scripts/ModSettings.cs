namespace Assets.Scripts
{
    using ModApi.Common;
    using ModApi.Settings.Core;

    /// <summary>
    /// The settings for the mod.
    /// </summary>
    /// <seealso cref="ModApi.Settings.Core.SettingsCategory{Assets.Scripts.ModSettings}" />
    public class ModSettings : SettingsCategory<ModSettings>
    {
        /// <summary>
        /// The mod settings instance.
        /// </summary>
        private static ModSettings _instance;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModSettings"/> class.
        /// </summary>
        public ModSettings() : base("Volken")
        {
        }

        /// <summary>
        /// Gets the mod settings instance.
        /// </summary>
        /// <value>
        /// The mod settings instance.
        /// </value>
        public static ModSettings Instance => _instance ?? (_instance = Game.Instance.Settings.ModSettings.GetCategory<ModSettings>());

        /// <summary>
        /// Gets or sets the noise map index (1-5) used for the volumetric cloud shape.
        /// </summary>
        /// <value>
        /// The noise map index.
        /// </value>
        public NumericSetting<int> NoiseMapIndex { get; set; }

        /// <summary>
        /// Gets or sets the altitude (m) below which the water transparency override takes effect.
        /// </summary>
        /// <value>
        /// The minimum altitude in meters.
        /// </value>
        public NumericSetting<int> MinHeight { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether developer log messages are shown.
        /// </summary>
        /// <value>
        /// <c>true</c> to show dev logs; otherwise, <c>false</c>.
        /// </value>
        public BoolSetting DevMode { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the water transparency setting is overridden based on altitude.
        /// </summary>
        /// <value>
        /// <c>true</c> to alter water transparency; otherwise, <c>false</c>.
        /// </value>
        public BoolSetting AlterTransparency { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the in-game performance profiler overlay is shown.
        /// </summary>
        /// <value>
        /// <c>true</c> to show the profiler overlay; otherwise, <c>false</c>.
        /// </value>
        public BoolSetting ShowProfiler { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether volumetric clouds are rendered into the water reflection.
        /// </summary>
        /// <value>
        /// <c>true</c> to render clouds into the water reflection; otherwise, <c>false</c>.
        /// </value>
        public BoolSetting WaterReflection { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether volumetric clouds are auto-rendered on extra world cameras
        /// (PIP windows etc.).
        /// </summary>
        /// <value>
        /// <c>true</c> to render clouds on extra cameras; otherwise, <c>false</c>.
        /// </value>
        public BoolSetting ExtraCameraClouds { get; set; }

        /// <summary>
        /// Initializes the settings in the category.
        /// </summary>
        protected override void InitializeSettings()
        {
            // 注意:设置名称/描述使用 "{...}" 本地化键引用(游戏新版模式),由设置系统在渲染时惰性解析。
            // xmlName 显式固定为语言无关的稳定 id(沿用旧代码的派生键,可兼容玩家已保存的设置);
            // 若不指定 xmlName,存储键会从显示名派生,而显示名是本地化引用,切换语言后存储键随之改变,导致设置被重置。
            NoiseMapIndex = CreateNumeric<int>("{Volken.ModSettings.NoiseMapIndex}", 1, 5, 1, "noiseMap")
                .SetDescription("{Volken.ModSettings.NoiseMapIndexDesc}")
                .SetDisplayFormatter(x => x.ToString("F0"))
                .SetDefault(2);
            MinHeight = CreateNumeric<int>("{Volken.ModSettings.MinHeight}", 10, 100, 1, "minHeight")
                .SetDescription("{Volken.ModSettings.MinHeightDesc}")
                .SetDisplayFormatter(x => x.ToString("F0"))
                .SetDefault(10);
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
            ShowProfiler = CreateBool("{Volken.ModSettings.ShowProfiler}", "ShowProfiler")
                .SetDescription("{Volken.ModSettings.ShowProfilerDesc}")
                .SetDefault(false);
        }
    }
}
