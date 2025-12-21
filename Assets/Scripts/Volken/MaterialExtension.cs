using UnityEngine;
using UnityEngine.Rendering;   // ShaderPropertyType 定義在這裡

/// <summary>
/// 把 Material 裡的所有屬性（float、int、vector/colour、texture）
/// 一次性拷貝到指定 ComputeShader kernel。只使用 Runtime API，  
/// 因此可以在任何平台（包括 Build）上使用。
/// </summary>
public static class MaterialExtensions
{
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
                    cs.SetVector(kernel, mat.GetVector(propName));
                    break;

                // -------- texture --------
                case ShaderPropertyType.Texture:
                    cs.SetTexture(kernel, propName, mat.GetTexture(propName));
                    break;
                
                default:
                    break;
            }
        }
    }
}
