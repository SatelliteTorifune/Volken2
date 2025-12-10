using UnityEngine;
using UnityEngine.Rendering;   // ShaderPropertyType 定義在這裡

/// <summary>
/// 把 Material 裡的所有屬性（float、int、vector/colour、texture）
/// 一次性拷貝到指定 ComputeShader kernel。只使用 Runtime API，  
/// 因此可以在任何平台（包括 Build）上使用。
/// </summary>
public static class MaterialExtensions
{
    /// <summary>
    /// 把 material 的每一個屬性根據類型寫入 computeShader 的同名屬性。
    /// 注意：僅會拷貝 shader 本身宣告過的屬性（不包括動態添加的隱式屬性）。
    /// </summary>
    /// <param name="mat">要拷貝的 Material</param>
    /// <param name="cs">目標 ComputeShader</param>
    /// <param name="kernel">要寫入的 kernel 索引（找不到會拋例外）</param>
    public static void CopyPropertiesTo(this Material mat, ComputeShader cs, int kernel)
    {
        if (mat == null) throw new System.ArgumentNullException(nameof(mat));
        if (cs    == null) throw new System.ArgumentNullException(nameof(cs));

        Shader shader = mat.shader;
        int propertyCount = shader.GetPropertyCount();

        for (int i = 0; i < propertyCount; i++)
        {
            string propName = shader.GetPropertyName(i);
            ShaderPropertyType propType = shader.GetPropertyType(i);

            // 根據類型呼叫對應的 ComputeShader 設定函式
            switch (propType)
            {
                // -------- float / range / slider --------
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    cs.SetFloat(kernel, mat.GetFloat(propName));
                    break;

                // -------- int --------
                case ShaderPropertyType.Int:
                    cs.SetInt(kernel, mat.GetInt(propName));
                    break;

                // -------- vector / colour --------
                case ShaderPropertyType.Vector:
                case ShaderPropertyType.Color:
                    // Material.GetVector 會返回 UnityEngine.Vector4
                    cs.SetVector(kernel, mat.GetVector(propName));
                    break;

                // -------- texture --------
                case ShaderPropertyType.Texture:
                    cs.SetTexture(kernel, propName, mat.GetTexture(propName));
                    break;

                // 若未來 Unity 增加新類型，這裡會在編譯期給出警告，需手動補上
                default:
                    Debug.LogWarning($"[MaterialExtensions] 未處理的 shader 屬性類型 {propType} (name={propName})");
                    break;
            }
        }
    }
}
