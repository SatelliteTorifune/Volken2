namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
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

        ///// <summary>
        ///// Gets the TestSetting1 value
        ///// </summary>
        ///// <value>
        ///// The TestSetting1 value.
        ///// </value>
        public NumericSetting<int> NoiseMapIndex { get; private set; }
        public NumericSetting<int> MinHeight { get; private set; }
        public BoolSetting ShowDevLog{get; private set;}
        public BoolSetting AlterTransparency{get; private set;}
        

        /// <summary>
        /// Initializes the settings in the category.
        /// </summary>
        protected override void InitializeSettings()
        {
            this.NoiseMapIndex = this.CreateNumeric<int>("Noise Map", 1, 5, 1)
                .SetDescription("the noise map index.")
                .SetDisplayFormatter(x => x.ToString("F1"))
                .SetDefault(2);
            this.MinHeight = this.CreateNumeric<int>("MinHeight", 10, 100, 1)
                .SetDisplayFormatter(x => x.ToString("F0"))
                .SetDefault(10);
            this.ShowDevLog=this.CreateBool("Show Dev Log", "ShowDevLog").SetDefault(true).SetDescription("Show Log for devs or not");
            this.AlterTransparency=this.CreateBool("Alter Transparency", "AlterTransparency").SetDefault(true).SetDescription("the original transparency settings has been altered into this one");
        }
    }
}