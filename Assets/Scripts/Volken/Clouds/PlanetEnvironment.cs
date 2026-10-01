namespace Volken.Clouds
{
    /// <summary>当前行星环境的一次性快照(纯值对象);由 <see cref="VolkenClouds"/> 解析行星后经 <see cref="VolkenClouds.PlanetChanged"/> 广播。</summary>
    public struct PlanetEnvironment
    {
        public string PlanetName;   // 当前 SOI 行星名(不在飞行场景 / 无 SOI 时为空)

        public bool InFlight;       // 非飞行场景不驱动任何模块的行星逻辑

        public bool IsCelestial;    // false = 没有 SOI,或绕着恒星

        public bool HasAtmosphere;

        public bool HasWater;       // 给水体相关设置用

        public bool SupportsAtmosphereEffects => InFlight && IsCelestial && HasAtmosphere;   // = 可以装云/天气的行星

        public static PlanetEnvironment Empty => new PlanetEnvironment
        {
            PlanetName = string.Empty,
            InFlight = false,
            IsCelestial = false,
            HasAtmosphere = false,
            HasWater = false,
        };

        public override string ToString()
        {
            return $"planet={PlanetName} inFlight={InFlight} celestial={IsCelestial} " +
                   $"atmo={HasAtmosphere} water={HasWater}";
        }
    }
}
