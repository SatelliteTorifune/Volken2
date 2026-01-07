using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Assets.Scripts.Flight.GameView.Planet;
using System.Reflection;
using Assets.Scripts;

public static class PlanetRingsZWriteFix
{
    private static FieldInfo _frontMaterialField;
    private static FieldInfo _backMaterialField;
    private static FieldInfo _renderQueueAfterField;
    private static FieldInfo _renderQueueBeforeField;

   

    public static void Apply(Harmony harmony)
    {
        var type = typeof(PlanetRingsScript);

        _frontMaterialField = AccessTools.Field(type, "_frontMaterial");
        _backMaterialField = AccessTools.Field(type, "_backMaterial");
        _renderQueueAfterField = AccessTools.Field(type, "_renderQueueAfterAtmosphere");
        _renderQueueBeforeField = AccessTools.Field(type, "_renderQueueBeforeAtmosphere");

        var methods = new[] { "Start", "LateUpdate", "UpdateRingsBasedOnCameraPosition" };
        foreach (var methodName in methods)
        {
            var method = AccessTools.Method(type, methodName);
            if (method != null)
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(PlanetRingsZWriteFix), nameof(Postfix)));
            }
        }

        Mod.LOG("[Volken] Planet rings ZWrite + Queue fix installed (Mod.Instance.backRenderQueue = " + Mod.Instance.backRenderQueue + ")");
    }

    public static void Postfix(PlanetRingsScript __instance)
    {
        try
        {
            // 强制改原脚本的 queue 常量，防止它再改
            _renderQueueAfterField?.SetValue(__instance, Mod.Instance.backRenderQueue);
            _renderQueueBeforeField?.SetValue(__instance, Mod.Instance.backRenderQueue - 1);

            Material frontMat = (Material)_frontMaterialField?.GetValue(__instance);
            Material backMat = (Material)_backMaterialField?.GetValue(__instance);

            if (frontMat != null) FixMaterial(frontMat);
            if (backMat != null) FixMaterial(backMat);

            Mod.LOG("[Volken] Rings fixed: Queue = " + Mod.Instance.backRenderQueue + ", ZWrite ON");
        }
        catch (System.Exception ex)
        {
            Mod.LOG("[Volken Error] Rings fix failed: " + ex.Message);
        }
    }

    private static void FixMaterial(Material mat)
    {
        // 强制写深度
        mat.SetInt("_ZWrite", 1);
        mat.SetOverrideTag("ZWrite", "On");

        // 强制队列
        mat.renderQueue = Mod.Instance.backRenderQueue;
        
        //mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        //mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        
         mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
         mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
         
        mat.SetOverrideTag("RenderType", "Opaque");
    }
}