using System;
using UnityEngine;

namespace Volken.Clouds
{
    // 世界空间光学厚度格点;33³ 打包为 9×4 切片,观察射线只插值、不重复遮光步进。
    internal sealed class CloudLightningCache : IDisposable
    {
        internal const int Width = 297, Height = 132;
        private RenderTexture _atlas;
        private int _frame = -1;
        private Material _material;
        private Vector4 _source;
        private int _samples;

        internal void Invalidate() => _frame = -1;

        internal void Prepare(Material material)
        {
            var source = material.GetVector("_LightningPositionRange");
            int samples = material.GetInt("_LightningSamples");
            if (source.w <= 0f || samples < 2) return;
            int pass = material.FindPass("LightningShadow");
            if (pass < 0)
            {
                material.SetInt("_LightningSamples", 0);
                return;
            }
            if (_atlas == null || !_atlas.IsCreated())
            {
                Dispose();
                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf)
                    ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat;
                _atlas = new RenderTexture(Width, Height, 0, format, RenderTextureReadWrite.Linear)
                {
                    name = "VolkenLightningShadow", filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp, useMipMap = false
                };
                _atlas.Create();
            }
            if (_frame != Time.frameCount || _material != material || _source != source || _samples != samples)
            {
                var previous = RenderTexture.active;
                try { Graphics.Blit(Texture2D.blackTexture, _atlas, material, pass); }
                finally { RenderTexture.active = previous; }
                _frame = Time.frameCount;
                _material = material;
                _source = source;
                _samples = samples;
            }
            material.SetTexture("_LightningShadowTex", _atlas);
        }

        public void Dispose()
        {
            if (_atlas != null)
            {
                _atlas.Release();
                UnityEngine.Object.Destroy(_atlas);
                _atlas = null;
            }
            _frame = -1;
            _material = null;
        }
    }
}
