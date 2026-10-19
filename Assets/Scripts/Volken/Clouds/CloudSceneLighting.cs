using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Flight.GameView;
using HarmonyLib;
using UnityEngine;

namespace Volken.Clouds
{
    // 照明修改仅在所属相机绘制期间有效;嵌套反射先恢复基线,退出后恢复外层相机。
    internal static class CloudSceneLighting
    {
        private static readonly List<CloudRenderer> Renderers = new List<CloudRenderer>();
        private static readonly Stack<Scope> Scopes = new Stack<Scope>();
        private static readonly Stack<Scope> Pool = new Stack<Scope>();
        private static readonly int Noon = Shader.PropertyToID("_noonColor"), Dusk = Shader.PropertyToID("_duskColor"),
            SunColor = Shader.PropertyToID("_sunLightColor");
        private static bool _installed;

        internal static void Register(CloudRenderer renderer)
        {
            if (!Renderers.Contains(renderer)) Renderers.Add(renderer);
            if (_installed) return;
            _installed = true;
            Camera.onPreCull += Begin;
            Camera.onPreRender += BeforeRender;
            Camera.onPostRender += End;
        }

        internal static void Unregister(CloudRenderer renderer)
        {
            Renderers.Remove(renderer);
            foreach (var scope in Scopes)
                if (scope.Owner == renderer) { ResetScopes(); break; }
            if (Renderers.Count != 0) return;
            ResetScopes();
            Camera.onPreCull -= Begin;
            Camera.onPreRender -= BeforeRender;
            Camera.onPostRender -= End;
            _installed = false;
        }

        internal static CloudRenderer Resolve(Camera camera)
        {
            if (camera == null || !Game.InFlightScene) return null;
            for (int i = 0; i < Renderers.Count; i++)
            {
                var renderer = Renderers[i];
                if (renderer != null && renderer.isActiveAndEnabled &&
                    (renderer.GetComponent<Camera>() == camera || renderer.farDepthSource?.Camera == camera)) return renderer;
            }
            return null;
        }

        internal static Vector3 Factors(Camera camera) => Resolve(camera)?.SunOcclusionFactors ?? Vector3.one;
        internal static Color AmbientBaseline => Scopes.Count > 0 ? Scopes.Peek().Ambient : RenderSettings.ambientLight;
        internal static float SunIntensityBaseline(Light sun) => sun == null ? 0f :
            Scopes.Count > 0 && Scopes.Peek().Sun == sun ? Scopes.Peek().Intensity : sun.intensity;
        internal static Color SunColorBaseline(Light sun) => sun == null ? Color.black :
            Scopes.Count > 0 && Scopes.Peek().Sun == sun ? Scopes.Peek().LightColor : sun.color;

        internal static Color AttenuateAmbient(Color ambient, float factor)
        {
            if (factor >= 1f) return ambient;
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return ScaleRgb(ambient, factor);
            Color result = ScaleRgb(ambient.linear, factor).gamma;
            result.a = ambient.a;
            return result;
        }

        private static void Begin(Camera camera)
        {
            if (Scopes.Count > 0 && Scopes.Peek().Frame != Time.frameCount) ResetScopes();
            var renderer = Resolve(camera);
            if (renderer != null) renderer.PrepareSunOcclusion();
            Vector3 factors = renderer?.SunOcclusionFactors ?? Vector3.one;
            var sun = Game.Instance?.FlightScene?.ViewManager?.GameView?.SunLight;
            var groundShadow = renderer?.PrepareGroundShadows(camera, sun);
            if (Scopes.Count == 0 && factors == Vector3.one && groundShadow == null) return;
            if (Scopes.Count > 0) Scopes.Peek().Restore();
            var scope = Pool.Count > 0 ? Pool.Pop() : new Scope();
            scope.Camera = camera;
            scope.Rendering = false;
            scope.Owner = renderer;
            scope.Frame = Time.frameCount;
            scope.Sun = sun;
            scope.GroundShadow = groundShadow;
            scope.Ambient = RenderSettings.ambientLight;
            scope.Intensity = scope.Sun != null ? scope.Sun.intensity : 0f;
            scope.LightColor = scope.Sun != null ? scope.Sun.color : Color.black;
            scope.Noon = Shader.GetGlobalColor(Noon); scope.Dusk = Shader.GetGlobalColor(Dusk);
            scope.SunColor = Shader.GetGlobalColor(SunColor);
            scope.Factors = factors;
            scope.ShadowDistance = QualitySettings.shadowDistance;
            scope.Cascade2 = QualitySettings.shadowCascade2Split;
            scope.Cascade4 = QualitySettings.shadowCascade4Split;
            Scopes.Push(scope);
            scope.Apply();
        }

        private static void BeforeRender(Camera camera)
        {
            if (Scopes.Count == 0 || Scopes.Peek().Camera != camera) return;
            var scope = Scopes.Peek();
            scope.RestoreCameraClip();
            scope.Rendering = true;
        }

        private static void End(Camera camera)
        {
            if (Scopes.Count == 0 || Scopes.Peek().Camera != camera) return;
            var scope = Scopes.Pop();
            scope.Restore();
            scope.Camera = null; scope.Sun = null; scope.Owner = null; scope.GroundShadow = null;
            Pool.Push(scope);
            if (Scopes.Count > 0) Scopes.Peek().Apply();
        }

        private static void ResetScopes()
        {
            while (Scopes.Count > 0)
            {
                var scope = Scopes.Pop();
                scope.Restore();
                scope.Camera = null; scope.Sun = null; scope.Owner = null; scope.GroundShadow = null;
                Pool.Push(scope);
            }
        }

        private static Color ScaleRgb(Color color, float factor) => new Color(color.r * factor, color.g * factor, color.b * factor, color.a);

        private sealed class Scope
        {
            internal Camera Camera;
            internal CloudRenderer Owner;
            internal int Frame;
            internal Light Sun;
            internal Color Ambient, Noon, Dusk, SunColor, LightColor;
            internal float Intensity;
            internal float ShadowDistance, Cascade2;
            internal Vector3 Cascade4;
            internal Vector3 Factors;
            internal CloudGroundShadows.View GroundShadow;
            internal bool Rendering;
            private bool _clipExpanded, _customProjection, _customCulling;
            private float _farClip;
            private Matrix4x4 _projection, _culling;

            private void PrepareCameraCull()
            {
                if (Rendering || _clipExpanded || GroundShadow == null || Camera == null) return;
                float shadowFar = Mathf.Max(GroundShadow.MaximumDistance, QualitySettings.shadowDistance);
                if (Camera.farClipPlane >= shadowFar) return;
                _farClip = Camera.farClipPlane;
                _projection = Camera.projectionMatrix;
                _culling = Camera.cullingMatrix;
                Camera.ResetProjectionMatrix();
                _customProjection = !_projection.Equals(Camera.projectionMatrix);
                if (_customProjection) Camera.projectionMatrix = _projection;
                Camera.ResetCullingMatrix();
                _customCulling = !_culling.Equals(Camera.cullingMatrix);
                if (_customCulling) Camera.cullingMatrix = _culling;
                // 阴影剔除的 far、投影与剔除矩阵必须一致;正式绘制前恢复原裁剪。
                _clipExpanded = true;
                Camera.farClipPlane = shadowFar;
                Camera.ResetProjectionMatrix();
                if (_customProjection)
                {
                    float scale = (_farClip - Camera.nearClipPlane) / (shadowFar - Camera.nearClipPlane);
                    if (!Camera.orthographic) scale *= shadowFar / _farClip;
                    var shadowProjection = _projection;
                    var row3 = _projection.GetRow(3);
                    shadowProjection.SetRow(2, scale * (_projection.GetRow(2) + row3) - row3);
                    Camera.projectionMatrix = shadowProjection;
                }
                Camera.ResetCullingMatrix();
            }

            internal void RestoreCameraClip()
            {
                if (!_clipExpanded) return;
                _clipExpanded = false;
                if (Camera == null) return;
                Camera.farClipPlane = _farClip;
                if (_customProjection) Camera.projectionMatrix = _projection; else Camera.ResetProjectionMatrix();
                if (_customCulling) Camera.cullingMatrix = _culling; else Camera.ResetCullingMatrix();
            }

            internal void Apply()
            {
                RenderSettings.ambientLight = AttenuateAmbient(Ambient, Factors.z);
                if (Sun != null)
                {
                    // 工作空间与灯光强度模式不同时,先在实际输出空间衰减,再反求灯光输入。
                    bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                    bool compensate = linear != UnityEngine.Rendering.GraphicsSettings.lightsUseLinearIntensity && Intensity > 1e-6f;
                    if (compensate)
                    {
                        Color incident = linear ? ScaleRgb(LightColor, Intensity).linear : ScaleRgb(LightColor.linear, Intensity).gamma;
                        Color attenuated = ScaleRgb(incident, Factors.y);
                        Sun.color = linear ? ScaleRgb(attenuated.gamma, 1f / Intensity) : ScaleRgb(attenuated.linear, 1f / Intensity).gamma;
                        Sun.intensity = Intensity;
                    }
                    else { Sun.color = LightColor; Sun.intensity = Intensity * Factors.y; }
                }
                Shader.SetGlobalColor(CloudSceneLighting.Noon, ScaleRgb(Noon, Factors.y));
                Shader.SetGlobalColor(CloudSceneLighting.Dusk, ScaleRgb(Dusk, Factors.y));
                Shader.SetGlobalColor(CloudSceneLighting.SunColor, ScaleRgb(SunColor, Factors.y));
                if (GroundShadow != null)
                {
                    float range = Mathf.Max(ShadowDistance, GroundShadow.MaximumDistance);
                    float originalRange = Camera != null ? Mathf.Min(ShadowDistance, Camera.farClipPlane) : ShadowDistance;
                    float nearClip = Camera != null ? Camera.nearClipPlane : 0f;
                    float ratio = Mathf.Clamp01((originalRange - nearClip) / Mathf.Max(range - nearClip, 0.001f));
                    QualitySettings.shadowDistance = range;
                    // 前 N-1 级覆盖完整旧有效范围,末级承担新增云影距离。
                    bool retain = range > originalRange && originalRange > nearClip;
                    QualitySettings.shadowCascade2Split = retain ? ratio : Cascade2;
                    QualitySettings.shadowCascade4Split = retain ? new Vector3(Cascade4.x, Cascade4.y, 1f) * ratio : Cascade4;
                }
                PrepareCameraCull();
                GroundShadow?.Apply(Sun);
            }
            internal void Restore()
            {
                RestoreCameraClip();
                GroundShadow?.Restore();
                QualitySettings.shadowDistance = ShadowDistance;
                QualitySettings.shadowCascade2Split = Cascade2;
                QualitySettings.shadowCascade4Split = Cascade4;
                RenderSettings.ambientLight = Ambient;
                if (Sun != null) { Sun.intensity = Intensity; Sun.color = LightColor; }
                Shader.SetGlobalColor(CloudSceneLighting.Noon, Noon);
                Shader.SetGlobalColor(CloudSceneLighting.Dusk, Dusk);
                Shader.SetGlobalColor(CloudSceneLighting.SunColor, SunColor);
            }
        }
    }

    [HarmonyPatch(typeof(ImageEffectsScript), "LateUpdate")]
    internal static class CloudSunFlarePatch
    {
        private static void Postfix(ImageEffectsScript __instance)
        {
            if (!Game.InFlightScene || __instance.Beautify == null) return;
            __instance.Beautify.sunFlaresIntensity *= CloudSceneLighting.Factors(__instance.GetComponent<Camera>()).x;
        }
    }
}
