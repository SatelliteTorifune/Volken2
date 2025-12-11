using UnityEngine;

public class CloudConfig
{
    public bool enabled;
    public float density;
    public float absorption;
    public float ambientLight;
    public float coverage;
    public float shapeScale;
    public float detailScale;
    public float detailStrength;
    public Vector4 phaseParameters;
    public Vector3 offset;
    public float windSpeed;
    public float windDirection;
    public float scatterStrength;
    public float atmoBlendFactor;
    public Color cloudColor;
    public Vector2 layerHeights;
    public Vector2 layerSpreads;
    public Vector2 layerStrengths;
    public float maxCloudHeight;
    public float resolutionScale;
    public float stepSize;
    public float stepSizeFalloff;
    public int numLightSamplePoints;
    public float blueNoiseStrength;
    public float depthThreshold;
    public float historyBlend;
    public float historyDepthThreshold = 0.05f;
}