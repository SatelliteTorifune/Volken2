Shader "Hidden/Clouds"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _NearThreshold("Near Threshold", Float) = 2000.0

        // 关键修复:所有通过 material.SetTexture 在运行时绑定的纹理都必须在此声明。
        // 只在 CGPROGRAM 里声明(Texture2D/Texture3D/TextureCube)不会被注册为材质纹理属性,
        // SetTexture 会静默失败 → shader 采样到空 → 全 0 → 无云。
        // 之前只有 StockCloudCube 声明在此(所以它绑定成功),其余全静默失败。
        CloudShapeTex("CloudShapeTex", 3D) = "" {}
        CloudDetailTex("CloudDetailTex", 3D) = "" {}
        PlanetMapTex("PlanetMapTex", 2D) = "" {}
        BlueNoiseTex("BlueNoiseTex", 2D) = "" {}
        DepthTex("DepthTex", 2D) = "" {}
        HistoryTex("HistoryTex", 2D) = "" {}
        HistoryDepthTex("HistoryDepthTex", 2D) = "" {}
        CombinedDepthTex("CombinedDepthTex", 2D) = "" {}
        LowResDepthTex("LowResDepthTex", 2D) = "" {}
        CloudDepthTex("CloudDepthTex", 2D) = "" {}
        HistoryCloudDepthTex("HistoryCloudDepthTex", 2D) = "" {}
        UpscaledCloudTex("UpscaledCloudTex", 2D) = "" {}
        SceneDepthTex("SceneDepthTex", 2D) = "" {}
        StockCloudCube("Stock Cloud Cube", Cube) = "" {}
    }
        SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "FarDepth"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _CameraDepthTexture;

            float2 clipPlanes;

            float4 frag(v2f i) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);

                // vvv depth is stored nonlinearly, this function converts it to a useful value
                return rawDepth > 0.0 ? LinearEyeDepth(rawDepth) : clipPlanes.y;
            }
            ENDCG
        }

        Pass
        {
            Name "NearDepth"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;

            float4 frag(v2f i) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float farDepth = tex2D(_MainTex, i.uv);

                return rawDepth > 0.0 ? LinearEyeDepth(rawDepth) : farDepth;
            }
            ENDCG
        }

        Pass
        {
            Name "DownsampleDepth"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex;

            float4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }

        Pass
        {
            Name "Clouds"

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct vert2Frag
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
            };

            float3 _CamFwd;
            float3 _CamRight;
            float3 _CamUp;
            float _TanHalfFovV;
            float _Aspect;

            vert2Frag vert(appdata v)
            {
                vert2Frag o;
                // 阶段二:使用 DrawMeshNow + 全屏三角形(clip-space 顶点),
                // 以实现 MRT(SV_Target0=颜色, SV_Target1=云面距离)。
                o.vertex = v.vertex;  // 直接传递 clip-space 位置
                // 观察射线:直接用 clip 坐标(NDC) + 相机 transform 轴 + fov/aspect 构造。
                // 用 cam.transform.forward/right/up(C# 传入,无歧义),不要从 cameraToWorldMatrix
                // 第2列取 fwd(那是 -forward,会反向导致云随相机旋转/缩放漂移)。
                float2 ndc = v.vertex.xy;   // NDC:-1..1,y=+1 为屏幕顶部
                // 注意:up 项用【减】号。实测(模式2/云图)显示加号会把云垂直反置:
                // 太空看行星时云跑到上空(倒扣穹顶)、贴地场景云掉地底。减号修正垂直方向。
                o.viewDir = _CamFwd + _CamRight * (ndc.x * _TanHalfFovV * _Aspect) - _CamUp * (ndc.y * _TanHalfFovV);
                // uv:必须与后续 Upscale/Composite(走 Graphics.Blit)的约定一致,
                // 否则 cloudTex 会被它们上下翻转显示(云全被压到地平线下)。
                // Blit 在 D3D(_ProjectionParams.x<0)上 uv.y=0 在顶部 → 此处翻转 Y。
                float2 uv = float2(ndc.x * 0.5 + 0.5, ndc.y * 0.5 + 0.5);
                uv.y = _ProjectionParams.x < 0.0 ? 1.0 - uv.y : uv.y;
                o.uv = uv;
                return o;
            }

            //Textures
            sampler2D _CameraDepthTexture;

            Texture2D<float> DepthTex;
            SamplerState samplerDepthTex;

            Texture3D<float> CloudShapeTex;
            SamplerState samplerCloudShapeTex;

            Texture3D<float> CloudDetailTex;
            SamplerState samplerCloudDetailTex;

            Texture2D<float2> PlanetMapTex;
            SamplerState samplerPlanetMapTex;

            // Game stock cloud cubemap as global distribution shape (plan B)
            TextureCube<float4> StockCloudCube;
            SamplerState samplerStockCloudCube;
            float useStockCloudMap;        // 0/1 master switch (forced 0 when no cubemap loaded)
            float stockMapStrength;        // 0..1 blend strength
            float stockMaskInfluence;      // 0..1 latitude/planet mask (A channel) influence
            float stockMapLayer;           // 0=low(R), 1=mid(G), 2=high(B), 3=per-band mapping
            float4 stockLayerValid;        // load-time check: (R=low, G=mid, B=high, A=mask) layer presence; 0 = fall back that band to planetMap
            float stockAlignSign;          // +-1 rotation direction
            float stockAlignAngleOffset;   // degrees, one-time alignment
            float4x4 planetToBody;         // reference-frame -> planet-body rotation

            Texture2D<float4> BlueNoiseTex;
            SamplerState samplerBlueNoiseTex;

            Texture2D<float4> HistoryTex;
            SamplerState samplerHistoryTex;
            Texture2D<float> HistoryDepthTex;
            SamplerState samplerHistoryDepthTex;

            Texture2D<float> HistoryCloudDepthTex;
            SamplerState samplerHistoryCloudDepthTex;

            Texture2D<float> CloudDepthTex;
            SamplerState samplerCloudDepthTex;

            //Cloud Shape
            float cloudDensity;
            float cloudAbsorption;
            float ambientLight;
            float cloudCoverage;
            float cloudScale;
            float detailScale;
            float detailStrength;
            float3 cloudOffset;
            float4 cloudColor;
            float scatterStrength;
            float historyDepthThreshold;

            //scatter
            float scatterPower;
            float multiScatterBlend;
            float ambientScatterStrength;
            float3 customWavelengths;
            float silverLiningIntensity;
            float forwardScatteringBias;
            //Cloud Layers (x=Layer1, y=Layer2, z=Layer3, w=Layer4)
            float4 cloudLayerHeights;
            float4 cloudLayerSpreads;
            float4 cloudLayerStrengths;

            //Container
            float surfaceRadius;
            float maxCloudHeight;
            float3 sphereCenter;

            // Quality
            float stepSize;
            float stepSizeFalloff;
            int numLightSamplePoints;

            //Misc
            float3 lightDir;
            float4 phaseParams;
            float2 blueNoiseScale;
            float2 blueNoiseOffset;
            float blueNoiseStrength;
            float atmoBlendFactor;
            float maxDepth;
            float historyBlend;
            matrix reprojMat;
            float currentRotation;
            // === 方案 C: 时序超采样 ===
            float _UseTemporal;      // 0 = 现状路径(每格都步进);1 = 时序子集步进
            float2 _SampleCell;      // 本帧要步进的格 (cellX, cellY)
            float2 _Upscale;         // 格网尺寸 (upscaleX, upscaleY)
            float2 _LowResSize;      // 低清步进 RT 的像素尺寸(uv -> 格坐标换算用)
                    
            // magic functions for better lighting
            float HenyeyGreenstein(float a, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * 3.1415926 * pow(abs(1.0 + g2 - 2.0 * g * a), 1.5));
            }
            float HenyeyGreensteinMultiple(float cosAngle, float g1, float g2, float blend)
            {
                float hg1 = HenyeyGreenstein(cosAngle, g1);
                float hg2 = HenyeyGreenstein(cosAngle, g2);
                float hg3 = HenyeyGreenstein(cosAngle, 0.0);
                
                float firstBlend = lerp(hg1, hg2, blend);
                return lerp(firstBlend, hg3, multiScatterBlend * 0.5);
            }
            float CloudPhase(float a, float blend)
            {
                float hgBlend = HenyeyGreensteinMultiple(a, forwardScatteringBias, -phaseParams.y, blend);
                return phaseParams.z + hgBlend * phaseParams.w * silverLiningIntensity;
            }

            float Phase(float a) {
                float blend = .5;
                float hgBlend = HenyeyGreenstein(a, phaseParams.x) * (1 - blend) + HenyeyGreenstein(a, -phaseParams.y) * blend;
                return phaseParams.z + hgBlend * phaseParams.w;
            }

            

            // basic transmittance function
            float Beer(float d, float amb) {
                return amb + exp(-d * cloudAbsorption) * (1.0 - amb);
            }
            
            // more advanced transmittance function for lighting stuff
            float BeersPowder(float d, float amb)
            {
                return amb + 2.0 * exp(-d * cloudAbsorption) * (1.0 - exp(-2.0 * d * cloudAbsorption)) * (1.0 - amb);
            }

            // returns the distances of the intersections from the given point
            float2 RaySphereIntersect(float3 pos, float3 dir, float radius) {
                float3 offset = pos - sphereCenter;

                float a = dot(dir, dir);
                float b = 2 * dot(offset, dir);
                float c = dot(offset, offset) - radius * radius;
                float d = b * b - 4 * a * c;

                // no intersection
                if (d < 0.0) {
                    return -1.0;
                }

                float sqrtD = sqrt(d);
                return float2((-b - sqrtD) / (2 * a), (-b + sqrtD) / (2 * a));
            }

            // 方案 B: sample the game stock Clouds cubemap as the global distribution shape.
            // dir is in the reference frame (already rotated by currentRotation);
            // we apply E/W wind as a Y rotation (approximation of the planetMap UV shift),
            // then rotate into the planet-body space where the cubemap was baked.
            float4 SampleStockDistribution(float3 dir, float windAngle)
            {
                float yAngle = windAngle + stockAlignSign * (stockAlignAngleOffset * 0.0174532925);
                float ca = cos(yAngle);
                float sa = sin(yAngle);
                float3 sd = float3(dir.x * ca - dir.z * sa, dir.y, dir.x * sa + dir.z * ca);
                sd = mul(planetToBody, float4(sd, 0.0)).xyz;
                return StockCloudCube.SampleLevel(samplerStockCloudCube, sd, 0);
            }

            //those 2 functions made me wanna kill myself tbh
            float SampleDensity(float3 worldPos, float detailFalloff) 
            {
                float3 offset = worldPos - sphereCenter;
                float r = length(offset);
            
                float cosAngle = cos(currentRotation);
                float sinAngle = sin(currentRotation);
                float3 rotatedOffset = float3(
                    offset.x * cosAngle - offset.z * sinAngle,
                    offset.y,
                    offset.x * sinAngle + offset.z * cosAngle
                );
            
                // Domain warping: small sinusoidal perturbation to break up visible tiling
                float3 warpOffset = float3(
                    sin(rotatedOffset.y * 0.05 + rotatedOffset.z * 0.03),
                    cos(rotatedOffset.x * 0.04 - rotatedOffset.z * 0.05),
                    sin(rotatedOffset.x * 0.03 + rotatedOffset.y * 0.04)
                ) * 0.15;
                float3 warpedUv = rotatedOffset + warpOffset;
            
                // Keep shape relatively cheap; target the smallest-scale repetition in detail.
                float shape = CloudShapeTex.SampleLevel(samplerCloudShapeTex, warpedUv * cloudScale, 0);
                float detail = CloudDetailTex.SampleLevel(samplerCloudDetailTex, warpedUv * detailScale, 0);
                shape -= (1.0 - shape) * (1.0 - shape) * detailStrength * detailFalloff * detail;
            
                float3 dir = normalize(rotatedOffset);
                float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);
            
                //yes the wind and the angular are somehow conflic...but lmao i dgaf
                //no joking,but that's the best effect for now if you don't want to see the pile of shit in northern pole
                // Strong east/west wind (full strength)
                spherical.x += cloudOffset.x;
            
                //TODO the velocity of the cloud should be with direction
                // Safe north/south wind: attenuated by latitude (zero at poles, max at equator)
                float latFactor = sin(spherical.y * 3.14159265); 
                spherical.y += cloudOffset.z * 0.25 * latFactor; 
            
                float2 planetMap = PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);

                // 方案 B: game stock Clouds cubemap as the global distribution shape.
                // stockEff == 0 keeps the exact previous behavior (pure fallback).
                // Uniform branch: when the feature is off (or no cubemap is bound) we skip
                // the cubemap fetch entirely so there is zero added cost vs the original.
                float4 stock = 0.0;
                if (useStockCloudMap > 0.5)
                {
                    stock = SampleStockDistribution(dir, cloudOffset.x * 6.28318530718);
                }
                float stockEff = useStockCloudMap * stockMapStrength;
                // 兜底(方案 B):load 时检测该星球游戏各云层是否真实存在(R/G/B=低/中/高,A=遮罩)。
                // 某层不存在(valid=0)→ 该层回退到老 Volken 的 planetMap;遮罩不存在 → mask 置中性。
                float selValid = lerp(lerp(stockLayerValid.x, stockLayerValid.y, step(0.5, stockMapLayer)), stockLayerValid.z, step(1.5, stockMapLayer));
                float4 valid = lerp(selValid.xxxx, float4(stockLayerValid.x, stockLayerValid.y, stockLayerValid.z, stockLayerValid.x), step(2.5, stockMapLayer));
                float stockMaskValid = stockLayerValid.w;
                float stockMask = lerp(1.0, stock.a, stockMaskInfluence * stockEff * stockMaskValid);
                // 方案 B layer source: 0=low(R), 1=mid(G), 2=high(B), 3=per-band mapping
                float selChannel = lerp(lerp(stock.r, stock.g, step(0.5, stockMapLayer)), stock.b, step(1.5, stockMapLayer));
                float4 stockBandRaw = lerp(selChannel.xxxx, float4(stock.r, stock.g, stock.b, stock.r), step(2.5, stockMapLayer));
                float4 stockBand = stockBandRaw * stockMask;

                // Layer1/3/4 use density (planetMap.r), Layer2 uses height (planetMap.g)
                // Stock replaces the data source: R=low, G=mid, B=high, A=mask.
                // valid==0 的层保持 planetMap(老 Volken 行为)
                float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockBand, stockEff * valid);
                float4 layers;
                layers.x = cloudLayerStrengths.x * mapVal.x;
                layers.y = cloudLayerStrengths.y * mapVal.y;
                layers.z = cloudLayerStrengths.z * mapVal.z;
                layers.w = cloudLayerStrengths.w * mapVal.w;
            
                float4 falloffExponent = ((r - surfaceRadius) - cloudLayerHeights) / cloudLayerSpreads;
                float4 falloff = exp(-falloffExponent * falloffExponent);
                
                // Gate: only active layers (strength > 0) contribute shape * falloff
                // This preserves EXACT original behavior for Layer1&2 when Layer3/4 are disabled
                float4 active = step(0.0001, cloudLayerStrengths);
                // 方案 B: gate the 3D shape by the stock distribution per band (valid==0 → dist=1, 不回退门)
                float4 dist = lerp(float4(1.0, 1.0, 1.0, 1.0), stockBand, stockEff * valid);
                float totalDensity = shape * (dist.x * falloff.x + dist.y * falloff.y + active.z * dist.z * falloff.z + active.w * dist.w * falloff.w)
                                   + layers.x * falloff.x + layers.y * falloff.y
                                   + layers.z * falloff.z + layers.w * falloff.w;
                
                return (totalDensity + cloudCoverage - 1.0) * cloudDensity;
            }

            float SampleDensityCheap(float3 worldPos) 
            {
                float3 offset = worldPos - sphereCenter;
                float r = length(offset);
            
                float cosAngle = cos(currentRotation);
                float sinAngle = sin(currentRotation);
                float3 rotatedOffset = float3(
                    offset.x * cosAngle - offset.z * sinAngle,
                    offset.y,
                    offset.x * sinAngle + offset.z * cosAngle
                );
            
                // Domain warping: small sinusoidal perturbation to break up visible tiling
                float3 warpOffset = float3(
                    sin(rotatedOffset.y * 0.05 + rotatedOffset.z * 0.03),
                    cos(rotatedOffset.x * 0.04 - rotatedOffset.z * 0.05),
                    sin(rotatedOffset.x * 0.03 + rotatedOffset.y * 0.04)
                ) * 0.15;
                float3 warpedUv = rotatedOffset + warpOffset;
            
                  float shape = CloudShapeTex.SampleLevel(samplerCloudShapeTex, warpedUv * cloudScale, 0);
                float detail = CloudDetailTex.SampleLevel(samplerCloudDetailTex, warpedUv * detailScale, 0);
                shape -= (1.0 - shape) * (1.0 - shape) * detailStrength * detail;
            
                float3 dir = normalize(rotatedOffset);
                float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);
            
                //yes the wind and the angular are somehow conflic...but lmao i dgaf
                //no joking,but that's the best effect for now if you don't want to see the pile of shit in northern pole
                // Strong east/west wind (full strength)
                spherical.x += cloudOffset.x;
                
                float latFactor = sin(spherical.y * 3.14159265);  
                spherical.y += cloudOffset.z * 0.25 * latFactor;  
            
                float2 planetMap = PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);

                // 方案 B: game stock Clouds cubemap as the global distribution shape.
                // stockEff == 0 keeps the exact previous behavior (pure fallback).
                // Uniform branch: when the feature is off (or no cubemap is bound) we skip
                // the cubemap fetch entirely so there is zero added cost vs the original.
                float4 stock = 0.0;
                if (useStockCloudMap > 0.5)
                {
                    stock = SampleStockDistribution(dir, cloudOffset.x * 6.28318530718);
                }
                float stockEff = useStockCloudMap * stockMapStrength;
                // 兜底(方案 B):load 时检测该星球游戏各云层是否真实存在(R/G/B=低/中/高,A=遮罩)。
                // 某层不存在(valid=0)→ 该层回退到老 Volken 的 planetMap;遮罩不存在 → mask 置中性。
                float selValid = lerp(lerp(stockLayerValid.x, stockLayerValid.y, step(0.5, stockMapLayer)), stockLayerValid.z, step(1.5, stockMapLayer));
                float4 valid = lerp(selValid.xxxx, float4(stockLayerValid.x, stockLayerValid.y, stockLayerValid.z, stockLayerValid.x), step(2.5, stockMapLayer));
                float stockMaskValid = stockLayerValid.w;
                float stockMask = lerp(1.0, stock.a, stockMaskInfluence * stockEff * stockMaskValid);
                // 方案 B layer source: 0=low(R), 1=mid(G), 2=high(B), 3=per-band mapping
                float selChannel = lerp(lerp(stock.r, stock.g, step(0.5, stockMapLayer)), stock.b, step(1.5, stockMapLayer));
                float4 stockBandRaw = lerp(selChannel.xxxx, float4(stock.r, stock.g, stock.b, stock.r), step(2.5, stockMapLayer));
                float4 stockBand = stockBandRaw * stockMask;

                // Layer1/3/4 use density (planetMap.r), Layer2 uses height (planetMap.g)
                // Stock replaces the data source: R=low, G=mid, B=high, A=mask.
                // valid==0 的层保持 planetMap(老 Volken 行为)
                float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockBand, stockEff * valid);
                float4 layers;
                layers.x = cloudLayerStrengths.x * mapVal.x;
                layers.y = cloudLayerStrengths.y * mapVal.y;
                layers.z = cloudLayerStrengths.z * mapVal.z;
                layers.w = cloudLayerStrengths.w * mapVal.w;
            
                float4 falloffExponent = ((r - surfaceRadius) - cloudLayerHeights) / cloudLayerSpreads;
                float4 falloff = exp(-falloffExponent * falloffExponent);
                
                // 方案 B: gate the 3D shape by the stock distribution per band
                float4 active = step(0.0001, cloudLayerStrengths);
                float4 dist = lerp(float4(1.0, 1.0, 1.0, 1.0), stockBand, stockEff);
                float totalDensity = shape * (dist.x * falloff.x + dist.y * falloff.y + active.z * dist.z * falloff.z + active.w * dist.w * falloff.w)
                                   + layers.x * falloff.x + layers.y * falloff.y
                                   + layers.z * falloff.z + layers.w * falloff.w;
                
                return (totalDensity + cloudCoverage - 1.0) * cloudDensity;
            }

            // approximate the light that reaches the given point
            float2 SampleLightRay(float3 pos) {
                float3 rayPos = pos;
                float3 rayDir = -lightDir;

                float2 surfIntersect = RaySphereIntersect(rayPos, rayDir, surfaceRadius);
                if (surfIntersect.y > 0.0) {
                    return 0.0;
                }

                float2 intersect = RaySphereIntersect(rayPos, rayDir, surfaceRadius + maxCloudHeight);
                float step = stepSize;
                int lightSamples = min(numLightSamplePoints, ceil((intersect.y - max(0.0, intersect.x)) / step));

                float d = 0.0;

                [loop]
                for (int i = 0; i < lightSamples; i++) {
                    rayPos += step * rayDir;
                    d += step * max(0.0, SampleDensityCheap(rayPos));
                }

                return float2(d, intersect.y - max(0.0, intersect.x));
            }

            struct CloudOut
            {
                float4 col : SV_Target0;
                float cloudDepth : SV_Target1;
            };

            CloudOut frag(vert2Frag i)
            {
                CloudOut o;
                float3 camPos = _WorldSpaceCameraPos;
                float viewLength = length(i.viewDir);
                float3 viewDir = i.viewDir / viewLength;

                // === 方案 C: 本像素是否属于"本帧要步进的格子" ===
                // 关闭时(_UseTemporal<=0.5)或冷启动帧(_Upscale=1)恒为真 → 走原路径。
                float2 cellCoord = floor(i.uv * _LowResSize);
                float2 inCell = fmod(cellCoord, _Upscale);
                bool isFresh = (_UseTemporal <= 0.5) || all(inCell == _SampleCell);

                float2 intersect = RaySphereIntersect(camPos, viewDir, surfaceRadius + maxCloudHeight);

                float firstCloudDepth = maxDepth;  // Track first cloud intersection depth
                bool foundCloud = false;
                
                // no intersection in front of the camera
                if (intersect.y < 0.0) {
                    o.col = float4(0.0, 0.0, 0.0, 1.0);
                    o.cloudDepth = 0.0;
                    return o;
                }
                
                

                float2 surfIntersect = RaySphereIntersect(camPos, viewDir, surfaceRadius);
                float depth = viewLength * DepthTex.SampleLevel(samplerDepthTex, i.uv, 0);

                // determine the starting point of the sample ray
                float startRayDist = surfIntersect.x * surfIntersect.y < 0.0 ? surfIntersect.y : max(0.0, intersect.x);
                // end point of sample ray
                float maxRayDist = surfIntersect.y > 0.0 ? surfIntersect.x : intersect.y;
                // cut short by scene depth
                maxRayDist = min(maxRayDist, depth);

                

                if (maxRayDist <= startRayDist) {
                    o.col = float4(0.0, 0.0, 0.0, 1.0);
                    o.cloudDepth = 0.0;
                    return o;
                }

                // === 方案 C 阶段二: 非新鲜格 → 重投影采样历史 ===
                if (!isFresh)
                {
                    float prevCloudDepth = HistoryCloudDepthTex.SampleLevel(samplerHistoryCloudDepthTex, i.uv, 0);
                    if (prevCloudDepth <= 0.0)
                    {
                        o.col = HistoryTex.SampleLevel(samplerHistoryTex, i.uv, 0);
                        o.cloudDepth = 0.0;
                        return o;
                    }
                    float3 cloudPos = camPos + prevCloudDepth * viewDir;
                    float4 reproj = mul(reprojMat, float4(cloudPos, 1.0));
                    float2 reprojUV = 0.5 * (reproj.xy / reproj.w) + 0.5;
                    // reprojUV 来自 GL 风格 clip(uv.y=0 在底部),必须翻转为与 HistoryTex
                    // 相同的约定(uv.y=0 在顶部,D3D),否则时序重投影采错行 → 云随镜头漂移/掉地底。
                    reprojUV.y = _ProjectionParams.x < 0.0 ? 1.0 - reprojUV.y : reprojUV.y;
                    float4 history = HistoryTex.SampleLevel(samplerHistoryTex, reprojUV, 0);
                    float currentDepth = DepthTex.SampleLevel(samplerDepthTex, i.uv, 0);
                    float historySceneDepth = HistoryDepthTex.SampleLevel(samplerHistoryDepthTex, reprojUV, 0);
                    float depthDiff = abs(currentDepth - historySceneDepth) / max(currentDepth, 0.001);
                    bool badSample = (min(reprojUV.x,reprojUV.y) < 0.0) || (max(reprojUV.x,reprojUV.y) > 1.0) || (depthDiff >= historyDepthThreshold);
                    if (badSample)
                    {
                        history = HistoryTex.SampleLevel(samplerHistoryTex, i.uv, 0);
                        o.cloudDepth = prevCloudDepth;
                    }
                    else
                    {
                        o.cloudDepth = HistoryCloudDepthTex.SampleLevel(samplerHistoryCloudDepthTex, reprojUV, 0);
                    }
                    o.col = history;
                    return o;
                }

                float blueNoise = BlueNoiseTex.SampleLevel(samplerBlueNoiseTex, blueNoiseScale * i.uv + blueNoiseOffset, 0).r;
                float rayDist = startRayDist + blueNoiseStrength * stepSize * (blueNoise - 0.5) * 1.5;

                float phaseValue = CloudPhase(dot(viewDir, -lightDir), multiScatterBlend);

                float transmittance = 1.0;
                float3 lightEnergy = 0.0;
                
                float3 rayPos;
                float3 lightTransmittance=0.0;
                float density=0.0;

                // precompute light dependant scattering (ideally this would be parameterised)
                float3 wavelengths = customWavelengths;
                float3 normalizedWavelengths = wavelengths / 550.0;
                float3 scatterCoeff = pow(normalizedWavelengths, -scatterPower) * scatterStrength * 0.1;

                float localStepSize = stepSize;
                float stepSizeMultiplier = 1.0;
                int emptySamples = 0;
                float detailCutoffDist = 25.0 / detailScale;
                float cloudSurfaceDist = maxRayDist;
                
                int iter = 0;
                float3 scatteredLight = density * localStepSize * transmittance * lightTransmittance * phaseValue;
                float3 ambientScatter = scatterCoeff * ambientLight * ambientScatterStrength * density * (1.0 - transmittance) * localStepSize;
                lightEnergy += scatteredLight + ambientScatter;
                
                
                


                [loop]
                while(rayDist < maxRayDist && iter < 350) {
                    rayPos = camPos + rayDist * viewDir;
                    // get full or partial density sample at the current ray position and interpolate at the transition
                    density = (stepSizeMultiplier == 1.0 && rayDist < detailCutoffDist) ? SampleDensity(rayPos, saturate(1e-4 * (detailCutoffDist - rayDist))) : SampleDensityCheap(rayPos);
                    
                    if (density > 0.0) {
                        cloudSurfaceDist = min(cloudSurfaceDist, rayDist);

                        // switch to normal step size when a cloud surface is hit and backtrack the overshot distance
                        if(stepSizeMultiplier == 2.0) {
                            rayDist -= localStepSize * stepSizeMultiplier;
                            stepSizeMultiplier = 1.0;
                            emptySamples = 0;
                            continue;
                        }

                        float amb = ambientLight * clamp(10.0 * dot(normalize(rayPos - sphereCenter), -lightDir), 0.0, 1.0);
                    
                        float2 lightSample = SampleLightRay(rayPos);
                        
                        lightTransmittance = BeersPowder(lightSample.x, amb) * exp(-lightSample.y * scatterCoeff);

                        lightEnergy += density * localStepSize * transmittance * lightTransmittance * phaseValue;
                        transmittance *= Beer(density * localStepSize, amb);
                        
                        // break when visibility reaches threshold to avoid unnecessary samples
                        if (transmittance < 0.01) {
                            break;
                        }
                    }
                    // switch to higher step size after leaving a cloud surface
                    else if (stepSizeMultiplier == 1.0) {
                        emptySamples++;
                        stepSizeMultiplier = (emptySamples > 3) ? 2.0 : 1.0;
                    }

                    // increase step size based on distance from the camera (scuffed implementation)
                    localStepSize = stepSize * clamp(stepSizeFalloff * 1e-5 * rayDist, 1.0, 2.0);
                    // advance sample ray position
                    rayDist += localStepSize * stepSizeMultiplier;
                    iter++;
                }
                
                float shadowTransmittance = 1.0;
                // calculate shadows for solid surfaces
                if (surfIntersect.y > 0.0 || depth < maxDepth) {
                    // offset sample point to avoid precision artifacts
                    shadowTransmittance = 0.5 + 0.5 * Beer(SampleLightRay(camPos + (maxRayDist - 50.0) * viewDir).x, ambientLight);
                }
                transmittance *= shadowTransmittance;

                float atmoBlend = exp(-atmoBlendFactor * (cloudSurfaceDist - startRayDist));
                float4 raymarchOutput = float4(atmoBlend * lightEnergy * cloudColor.rgb, min(1.0, transmittance + 1.0 - atmoBlend));


                float4 reproj = mul(reprojMat, float4(camPos + cloudSurfaceDist * viewDir, 1));
                float2 reprojUV = 0.5 * (reproj.xy / reproj.w) + 0.5;
                reprojUV.y = _ProjectionParams.x < 0.0 ? 1.0 - reprojUV.y : reprojUV.y;
                float4 history = HistoryTex.SampleLevel(samplerHistoryTex, reprojUV.xy, 0);
                
                // === 割裂线修复(2026-08-24):历史接受判据从二值改为软过渡 ===
                // 现象:屏幕中段出现随相机俯仰张开/收拢的两条水平割裂线(TSS关+运动残影开),
                //       TSS开时线附近云闪烁。根因:depthWeight 用场景深度做硬 0/1 判据,
                //       在等深度轮廓/云壳边界处整排翻转 → 一侧混历史、一侧纯新鲜 → 硬接缝。
                float currentDepth = DepthTex.SampleLevel(samplerDepthTex, i.uv, 0);
                float historyDepth = HistoryDepthTex.SampleLevel(samplerHistoryDepthTex, reprojUV.xy, 0);
                float depthDiff = abs(currentDepth - historyDepth) / max(currentDepth, 0.001);
                // 1) 深度权重软过渡:depthDiff 从 0.5×threshold 平滑升到 threshold
                float depthWeight = 1.0 - smoothstep(historyDepthThreshold * 0.5, historyDepthThreshold, depthDiff);

                // 2) 云结束边渐变:cloudSurfaceDist 接近 maxRayDist 时混合淡出(约 2 步长),
                //    避免"有云整段混合/无云完全不混合"的硬接缝
                float edgeFade = saturate((maxRayDist - cloudSurfaceDist) / max(2.0 * stepSize, 1.0));

                // 3) 历史处有云才混(用上一帧云面距离):防止"云移动后把上一帧无云处当历史"的残影
                float prevCloudDepth = HistoryCloudDepthTex.SampleLevel(samplerHistoryCloudDepthTex, reprojUV.xy, 0);
                float cloudGate = prevCloudDepth > 0.0 ? 1.0 : 0.0;

                bool badSample = (min(reprojUV.x,reprojUV.y) < 0.0) || (max(reprojUV.x,reprojUV.y) > 1.0);
                float finalHistoryBlend = badSample ? 0.0 : historyBlend * depthWeight * edgeFade * cloudGate;
                
                o.col = (1.0 - finalHistoryBlend) * raymarchOutput + finalHistoryBlend * history;
                o.cloudDepth = (cloudSurfaceDist < maxRayDist) ? cloudSurfaceDist : 0.0;
                return o;

            }
            ENDCG
        }
        

        Pass
        {
            Name "Upscale"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _CameraDepthTexture;

            Texture2D<float4> _MainTex;
            SamplerState sampler_MainTex;

            Texture2D<float> CombinedDepthTex;
            SamplerState samplerCombinedDepthTex;

            Texture2D<float> LowResDepthTex;
            SamplerState samplerLowResDepthTex;

            bool isNativeRes;
            float depthThreshold;

            // compare lowres upscaled depth to fullres depth and use closest matching neighbour to reduce aliasing
            float4 DepthAwareUpsample(float2 uv)
            {
                float d0 = CombinedDepthTex.Sample(samplerCombinedDepthTex, uv);
                float d1 = LowResDepthTex.Sample(samplerLowResDepthTex, uv);
                float d2 = LowResDepthTex.Sample(samplerLowResDepthTex, uv, int2(0, 1));
                float d3 = LowResDepthTex.Sample(samplerLowResDepthTex, uv, int2(0, -1));
                float d4 = LowResDepthTex.Sample(samplerLowResDepthTex, uv, int2(1, 0));
                float d5 = LowResDepthTex.Sample(samplerLowResDepthTex, uv, int2(-1, 0));

                d1 = abs(d0 - d1);
                d2 = abs(d0 - d2);
                d3 = abs(d0 - d3);
                d4 = abs(d0 - d4);
                d5 = abs(d0 - d5);

                float dmin = min(min(min(min(d1,d2),d3),d4),d5);
                float4 value;

                if (dmin / d0 < depthThreshold)
                    value = _MainTex.Sample(sampler_MainTex, uv);
                else if (dmin == d1)
                    value = _MainTex.Sample(sampler_MainTex, uv);
                else if (dmin == d2)
                    value = _MainTex.Sample(sampler_MainTex, uv, int2(0, 1));
                else if (dmin == d3)
                    value = _MainTex.Sample(sampler_MainTex, uv, int2(0, -1));
                else if (dmin == d4)
                    value = _MainTex.Sample(sampler_MainTex, uv, int2(1, 0));
                else
                    value = _MainTex.Sample(sampler_MainTex, uv, int2(-1, 0));
                
                return value;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (isNativeRes)
                    return _MainTex.Sample(sampler_MainTex, i.uv);

                return DepthAwareUpsample(i.uv);
            }
            ENDCG
        }

        Pass
        {
            Name "Composite"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
        
            #include "UnityCG.cginc"
        
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
        
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
            };
        
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                // Reconstruct view direction
                o.viewDir = mul(unity_CameraInvProjection, float4(v.uv * 2 - 1, 0, -1));
                o.viewDir = mul(unity_CameraToWorld, float4(o.viewDir, 0));
                return o;
            }
        
            Texture2D<float4> UpscaledCloudTex;
            SamplerState samplerUpscaledCloudTex;
            Texture2D<float> SceneDepthTex;
            SamplerState samplerSceneDepthTex;
            sampler2D _MainTex;
            
            float3 sphereCenter;
            float surfaceRadius;
            float _NearThreshold;
            float _CompositeMode;  // 0.0 = Additive (零干扰), 1.0 = Standard (物理遮挡)
            
            float4 frag(v2f i) : SV_Target
            {
                float4 clouds = UpscaledCloudTex.Sample(samplerUpscaledCloudTex, i.uv);
                float4 source = tex2D(_MainTex, i.uv);
                float sceneDepth = SceneDepthTex.Sample(samplerSceneDepthTex, i.uv);

                // === Additive Mode (零视觉干扰) ===
                // 直接将云光加到场景上，不改变场景透过率
                if (_CompositeMode < 0.5)
                {
                    return float4(source.rgb + clouds.rgb, source.a);
                }

                // === Standard Mode (物理遮挡) ===
                float nearThreshold = _NearThreshold;
                if (sceneDepth > 0.0 && sceneDepth < nearThreshold)
                {
                    float nearFactor = smoothstep(0.0, nearThreshold, sceneDepth);
                    nearFactor = lerp(0.2, 1.0, nearFactor);
                    
                    float3 finalCloudColor = clouds.rgb * nearFactor;
                    float finalTransmittance = lerp(0.8, clouds.a, nearFactor);
                    return float4(source.rgb * finalTransmittance + finalCloudColor, source.a);
                }
                else
                {
                    float depthThreshold = 5000.0;
                    float depthMask = saturate(sceneDepth / depthThreshold);
                    float3 maskedCloudColor = clouds.rgb * depthMask;
                    float maskedTransmittance = lerp(1.0, clouds.a, depthMask);
                    
                    return float4(source.rgb * maskedTransmittance + maskedCloudColor, source.a);
                }
            }

            ENDCG
        }
    }
}