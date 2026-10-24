using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Clouds
{
    // 诊断专用快照；每阶段读取自己的固定输入，省略上游不改变下游工作量。
    internal sealed class CloudPerformanceFreeze : IDisposable
    {
        internal enum Omit { None, Trace, Upscale, Composite, NoSolar }
        private sealed class View
        {
            internal Material Trace, Upscale, Composite;
            internal RenderTexture Cloud, Scene, Output;
            internal int Width, Height;
        }
        private readonly Dictionary<CloudLayerView, View> _views = new Dictionary<CloudLayerView, View>();
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        internal bool Capturing { get; private set; }
        internal bool Ready { get; private set; }
        internal Omit Omitted;

        internal void Arm() { Dispose(); Capturing = true; }

        internal void BeginFrame(List<CloudLayerView> views, bool fog)
        {
            if (!Capturing && !Ready) return;
            if (fog || views.Count == 0) throw new InvalidOperationException("Frozen benchmark requires active volume clouds and no fog.");
            foreach (var view in views)
            {
                if (view.orbitFade > 0f || view.currentLightningEnabled)
                    throw new InvalidOperationException("Frozen benchmark requires volume-only clouds and cloud lightning disabled.");
                if (Capturing)
                    _views.Add(view, new View { Width = view.currentW, Height = view.currentH });
                else if (!_views.TryGetValue(view, out var snapshot) || snapshot.Width != view.currentW || snapshot.Height != view.currentH)
                    throw new InvalidOperationException("Frozen benchmark view changed.");
            }
            if (views.Count != _views.Count) throw new InvalidOperationException("Frozen benchmark layer count changed.");
        }

        internal Material Trace(CloudLayerView view, Material material, bool? lightReuse = null)
        {
            material.DisableKeyword("VOLKEN_CLOUD_BENCH_NO_SOLAR");
            if (Capturing)
            {
                var snapshot = _views[view];
                snapshot.Trace = Clone(material);
                Bind(snapshot.Trace, "DepthTex");
            }
            if (!Ready) return material;
            var trace = _views[view].Trace;
            if (Omitted == Omit.NoSolar) trace.EnableKeyword("VOLKEN_CLOUD_BENCH_NO_SOLAR");
            else trace.DisableKeyword("VOLKEN_CLOUD_BENCH_NO_SOLAR");
            if (lightReuse.HasValue) CloudLightReuse.Apply(trace, lightReuse.Value);
            return trace;
        }

        internal bool Upscale(CloudLayerView view, Material material, int pass)
        {
            if (Capturing)
            {
                var snapshot = _views[view];
                snapshot.Upscale = Clone(material);
                snapshot.Cloud = Copy(view.cloudTex);
                foreach (string property in new[] { "CloudDepthTex", "CloudMVTex", "CombinedDepthTex", "HistoryTex", "HistoryDepthTex", "HistoryCloudDepthTex" })
                    Bind(snapshot.Upscale, property);
            }
            if (!Ready) return false;
            if (Omitted != Omit.Upscale)
                Graphics.Blit(_views[view].Cloud, view.upscaledCloudTex, _views[view].Upscale, pass);
            return true;
        }

        internal Texture CloudDepth(CloudLayerView view) => Ready
            ? _views[view].Upscale.GetTexture("CloudDepthTex") : view.cloudDepthTex;

        internal Texture SceneDepth(Texture fallback)
        {
            if (Ready) foreach (var snapshot in _views.Values) return snapshot.Upscale.GetTexture("CombinedDepthTex");
            return fallback;
        }

        internal bool Composite(CloudLayerView view, Texture source, RenderTexture destination, Material material, int pass)
        {
            if (Capturing)
            {
                var snapshot = _views[view];
                snapshot.Composite = Clone(material);
                foreach (string property in new[] { "UpscaledCloudTex", "CloudLightningTex", "OrbitCloudTex", "SceneDepthTex", "FogCloudDepthTex" })
                    Bind(snapshot.Composite, property);
                snapshot.Scene = Copy((RenderTexture)source);
                snapshot.Output = Create(snapshot.Scene);
                Graphics.Blit(source, snapshot.Output, material, pass);
                Graphics.Blit(snapshot.Output, destination);
                return true;
            }
            if (!Ready) return false;
            var frozen = _views[view];
            if (Omitted == Omit.Composite) Graphics.Blit(frozen.Output, destination);
            else Graphics.Blit(frozen.Scene, destination, frozen.Composite, pass);
            return true;
        }

        internal void CompleteFrame()
        {
            if (!Capturing) return;
            foreach (var snapshot in _views.Values)
                if (snapshot.Trace == null || snapshot.Upscale == null || snapshot.Composite == null || snapshot.Output == null)
                    throw new InvalidOperationException("Incomplete frozen benchmark capture.");
            Capturing = false; Ready = true;
        }

        private Material Clone(Material source)
        {
            var material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            material.CopyPropertiesFromMaterial(source);
            _owned.Add(material); return material;
        }

        private void Bind(Material material, string property)
        {
            if (material.GetTexture(property) is RenderTexture texture) material.SetTexture(property, Copy(texture));
        }

        private RenderTexture Create(RenderTexture source)
        {
            var descriptor = source.descriptor;
            descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1;
            descriptor.enableRandomWrite = false; descriptor.useMipMap = false; descriptor.autoGenerateMips = false;
            var texture = new RenderTexture(descriptor) { name = "CloudBenchFrozen", filterMode = source.filterMode,
                wrapMode = source.wrapMode, hideFlags = HideFlags.HideAndDontSave };
            _owned.Add(texture);
            if (!texture.Create()) throw new InvalidOperationException("Cannot allocate frozen benchmark texture.");
            return texture;
        }

        private RenderTexture Copy(RenderTexture source)
        {
            if (!source.IsCreated() || source.dimension != TextureDimension.Tex2D)
                throw new InvalidOperationException("Invalid frozen benchmark texture.");
            var target = Create(source);
            if (source.antiAliasing == 1 && (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0)
                Graphics.CopyTexture(source, 0, 0, target, 0, 0);
            else Graphics.Blit(source, target);
            return target;
        }

        public void Dispose()
        {
            Capturing = Ready = false;
            foreach (var obj in _owned)
            {
                if (obj is RenderTexture texture) texture.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
                else UnityEngine.Object.DestroyImmediate(obj);
            }
            _owned.Clear(); _views.Clear();
        }
    }
}
