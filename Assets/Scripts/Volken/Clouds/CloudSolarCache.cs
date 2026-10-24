using System;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Clouds
{
    // 每相机、每层的有限世界格点；每次绘制重建，覆盖外或不可信插值回退原追光。
    internal sealed class CloudSolarCache : IDisposable
    {
        private RenderTexture _atlas;
        private CommandBuffer _commands;
        private string _lastError;
        private static readonly int Enabled = Shader.PropertyToID("_CloudSolarCacheEnabled");
        private static readonly int Atlas = Shader.PropertyToID("_CloudSolarCacheTex");
        private static readonly int Grid = Shader.PropertyToID("_CloudSolarCacheMin");
        private static readonly int Layout = Shader.PropertyToID("_CloudSolarCacheLayout");
        internal static readonly UnityEngine.Profiling.CustomSampler BuildSampler =
            UnityEngine.Profiling.CustomSampler.Create("Volken.Clouds.SolarCache", true);

        internal bool Prepare(Material material, CloudConfig config, Vector3 cameraPosition)
        {
            Disable(material);
            if (material == null || config == null || !config.solarCacheEnabled)
            {
                Dispose();
                return false;
            }
            int pass = material.FindPass("SolarCache");
            if (pass < 0 || material.GetInt("numLightSamplePoints") <= 2 ||
                !Finite(cameraPosition) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat))
            {
                Dispose();
                return false;
            }

            config.ClampAccelerationSettings();
            int size = config.solarCacheResolution;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(size));
            int width = columns * size, height = ((size + columns - 1) / columns) * size;
            float cell = config.solarCacheExtent / (size - 1);
            var center = new Vector3(
                Mathf.Round(cameraPosition.x / cell) * cell,
                Mathf.Round(cameraPosition.y / cell) * cell,
                Mathf.Round(cameraPosition.z / cell) * cell);
            Vector3 minimum = center - Vector3.one * (config.solarCacheExtent * 0.5f);
            if (!Finite(minimum)) return false;

            var previous = RenderTexture.active;
            try
            {
                if (_atlas == null || !_atlas.IsCreated() || _atlas.width != width || _atlas.height != height)
                {
                    Dispose();
                    _atlas = new RenderTexture(width, height, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear)
                    {
                        name = "VolkenSolarCache", filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false
                    };
                    if (!_atlas.Create()) { Dispose(); return false; }
                }
                material.SetVector(Grid, new Vector4(minimum.x, minimum.y, minimum.z, 1f / cell));
                material.SetVector(Layout, new Vector4(size, columns, width, height));
                if (_commands == null) _commands = new CommandBuffer { name = "Volken Solar Cache" };
                _commands.Clear();
                _commands.BeginSample(BuildSampler);
                _commands.Blit(Texture2D.blackTexture, _atlas, material, pass);
                _commands.EndSample(BuildSampler);
                Graphics.ExecuteCommandBuffer(_commands);
                material.SetTexture(Atlas, _atlas);
                material.SetFloat(Enabled, 1f);
                _lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                Disable(material);
                Dispose();
                if (_lastError != ex.Message)
                {
                    _lastError = ex.Message;
                    Mod.Log("Cloud solar cache unavailable, using direct tracing: {0}", ex);
                }
                return false;
            }
            finally { RenderTexture.active = previous; }
        }

        internal static void Disable(Material material)
        {
            if (material == null) return;
            material.SetFloat(Enabled, 0f);
            material.SetTexture(Atlas, Texture2D.blackTexture);
        }

        internal static void SelectVariant(Material material)
        {
            if (material.GetFloat(Enabled) > 0.5f) material.EnableKeyword("VOLKEN_CLOUD_SOLAR_CACHE");
            else material.DisableKeyword("VOLKEN_CLOUD_SOLAR_CACHE");
            if (material.GetFloat("_CloudEmptySpaceEnabled") > 0.5f) material.EnableKeyword("VOLKEN_CLOUD_EMPTY_SPACE");
            else material.DisableKeyword("VOLKEN_CLOUD_EMPTY_SPACE");
        }

        private static bool Finite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        public void Dispose()
        {
            if (_commands != null) { _commands.Release(); _commands = null; }
            if (_atlas == null) return;
            _atlas.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(_atlas);
            else UnityEngine.Object.DestroyImmediate(_atlas);
            _atlas = null;
        }
    }
}
