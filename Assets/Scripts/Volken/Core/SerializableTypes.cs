using UnityEngine;
using System;

[Serializable]
public struct SerializableVector2
{
    public float x;
    public float y;

    public SerializableVector2(Vector2 v)
    {
        x = v.x;
        y = v.y;
    }

    public Vector2 ToVector2()
    {
        return new Vector2(x, y);
    }
}

[Serializable]
public struct SerializableVector3
{
    public float x;
    public float y;
    public float z;

    public SerializableVector3(Vector3 v)
    {
        x = v.x;
        y = v.y;
        z = v.z;
    }

    public Vector3 ToVector3()
    {
        return new Vector3(x, y, z);
    }
}

[Serializable]
public struct SerializableVector4
{
    public float x;
    public float y;
    public float z;
    public float w;

    public SerializableVector4(Vector4 v)
    {
        x = v.x;
        y = v.y;
        z = v.z;
        w = v.w;
    }

    public Vector4 ToVector4()
    {
        return new Vector4(x, y, z, w);
    }
}

[Serializable]
public struct SerializableColor
{
    public float r;
    public float g;
    public float b;
    public float a;

    public SerializableColor(Color c)
    {
        r = c.r;
        g = c.g;
        b = c.b;
        a = c.a;
    }

    public Color ToColor()
    {
        return new Color(r, g, b, a);
    }
}