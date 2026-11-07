using System;

namespace Volken.Weather
{
    /// <summary>水平地区倍率；高度、大气和用户开关由各效果继续门控。</summary>
    public readonly struct RegionalWeatherSample
    {
        public static readonly RegionalWeatherSample Identity = new RegionalWeatherSample(1f, 1f);
        public readonly float Rain;
        public readonly float Storm;

        public RegionalWeatherSample(float rain, float storm)
        {
            Rain = rain;
            Storm = storm;
        }
    }

    /// <summary>无状态球面天气场：双精度表面坐标、游戏秒数；不分配纹理或回读 GPU。</summary>
    public static class RegionalWeather
    {
        public static bool TrySample(VolkenWeatherConfig.RegionalSection settings, string planetName,
            double x, double y, double z, double radius, double simulationTime, out RegionalWeatherSample sample,
            RegionalWeatherMap map = null)
        {
            sample = RegionalWeatherSample.Identity;
            if (settings == null || !settings.enabled) return true;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!Finite(length) || length <= 0 || !Finite(radius) || radius <= 0 || !Finite(simulationTime) ||
                !Finite(settings.coverage) || !Finite(settings.cellSizeKm) || !Finite(settings.edgeSoftness) ||
                !Finite(settings.windSpeed) || !Finite(settings.stormFraction) || !Finite(settings.mapLongitudeOffset)) return false;

            double coverage = Clamp(settings.coverage, 0, 1);
            double storms = Clamp(settings.stormFraction, 0, 1);
            double scale = radius / (Clamp(settings.cellSizeKm, 1, 2000) * 1000);
            if (!Finite(scale)) return false;
            x /= length;
            y /= length;
            z /= length;

            // 游戏东向为 Cross(径向, up)，对平流做逆变换；先取周期余数，避免长存档相位失精。
            double wind = Clamp(settings.windSpeed, -200, 200);
            if (wind != 0)
            {
                double period = 2 * Math.PI * radius / Math.Abs(wind);
                if (!Finite(period) || period <= 0) return false;
                double angle = (simulationTime % period) * wind / radius;
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                double rotatedX = cosine * x + sine * z;
                z = -sine * x + cosine * z;
                x = rotatedX;
            }

            double distribution;
            bool useMap = settings.distribution == 1 && map != null;
            if (useMap)
            {
                double longitude = -Math.Atan2(x, z);
                double latitude = Math.Atan2(y, Math.Sqrt(x * x + z * z));
                distribution = map.SampleUv(longitude / (2 * Math.PI) + 0.5 -
                    Clamp(settings.mapLongitudeOffset, -180, 180) / 360, latitude / Math.PI + 0.5);
            }
            else distribution = 0;

            x *= scale;
            y *= scale;
            z *= scale;
            uint seed = Seed(settings.seed, planetName);
            if (!useMap) distribution = Noise(x, y, z, seed) * 0.75 +
                Noise(x * 2 + 19.3, y * 2 - 7.1, z * 2 + 41.7, seed ^ 0x9e3779b9u) * 0.25;
            double edge = Clamp(settings.edgeSoftness, 0.01, 0.5);
            double type = Noise(x * 0.75 - 31.5, y * 0.75 + 11.7, z * 0.75 + 5.3, seed ^ 0x85ebca6bu);
            if (!Finite(distribution) || !Finite(type)) return false;
            float rain = Weight(distribution, coverage, edge);
            sample = new RegionalWeatherSample(rain, rain * Weight(type, storms, edge));
            return true;
        }

        private static float Weight(double noise, double coverage, double edge)
        {
            if (coverage <= 0) return 0f;
            if (coverage >= 1) return 1f;
            double t = Clamp((noise - (1 - coverage - edge * 0.5)) / edge, 0, 1);
            return (float)(t * t * (3 - 2 * t));
        }

        private static double Noise(double x, double y, double z, uint seed)
        {
            double ix = Math.Floor(x), iy = Math.Floor(y), iz = Math.Floor(z);
            double u = Fade(x - ix), v = Fade(y - iy), w = Fade(z - iz);
            // 周期为百万个格点；取余后转换为整数，避免大半径溢出。
            int a = (int)(ix % 1048576), b = (int)(iy % 1048576), c = (int)(iz % 1048576);
            double lower = Lerp(Lerp(Hash(a, b, c, seed), Hash(a + 1, b, c, seed), u),
                Lerp(Hash(a, b + 1, c, seed), Hash(a + 1, b + 1, c, seed), u), v);
            double upper = Lerp(Lerp(Hash(a, b, c + 1, seed), Hash(a + 1, b, c + 1, seed), u),
                Lerp(Hash(a, b + 1, c + 1, seed), Hash(a + 1, b + 1, c + 1, seed), u), v);
            return Lerp(lower, upper, w);
        }

        private static double Hash(int x, int y, int z, uint seed)
        {
            unchecked
            {
                uint h = seed ^ ((uint)x & 1048575u) * 0x8da6b343u ^
                    ((uint)y & 1048575u) * 0xd8163841u ^ ((uint)z & 1048575u) * 0xcb1ab31fu;
                h ^= h >> 16;
                h *= 0x7feb352du;
                h ^= h >> 15;
                h *= 0x846ca68bu;
                h ^= h >> 16;
                return (h & 0x00ffffffu) / 16777215.0;
            }
        }

        private static uint Seed(int seed, string planetName)
        {
            unchecked
            {
                uint hash = (uint)seed ^ 2166136261u;
                if (planetName != null)
                    for (int i = 0; i < planetName.Length; i++) hash = (hash ^ planetName[i]) * 16777619u;
                return hash;
            }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    }
}
