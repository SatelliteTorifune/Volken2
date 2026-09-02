using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Assets.Scripts.Flight.GameView.Planet;

public static class PlanetRingsShaderPatch
{
    private static FieldInfo _frontMaterialField;
    private static FieldInfo _backMaterialField;
    private static MethodInfo _initializeMaterialMethod;

    private static Shader _fixedShader;

    public static void Apply(Harmony harmony)
    {
        var type = typeof(PlanetRingsScript);

        // 获取私有字段和方法
        _frontMaterialField = AccessTools.Field(type, "_frontMaterial");
        _backMaterialField = AccessTools.Field(type, "_backMaterial");
        _initializeMaterialMethod = AccessTools.Method(type, "InitializeMaterial");

        if (_frontMaterialField == null || _backMaterialField == null || _initializeMaterialMethod == null)
        {
            Debug.LogError("[YourMod] Failed to find PlanetRingsScript fields/methods via reflection!");
            return;
        }

        // 预先查找shader，避免运行时Find失败
        _fixedShader = Shader.Find("Jundroo/PlanetRingShader_Fixed");
        if (_fixedShader == null)
        {
            Debug.LogError("[YourMod] Cannot find shader 'Jundroo/PlanetRingShader_Fixed'! Check name and asset placement.");
            return;
        }

        // Patch Start 方法（环材质在这里被赋值）
        var startMethod = AccessTools.Method(type, "Start");
        if (startMethod != null)
        {
            harmony.Patch(startMethod, postfix: new HarmonyMethod(typeof(PlanetRingsShaderPatch), nameof(Postfix)));
        }
        else
        {
            Debug.LogError("[YourMod] PlanetRingsScript.Start method not found!");
        }
    }

    static void Postfix(PlanetRingsScript __instance)
    {
        // 获取 front 和 back 材质
        Material frontMat = (Material)_frontMaterialField.GetValue(__instance);
        Material backMat = (Material)_backMaterialField.GetValue(__instance);

        bool changed = false;

        if (frontMat != null && frontMat.shader != _fixedShader)
        {
            frontMat.shader = _fixedShader;
            // 关键：重新调用原版 InitializeMaterial 来恢复纹理和参数
            _initializeMaterialMethod.Invoke(__instance, new object[] { frontMat });
            changed = true;
            Debug.Log("[YourMod] Front ring material shader replaced and reinitialized.");
        }

        if (backMat != null && backMat.shader != _fixedShader)
        {
            backMat.shader = _fixedShader;
            _initializeMaterialMethod.Invoke(__instance, new object[] { backMat });
            changed = true;
            Debug.Log("[YourMod] Back ring material shader replaced and reinitialized.");
        }

        if (changed)
        {
            // 可选：强制刷新renderQueue（保持原版动态调整逻辑）
            // __instance.UpdateRingsBasedOnCameraPosition(); // 如果需要可以patch LateUpdate调用
        }
    }
}