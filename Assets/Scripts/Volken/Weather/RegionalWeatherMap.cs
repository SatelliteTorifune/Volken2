using System;

namespace Volken.Weather
{
    /// <summary>不可变线性灰度覆盖图，行从南向北；横向周期、极点收敛，无逐查询分配。</summary>
    public sealed class RegionalWeatherMap
    {
        public const string AssetPath = "Assets/Scripts/Volken/Weather/Textures/RegionalCoverage.png";
        public int Width { get; }
        public int Height { get; }
        private readonly byte[] _coverage;
        private readonly double _south, _north;

        public RegionalWeatherMap(int width, int height, byte[] coverage)
        {
            if (width < 2 || height < 2 || width > 8192 || height > 8192 ||
                (long)width * height > 16777216 || coverage == null || coverage.Length != (long)width * height)
                throw new ArgumentException("Invalid regional coverage map dimensions or pixels.");
            Width = width;
            Height = height;
            _coverage = (byte[])coverage.Clone();
            long south = 0, north = 0;
            for (int i = 0; i < width; i++)
            {
                south += _coverage[i];
                north += _coverage[(height - 1) * width + i];
            }
            _south = south / (width * 255.0);
            _north = north / (width * 255.0);
        }

        /// <summary>u=经度/360+0.5，v=纬度/180+0.5；极点半像素内向整行均值收敛。</summary>
        public double SampleUv(double u, double v)
        {
            if (double.IsNaN(u) || double.IsInfinity(u) || double.IsNaN(v) || double.IsInfinity(v)) return double.NaN;
            double x = (u - Math.Floor(u)) * Width - 0.5;
            int left = (int)Math.Floor(x);
            double horizontal = x - left;
            if (left < 0) left += Width;
            int right = (left + 1) % Width;
            double y = Math.Max(0, Math.Min(1, v)) * Height - 0.5;
            if (y < 0) return Lerp(_south, Row(0, left, right, horizontal), (y + 0.5) * 2);
            if (y > Height - 1) return Lerp(_north, Row(Height - 1, left, right, horizontal), (Height - 0.5 - y) * 2);
            int bottom = (int)Math.Floor(y), top = Math.Min(bottom + 1, Height - 1);
            return Lerp(Row(bottom, left, right, horizontal), Row(top, left, right, horizontal), y - bottom);
        }

        private double Row(int row, int left, int right, double t) =>
            Lerp(_coverage[row * Width + left], _coverage[row * Width + right], t) / 255.0;
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
