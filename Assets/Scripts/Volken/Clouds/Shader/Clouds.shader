Shader "Hidden/Clouds"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _NearThreshold("Near Threshold", Float) = 2000.0
        [HideInInspector] _CloudVisibilityRevision("Cloud visibility revision", Float) = 22
        [HideInInspector] _CloudSunOcclusion("Cloud sun occlusion", Float) = 0
        [HideInInspector] _SunlightOcclusionStrength("Sunlight occlusion strength", Float) = 0.8
        [HideInInspector] _AmbientOcclusionStrength("Ambient occlusion strength", Float) = 0.5
        [HideInInspector] _GroundShadowStrength("Ground shadow strength", Float) = 0.8
        [HideInInspector] _GroundShadowDistance("Ground shadow distance", Float) = 20000
        [HideInInspector] _CloudLightningRevision("Cloud lightning revision", Float) = 2
        [HideInInspector] _CloudAmbientColor("Cloud ambient incident light", Vector) = (0,0,0,0)
        [HideInInspector] _CloudSolarColor("Cloud solar incident light", Vector) = (1,1,1,0)

        // 运行时纹理显式登记,供材质复制、绑定与属性检查共用。
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
        CloudMVTex("Cloud motion and previous distance", 2D) = "" {}
        CloudLightningTex("Current cloud lightning", 2D) = "black" {}
        _LightningShadowTex("Lightning optical depth atlas", 2D) = "black" {}
        [HideInInspector] _CloudEmptySpaceEnabled("Conservative empty space", Float) = 0
        HistoryCloudDepthTex("HistoryCloudDepthTex", 2D) = "" {}
        UpscaledCloudTex("UpscaledCloudTex", 2D) = "" {}
        OrbitCloudTex("OrbitCloudTex", 2D) = "" {}
        SceneDepthTex("SceneDepthTex", 2D) = "" {}
        FogCloudDepthTex("Fog cloud depth", 2D) = "" {}
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

            #pragma multi_compile_local __ VOLKEN_CLOUD_EMPTY_SPACE
            #pragma target 4.0 VOLKEN_CLOUD_EMPTY_SPACE
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
            float3 _CamPos;         // 显式相机位置(反射路径不在相机渲染上下文内,不能用 _WorldSpaceCameraPos)
            float _TanHalfFovV;
            float _Aspect;
            float _ReflectionMode; // 1 = 反射路径(跳过 DepthTex 遮挡)
            float _UseTemporal;
            float _CompositeMode; // 0 = Additive, 1 = Standard;与 Composite pass 同一材质属性

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

            #include "CloudVolumeLighting.cginc"
            #if defined(VOLKEN_CLOUD_EMPTY_SPACE)
            #include "CloudEmptySpace.cginc"
            #endif

            struct CloudOut
            {
                float4 col : SV_Target0;
                float cloudDepth : SV_Target1;
                float4 motionVector : SV_Target2;   // RG=UV 位移,B=上一相机云距离×1e-5。
                float4 lightning : SV_Target3;
            };

            CloudOut frag(vert2Frag i)
            {
                CloudOut o;
                o.lightning = 0.0;
                float3 camPos = _CamPos;
                float viewLength = length(i.viewDir);
                float3 viewDir = i.viewDir / viewLength;

                float2 noiseLod = StableNoiseLod();

                float2 intersect = RaySphereIntersect(camPos, viewDir, surfaceRadius + maxCloudHeight);

                bool foundCloud = false;
                
                // no intersection in front of the camera
                if (intersect.y < 0.0) {
                    o.col = float4(0.0, 0.0, 0.0, 1.0);
                    o.cloudDepth = 0.0;
                    o.motionVector = 0.0;
                    return o;
                }
                
                float2 surfIntersect = RaySphereIntersect(camPos, viewDir, surfaceRadius);
                // 反射路径没有对应深纹理:直接取 maxDepth(不遮挡);主路径照旧采样深度。
                float depth = (_ReflectionMode > 0.5) ? maxDepth : (viewLength * DepthTex.SampleLevel(samplerDepthTex, i.uv, 0));

                // determine the starting point of the sample ray
                // end point of sample ray
                float startRayDist;
                float maxRayDist;
                if (_ReflectionMode > 0.5) {
                    // 反射相机位于水面(≈地表)之下的镜像位置,球面求交得到"从地心向外看"的病态解
                    // (near<0<far),再按主路径逻辑 maxRayDist=near<0 → 恒黑。反射路径直接忽略地表球面,
                    // 当作从球面向天穹看:起点取云球近交点,终点取云球远交点。
                    startRayDist = max(0.0, intersect.x);
                    maxRayDist = intersect.y;
                } else {
                    startRayDist = surfIntersect.x * surfIntersect.y < 0.0 ? surfIntersect.y : max(0.0, intersect.x);
                    maxRayDist = surfIntersect.y > 0.0 ? surfIntersect.x : intersect.y;
                }
                // cut short by scene depth
                maxRayDist = min(maxRayDist, depth);

                if (maxRayDist <= startRayDist) {
                    o.col = float4(0.0, 0.0, 0.0, 1.0);
                    o.cloudDepth = 0.0;
                    o.motionVector = 0.0;
                    return o;
                }

                float blueNoise = BlueNoiseTex.SampleLevel(samplerBlueNoiseTex, blueNoiseScale * i.uv + blueNoiseOffset, 0).r;
                float rayDist = max(startRayDist, startRayDist + blueNoiseStrength * stepSize * (blueNoise - 0.5) * 1.5);

                float phaseValue = CloudPhase(dot(viewDir, -lightDir), multiScatterBlend);

                float transmittance = 1.0;
                float3 lightEnergy = 0.0;
                float3 lightningEnergy = 0.0;
                
                float3 rayPos;
                float density=0.0;

                // precompute light dependant scattering (ideally this would be parameterised)
                float3 wavelengths = customWavelengths;
                float3 normalizedWavelengths = wavelengths / 550.0;
                float3 scatterCoeff = pow(normalizedWavelengths, -scatterPower) * scatterStrength * 0.1;
                // 环境入射各向同性;HG 的方向平均为 1/(4π),不随观察方向或太阳高度归零。
                float ambientPhase = max(0.0, phaseParams.z + phaseParams.w * silverLiningIntensity / (4.0 * 3.1415926));
                float3 ambientRadiance = max(0.0, _CloudAmbientColor.rgb) * max(0.0, ambientLight)
                    * (ambientPhase + scatterCoeff * max(0.0, ambientScatterStrength));

                float localStepSize = stepSize;
                float stepSizeMultiplier = 1.0;
                int emptySamples = 0;
                float detailCutoffDist = 25.0 / detailScale;
                float cloudSurfaceDist = maxRayDist;
                
                int iter = 0;
                
                [loop]
                while(rayDist < maxRayDist && iter < 350) {
                    #if defined(VOLKEN_CLOUD_EMPTY_SPACE)
                    [branch] if (CloudTrySkipEmptyInterval(camPos, viewDir, maxRayDist,
                        rayDist, localStepSize, stepSizeMultiplier, emptySamples, iter))
                    {
                        continue;
                    }
                    #endif
                    rayPos = camPos + rayDist * viewDir;
                    // get full or partial density sample at the current ray position and interpolate at the transition
                    density = (stepSizeMultiplier == 1.0 && rayDist < detailCutoffDist)
                        ? SampleDensity(rayPos, saturate(1e-4 * (detailCutoffDist - rayDist)), noiseLod)
                        : SampleDensityCheap(rayPos, noiseLod, true);
                    
                    if (density > 0.0) {
                        // switch to normal step size when a cloud surface is hit and backtrack the overshot distance
                        if(stepSizeMultiplier == 2.0) {
                            rayDist = max(startRayDist, rayDist - localStepSize * stepSizeMultiplier);
                            stepSizeMultiplier = 1.0;
                            emptySamples = 0;
                            continue;
                        }

                        foundCloud = true;
                        cloudSurfaceDist = min(cloudSurfaceDist, rayDist);

                        float amb = ambientLight * clamp(10.0 * dot(normalize(rayPos - sphereCenter), -lightDir), 0.0, 1.0);
                    
                        float2 lightSample = SampleLightRay(rayPos, noiseLod);
                        
                        float3 solarRadiance = BeersPowder(lightSample.x, 0.0) * (1.0 - amb)
                            * exp(-lightSample.y * scatterCoeff) * phaseValue;
                        if (_CloudSunOcclusion > 0.5) solarRadiance *= max(0.0, _CloudSolarColor.rgb);

                        float densityStep = density * localStepSize;
                        float extinctionAmbient = _CloudSunOcclusion > 0.5 ? 0.0 : amb;
                        lightEnergy += densityStep * SegmentScatteringWeight(densityStep, extinctionAmbient)
                            * transmittance * (solarRadiance + ambientRadiance);
                        [branch] if (_LightningSamples >= 2 && _LightningPositionRange.w > 0.0)
                            lightningEnergy += max(0.0, cloudAbsorption) * densityStep * SegmentScatteringWeight(densityStep, 0.0)
                                * transmittance * SampleLightningRadiance(rayPos, noiseLod);
                        transmittance *= Beer(density * localStepSize, extinctionAmbient);
                        
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
                // 相机级阴影光程只写 alpha(透射率);Additive 合成不消费 alpha,故只在 Standard 模式计算。
                // 反射路径没有地表/场景遮蔽,跳过地表阴影(否则反射云会被整体压暗 50%)。
                if (_CloudSunOcclusion < 0.5 && _CompositeMode > 0.5 && (surfIntersect.y > 0.0 || depth < maxDepth) && _ReflectionMode < 0.5) {
                    // offset sample point to avoid precision artifacts
                    shadowTransmittance = 0.5 + 0.5 * Beer(SampleLightRay(camPos + (maxRayDist - 50.0) * viewDir,
                        noiseLod).x, ambientLight);
                }
                transmittance *= shadowTransmittance;

                float atmoBlend = exp(-atmoBlendFactor * (cloudSurfaceDist - startRayDist));
                float4 raymarchOutput = float4(atmoBlend * lightEnergy * cloudColor.rgb, min(1.0, transmittance + 1.0 - atmoBlend));
                if (_CloudSunOcclusion > 0.5) raymarchOutput.a = lerp(1.0, transmittance, atmoBlend);

                // 运动矢量(云空间重投影):云面点 本帧屏幕位置 → 上一帧屏幕位置。
                // KSA 结构:本 pass 纯 raymarch(每帧全量,低清),时序混合/重投影移到 Upscale pass。
                o.col = raymarchOutput;
                o.lightning = float4(atmoBlend * lightningEnergy, 0.0);
                if (_ReflectionMode > 0.5) o.col.rgb = saturate(o.col.rgb) + o.lightning.rgb;
                // 0 = 未命中;1mm 为相机位置命中的编码下限,Upscale 对该距离使用当前帧。
                o.cloudDepth = foundCloud ? max(cloudSurfaceDist, 0.001) : 0.0;
                o.motionVector = 0.0;
                if (foundCloud && cloudSurfaceDist > 0.001 && (_UseTemporal > 0.5 || historyBlend > 0.0))
                {
                    o.motionVector.xy = float2(2.0, 2.0);
                    float4 cloudPoint = float4(camPos + cloudSurfaceDist * viewDir, 1);
                    float4 reproj = mul(reprojMat, cloudPoint);
                    if (reproj.w > 1e-5 && all(abs(reproj) < 1e20))
                    {
                        float2 reprojUV = 0.5 * (reproj.xy / reproj.w) + 0.5;
                        reprojUV.y = _ProjectionParams.x < 0.0 ? 1.0 - reprojUV.y : reprojUV.y;
                        float2 mv = reprojUV - i.uv;
                        // 限制在 Half 可表示范围,同时保留出界方向以拒绝历史。
                        o.motionVector.xy = clamp(mv, -2.0, 2.0);
                        o.motionVector.z = min(length(mul(_HistoryCloudViewMat, cloudPoint).xyz) * 1e-5, 60000.0);
                    }
                }
                return o;

            }
            ENDCG
        }

        Pass
        {
            Name "SunOcclusion"
            Blend DstColor Zero
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "CloudVolumeLighting.cginc"
            float3 _OcclusionOrigin, _OcclusionUp;
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(float4 vertex : POSITION) { v2f o; o.pos = vertex; return o; }
            float4 frag(v2f i) : SV_Target
            {
                int ray = (int)i.pos.x;
                float3 direction = -lightDir;
                if (ray > 0)
                {
                    float3 up = normalize(_OcclusionUp);
                    float3 axis = abs(up.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                    float3 tangent = normalize(cross(axis, up)), bitangent = cross(up, tangent);
                    float mu = sqrt((ray - 0.5) / 8.0), phi = (ray - 1) * 2.39996323;
                    direction = up * mu + sqrt(1.0 - mu * mu) * (tangent * cos(phi) + bitangent * sin(phi));
                }
                float t = CloudSunTransmittance(_OcclusionOrigin, direction);
                return float4(t, lerp(1.0, t, _SunlightOcclusionStrength), lerp(1.0, t, _AmbientOcclusionStrength), 1);
            }
            ENDCG
        }

        Pass
        {
            Name "GroundShadowQuery"
            Blend DstColor Zero, One Zero
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "CloudVolumeLighting.cginc"
            float4x4 _GroundShadowInverseVP, _GroundShadowView;
            float3 _GroundShadowCameraPosition;
            float4 _GroundShadowSize;
            float _GroundShadowStrength, _GroundShadowDistance;
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(float4 vertex : POSITION) { v2f o; o.pos = vertex; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.pos.xy * _GroundShadowSize.zw;
                float raw = tex2Dlod(_CameraDepthTexture, float4(uv,0,0)).r;
                #if defined(UNITY_REVERSED_Z)
                if (raw <= 0) return float4(1,1,1,1e20);
                #else
                if (raw >= 1) return float4(1,1,1,1e20);
                raw = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
                #endif
                float2 clip = uv * 2 - 1;
                #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;
                #endif
                float4 world = mul(_GroundShadowInverseVP, float4(clip, raw, 1));
                float3 position = world.xyz / world.w;
                float fade = 1 - smoothstep(_GroundShadowDistance * 0.8, _GroundShadowDistance, distance(position,_GroundShadowCameraPosition));
                float weight = _GroundShadowStrength * fade;
                float t = 1.0;
                // 零贡献仍输出接收深度,供双边重建拒绝异面样本。
                [branch] if (weight != 0.0)
                    t = lerp(1, CloudSunTransmittance(position, -lightDir), weight);
                float depth = -mul(_GroundShadowView, float4(position,1)).z;
                return float4(t,t,t,depth);
            }
            ENDCG
        }

        Pass
        {
            Name "GroundShadowApply"
            Blend DstColor Zero
            ColorMask RGB
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _CameraDepthTexture, _VolkenGroundShadowTex;
            float4 _GroundShadowSize;
            float4x4 _GroundShadowInverseVP, _GroundShadowView;
            struct v2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; };
            v2f vert(float4 vertex : POSITION)
            {
                v2f o; o.pos = vertex;
                o.screen = float4(vertex.xy * 0.5 + 0.5,0,1);
                #if UNITY_UV_STARTS_AT_TOP
                o.screen.y = 1 - o.screen.y;
                #endif
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                float raw = tex2D(_CameraDepthTexture,uv).r;
                #if defined(UNITY_REVERSED_Z)
                if (raw <= 0) return 1;
                #else
                if (raw >= 1) return 1;
                raw = lerp(UNITY_NEAR_CLIP_VALUE,1,raw);
                #endif
                float2 clip = uv * 2 - 1;
                #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;
                #endif
                float4 world = mul(_GroundShadowInverseVP,float4(clip,raw,1));
                float depth = -mul(_GroundShadowView,float4(world.xyz/world.w,1)).z;
                float2 grid = uv * _GroundShadowSize.xy - 0.5;
                float2 baseUv = (floor(grid) + 0.5) * _GroundShadowSize.zw;
                float2 f = frac(grid);
                float sum = 0, weightSum = 0;
                [unroll] for (int y=0; y<2; y++)
                [unroll] for (int x=0; x<2; x++)
                {
                    float4 sample = tex2Dlod(_VolkenGroundShadowTex,float4(baseUv + float2(x,y)*_GroundShadowSize.zw,0,0));
                    float w = (x ? f.x : 1-f.x) * (y ? f.y : 1-f.y);
                    w *= exp(-abs(sample.a-depth) / max(1,depth * 0.02));
                    sum += sample.r * w; weightSum += w;
                }
                float t = weightSum > 1e-5 ? sum / weightSum : 1;
                return float4(t,t,t,1);
            }
            ENDCG
        }

        Pass
        {
            Name "GroundShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(float4 vertex : POSITION) { v2f o; o.pos = UnityObjectToClipPos(vertex); return o; }
            float4 frag(v2f i) : SV_Target { clip(-1); return 0; }
            ENDCG
        }

        Pass
        {
            Name "LightningShadow"
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "CloudVolumeLighting.cginc"
            float frag(v2f_img i) : SV_Target
            {
                float2 pixel = floor(i.uv * float2(297.0, 132.0));
                float2 tile = floor(pixel / 33.0);
                float slice = tile.x + 9.0 * tile.y;
                if (slice >= 33.0) return 20.0;
                float3 grid = float3(pixel - tile * 33.0, slice) / 32.0;
                float3 pos = _LightningPositionRange.xyz + (grid * 2.0 - 1.0) * _LightningPositionRange.w;
                return TraceLightningOpticalDepth(pos);
            }
            ENDCG
        }

        Pass
        {
            Name "Upscale"

            // 固定低清格点的时序上采样:按运动量混合有效历史与本帧。
            // 无云 / 相机内命中 / 无效历史必须保留本帧;alpha 始终为透射率。

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

            Texture2D<float4> _MainTex;             // 本帧低清 raymarch 颜色(CloudTex)
            SamplerState sampler_MainTex;
            Texture2D<float> CloudDepthTex;         // 本帧低清 raymarch 云面距离
            SamplerState samplerCloudDepthTex;
            Texture2D<float4> CloudMVTex;          // 当前像素的 UV 位移与上一相机云距离。
            SamplerState samplerCloudMVTex;
            Texture2D<float> CombinedDepthTex;      // 全清场景深度
            SamplerState samplerCombinedDepthTex;
            Texture2D<float4> HistoryTex;           // 上一帧全清上采样结果
            SamplerState samplerHistoryTex;
            Texture2D<float> HistoryDepthTex;       // 上一帧全清场景深度
            SamplerState samplerHistoryDepthTex;
            Texture2D<float> HistoryCloudDepthTex;  // 上一帧全清云面距离
            SamplerState samplerHistoryCloudDepthTex;

            float _UseTemporal;       // 0=使用用户残影权重,1=按运动量混合
            float _TssBlend;          // 静止时本帧混合权重
            float historyBlend;       // TSS关:历史权重(运动残影,如 0.90)
            float historyDepthThreshold;
            float stepSize;

            Texture3D<float> CloudShapeTex;
            float4 _MainTex_TexelSize;
            float _TanHalfFovV, _Aspect, cloudScale;
            // 在密度积分之后重建颜色和透射率,不按屏幕足迹改变世界密度或命中距离。
            float4 ReconstructCurrentCloud(float2 uv, float4 center, float cloudDistance, float sceneEyeDepth)
            {
                float2 ndc = uv * 2.0 - 1.0;
                float2 rayPlane = float2(ndc.x * _Aspect, ndc.y) * _TanHalfFovV;
                float sceneRayDepth = sceneEyeDepth * sqrt(1.0 + dot(rayPlane, rayPlane));
                if (cloudDistance <= 0.001 || cloudDistance > sceneRayDepth || cloudScale <= 0.0)
                    return center;

                uint noiseWidth, noiseHeight, noiseDepth;
                CloudShapeTex.GetDimensions(noiseWidth, noiseHeight, noiseDepth);
                float2 texel = abs(_MainTex_TexelSize.xy);
                float worldPixel = cloudDistance * 2.0 * _TanHalfFovV * texel.y;
                float noiseVoxel = 1.0 / max(noiseWidth * cloudScale, 1e-6);
                float blend = smoothstep(1.0, 4.0, worldPixel / max(noiseVoxel, 0.001));
                if (blend <= 0.0) return center;

                float4 sum = center * 4.0;
                float weightSum = 4.0;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0) continue;
                    float2 sampleUv = uv + float2(x, y) * texel;
                    if (any(sampleUv < 0.0) || any(sampleUv > 1.0)) continue;
                    float neighborDepth = CloudDepthTex.SampleLevel(samplerCloudDepthTex, sampleUv, 0);
                    if (neighborDepth > sceneRayDepth) continue;
                    float weight = (x == 0 ? 2.0 : 1.0) * (y == 0 ? 2.0 : 1.0);
                    sum += _MainTex.SampleLevel(sampler_MainTex, sampleUv, 0) * weight;
                    weightSum += weight;
                }
                return lerp(center, sum / weightSum, blend);
            }

            // 历史颜色包络:当前帧 3×3 的 min/max 限制历史,旧云轮廓不能越过本帧邻域。
            // 颜色与透射率一起钳;alpha 容差更小,旧不透明轮廓收得更快。中心取重建后的本帧值。
            float4 ClampHistoryToCurrent(float2 uv, float4 fresh, float4 history)
            {
                float2 texel = abs(_MainTex_TexelSize.xy);
                float2 margin = texel * 0.5;
                float4 minimum = fresh;
                float4 maximum = fresh;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0) continue;
                    float2 sampleUv = clamp(uv + float2(x, y) * texel, margin, 1.0 - margin);
                    float4 current = _MainTex.SampleLevel(sampler_MainTex, sampleUv, 0);
                    minimum = min(minimum, current);
                    maximum = max(maximum, current);
                }
                float4 expansion = float4(0.025, 0.025, 0.025, 0.015);
                return clamp(history, minimum - expansion, maximum + expansion);
            }

            // 单目标输出:颜色走 Graphics.Blit(自动绑 _MainTex,已由 Dilate/深度 pass 验证可靠)。
            // 云面距离历史由 CloudRenderer 直接 Blit 低清 cloudDepthTex → 全清 historyCloudDepthTex。
            float4 frag(v2f i) : SV_Target
            {
                // 颜色双线性上采样;云距离 / MV 用同一低清格点,不混合不同云面。
                float4 fresh = _MainTex.Sample(sampler_MainTex, i.uv);
                float freshDepth = CloudDepthTex.Sample(samplerCloudDepthTex, i.uv);
                float currentDepth = CombinedDepthTex.Sample(samplerCombinedDepthTex, i.uv);
                fresh = ReconstructCurrentCloud(i.uv, fresh, freshDepth, currentDepth);
                if (_UseTemporal < 0.5 && historyBlend <= 0.0) return fresh;
                // 本帧 raymarch 在此像素是否找到云:看云面距离(>0 有云)。
                // alpha 是透射率;无云为 1,地表阴影也会降低它,不能用它判断云命中。
                bool hasFreshCloud = freshDepth > 0.0;
                // 相机位置命中缺少稳定的重投影锚点,保留本帧而不复用历史。
                if (hasFreshCloud && freshDepth <= 0.001) return fresh;
                float4 motion = CloudMVTex.Sample(samplerCloudMVTex, i.uv);
                float2 mv = motion.xy;
                float2 reprojUV = i.uv + mv;
                bool inBounds = (min(reprojUV.x, reprojUV.y) >= 0.0) && (max(reprojUV.x, reprojUV.y) <= 1.0);
                // These cases already have zero history weight; skip all three history texture reads.
                [branch] if (!inBounds || motion.z * 1e5 <= 0.001 ||
                    (_UseTemporal >= 0.5 && length(mv) * 200.0 >= 1.0)) return fresh;

                // 历史接受判据:场景遮挡与上一相机预期云面距离匹配。
                float historySceneDepth = HistoryDepthTex.Sample(samplerHistoryDepthTex, reprojUV);
                float depthDiff = abs(currentDepth - historySceneDepth) / max(currentDepth, 0.001);
                float depthWeight = 1.0 - smoothstep(historyDepthThreshold * 0.5, historyDepthThreshold, depthDiff);
                float prevCloudDepth = HistoryCloudDepthTex.Sample(samplerHistoryCloudDepthTex, reprojUV);
                float expectedCloudDepth = motion.z * 1e5;
                float cloudTolerance = max(0.1, max(2.0 * stepSize, expectedCloudDepth * 0.02));
                float cloudMatch = 1.0 - smoothstep(cloudTolerance * 0.5, cloudTolerance, abs(prevCloudDepth - expectedCloudDepth));
                float cloudGate = prevCloudDepth > 0.001 && expectedCloudDepth > 0.001 ? cloudMatch : 0.0;
                float validHist = (inBounds ? 1.0 : 0.0) * depthWeight * cloudGate;

                float4 history = HistoryTex.Sample(samplerHistoryTex, reprojUV);
                float4 result;

                if (_UseTemporal < 0.5)
                {
                    // === TSS 关:运动残影(全清 raymarch + 重投影历史) ===
                    float blend = historyBlend * validHist;
                    result = (1.0 - blend) * fresh + blend * history;
                }
                else
                {
                    // 运动自适应混合:快速拖动(|MV| 大)时加大本帧分量 → 边缘不拖影;
                    // 静止时回到 _TssBlend 时序降噪。MV 单位是低清 UV(0..1):
                    // ~0.005 UV(网格2 时约 7 屏像素/帧)即视为快移 → 纯本帧。
                    // 邻域钳制只在历史真正参与时采样;TSS 关的"运动残影"是用户显式选择,不钳制。
                    float mvLen = length(mv);
                    float motionW = saturate(mvLen * 200.0);
                    float tssBlend = lerp(_TssBlend, 1.0, motionW);

                    // Raymarch 每帧覆盖固定低清格点;轮换写入权重不能产生额外空间样本。
                    if (hasFreshCloud && validHist > 0.5)
                    {
                        // 历史降噪,本帧追运动;快移 → 纯本帧。
                        // 透射率差越大历史越不可信:历史权重再乘 0.5~1,向本帧收敛更快。
                        float4 historyValue = ClampHistoryToCurrent(i.uv, fresh, history);
                        float historyWeight = (1.0 - tssBlend) *
                            lerp(0.5, 1.0, saturate(1.0 - abs(historyValue.a - fresh.a)));
                        result = lerp(historyValue, fresh, 1.0 - historyWeight);
                    }
                    else if (hasFreshCloud)
                    {
                        result = fresh;                 // 本帧有效、历史不可用 → 直接本帧
                    }
                    else if (validHist > 0.5)
                    {
                        // 本帧无云、历史有云:拖影主因。当前帧是真实值 → 向"无云"(本帧)收敛,
                        // 不再整份保留旧云(旧逻辑 result=history 会让鬼影跟着云一直拖)。
                        // 0.85:残影 1 帧内降到 15%、2 帧内 ~2%,边缘后撤痕迹基本不可见。
                        float4 historyValue = ClampHistoryToCurrent(i.uv, fresh, history);
                        result = lerp(historyValue, fresh, max(tssBlend, 0.85));
                    }
                    else
                    {
                        result = fresh;
                    }
                }

                return result;
            }
            ENDCG
        }

        Pass
        {
            Name "OrbitClouds"

            // 轨道云:壳面宏观分布决定覆盖,分布差分提供浮雕,不做体积 / 光照步进。
            // 共享分布、风与旋转;单层覆盖不等于体积积分轮廓。

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            // === 与 Clouds pass 同源的 uniforms(orbit 需要的那组;均由现有 C# 每帧/静态设置) ===
            // 注意:必须声明在 vert 之前(Unity 编译器要求先声明后使用,否则 vert 引用处报未声明标识符)。
            float3 sphereCenter;
            float surfaceRadius;
            float maxCloudHeight;
            float3 _CamPos;
            float3 _CamFwd;              // 相机前向(世界),深度换算用(已每帧设在 material 上)
            float3 _CamRight;            // 相机右向(世界),viewDir 重建用(C# 每帧设置)
            float3 _CamUp;               // 相机上向(世界),viewDir 重建用(C# 每帧设置)
            float _TanHalfFovV;          // tan(垂直半 FOV),viewDir 重建用(C# 每帧设置)
            float _Aspect;               // 相机宽高比,viewDir 重建用(C# 每帧设置)
            float currentRotation;
            float3 cloudOffset;
            float cloudScale;
            float detailScale;
            float detailStrength;
            float4 cloudLayerHeights;
            float4 cloudLayerSpreads;
            float4 cloudLayerStrengths;
            float cloudCoverage;
            float cloudDensity;
            float cloudAbsorption;       // Beer 吸收系数(与体积云 transmittance 积累同源)
            float ambientLight;
            float _CloudSunOcclusion;
            float4 _CloudSolarColor;
            float4 cloudColor;
            float3 lightDir;
            float4 phaseParams;
            float multiScatterBlend;
            float forwardScatteringBias;
            float silverLiningIntensity;
            // 轨道云参数(静态)
            float orbitSampleAltitude;   // 壳面锚点高度,0 = maxCloudHeight。
            float orbitDensityBoost;     // 光学厚度乘数(与体积云 Beer 积累对齐;1=中性)
            float orbitBrightness;       // 亮度缩放(与体积云 Additive 强度对齐)
            float orbitReliefStrength;   // 密度梯度法线浮雕强度(KSA normal-map 等效,0=关)
            float orbitDetailStrength;   // detail 噪声作为云内"纹理"明暗变化强度(0=关)
            float _OrbitDebugMode;       // 调试:>0.5 输出 红=着色后不透明度,绿=原始光学厚度足迹

            Texture3D<float> CloudShapeTex;
            SamplerState samplerCloudShapeTex;
            Texture3D<float> CloudDetailTex;
            SamplerState samplerCloudDetailTex;
            Texture2D<float2> PlanetMapTex;
            SamplerState samplerPlanetMapTex;
            TextureCube<float4> StockCloudCube;
            SamplerState samplerStockCloudCube;
            float useStockCloudMap;
            float stockMapStrength;
            float stockMaskInfluence;
            float stockMapLayer;
            float4 stockLayerValid;
            float stockAlignSign;
            float stockAlignAngleOffset;
            float stockDensityScale;       // 方案 A:区域内密度缩放 0..1(1=恒等)
            float4x4 planetToBody;

            // 本相机当前场景线性眼深,每次轨道绘制前独立绑定 combinedDepthTex。
            // 使用 Blit UV 约定,与 Composite 的 SceneDepthTex 一致。
            Texture2D<float> DepthTex;
            SamplerState samplerDepthTex;

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
                // 与 Clouds pass 完全一致的 viewDir 重建(显式相机轴 + C# 传的 fov/aspect)。
                // 不用 unity_CameraInvProjection:若游戏相机投影非标准(自定义矩阵/宽高比偏差),
                // 两条路径会差一个随相机旋转(角速度)而变的角量 → 2D 与体积云相对转动/错位。
                // uv → ndc:Blit 的 uv 与 _ProjectionParams 约定 —— D3D 时 uv.y=0 在顶部,先翻到 y=0 底部。
                float2 uvFlip = float2(v.uv.x, _ProjectionParams.x < 0.0 ? 1.0 - v.uv.y : v.uv.y);
                float2 ndc = uvFlip * 2.0 - 1.0;
                o.viewDir = _CamFwd + _CamRight * (ndc.x * _TanHalfFovV * _Aspect) - _CamUp * (ndc.y * _TanHalfFovV);
                return o;
            }

            float2 RaySphereIntersect(float3 pos, float3 dir, float radius)
            {
                float3 offset = pos - sphereCenter;
                float a = dot(dir, dir);
                float b = 2 * dot(offset, dir);
                float c = dot(offset, offset) - radius * radius;
                float d = b * b - 4 * a * c;
                if (d < 0.0) return -1.0;
                float sqrtD = sqrt(d);
                return float2((-b - sqrtD) / (2 * a), (-b + sqrtD) / (2 * a));
            }

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

            float4 SampleStockDistribution(float3 dir, float windAngle)
            {
                float yAngle = windAngle + stockAlignSign * (stockAlignAngleOffset * 0.0174532925);
                float ca = cos(yAngle);
                float sa = sin(yAngle);
                float3 sd = float3(dir.x * ca - dir.z * sa, dir.y, dir.x * sa + dir.z * ca);
                sd = mul(planetToBody, float4(sd, 0.0)).xyz;
                return StockCloudCube.SampleLevel(samplerStockCloudCube, sd, 0);
            }

            // 与体积共享宏观分布、旋转和风;shape 是固定平均基准,覆盖不读周期切片。
            float SampleMacroCoverage(float3 worldPos, float shape)
            {
                float3 offset = worldPos - sphereCenter;

                float cosAngle = cos(currentRotation);
                float sinAngle = sin(currentRotation);
                float3 rotatedOffset = float3(
                    offset.x * cosAngle - offset.z * sinAngle,
                    offset.y,
                    offset.x * sinAngle + offset.z * cosAngle
                );

                float3 dir = normalize(rotatedOffset);
                float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);

                spherical.x += cloudOffset.x;
                float latFactor = sin(spherical.y * 3.14159265);
                spherical.y += cloudOffset.z * 0.25 * latFactor;

                float2 planetMap = PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);

                float4 stock = 0.0;
                if (useStockCloudMap > 0.5)
                {
                    stock = SampleStockDistribution(dir, cloudOffset.x * 6.28318530718);
                }
                float stockEff = useStockCloudMap * stockMapStrength;
                float selValid = lerp(lerp(stockLayerValid.x, stockLayerValid.y, step(0.5, stockMapLayer)), stockLayerValid.z, step(1.5, stockMapLayer));
                float4 valid = lerp(selValid.xxxx, float4(stockLayerValid.x, stockLayerValid.y, stockLayerValid.z, stockLayerValid.x), step(2.5, stockMapLayer));
                float stockMaskValid = stockLayerValid.w;
                float stockMask = lerp(1.0, stock.a, stockMaskInfluence * stockEff * stockMaskValid);
                float selChannel = lerp(lerp(stock.r, stock.g, step(0.5, stockMapLayer)), stock.b, step(1.5, stockMapLayer));
                float4 stockBandRaw = lerp(selChannel.xxxx, float4(stock.r, stock.g, stock.b, stock.r), step(2.5, stockMapLayer));
                float4 stockBand = stockBandRaw * stockMask;

                // 方案 A:区域内密度缩放(默认 1 → 恒等 → 现状逐字节一致;0 仍 0 → 足迹边缘保留)
                float4 stockCov = stockBand * stockDensityScale;

                float4 mapVal = lerp(float4(planetMap.r, planetMap.g, planetMap.r, planetMap.r), stockCov, stockEff * valid);
                float4 layers;
                layers.x = cloudLayerStrengths.x * mapVal.x;
                layers.y = cloudLayerStrengths.y * mapVal.y;
                layers.z = cloudLayerStrengths.z * mapVal.z;
                layers.w = cloudLayerStrengths.w * mapVal.w;

                float4 active = step(0.0001, cloudLayerStrengths);
                // dist 门乘 valid:该层在游戏自带云中不存在(valid=0)时 shape 门不压死,回退 planetMap。
                float4 dist = lerp(float4(1.0, 1.0, 1.0, 1.0), stockBand, stockEff * valid);
                // 覆盖掩膜 = 形状×分布门 + 层强度 + 全局覆盖偏置(无高度 falloff)
                float mask = shape * (dist.x + dist.y + active.z * dist.z + active.w * dist.w)
                           + layers.x + layers.y + layers.z + layers.w
                           + cloudCoverage - 1.0;
                return max(0.0, mask);
            }

            // 沿用体积廉价密度的旋转与扭曲帧,局部 detail 只调制明暗,不改变宏观覆盖。
            float SampleDetailAlbedo(float3 worldPos, float noiseLod)
            {
                float3 offset = worldPos - sphereCenter;
                float cosAngle = cos(currentRotation);
                float sinAngle = sin(currentRotation);
                float3 rotatedOffset = float3(
                    offset.x * cosAngle - offset.z * sinAngle,
                    offset.y,
                    offset.x * sinAngle + offset.z * cosAngle
                );
                float3 warpOffset = float3(
                    sin(rotatedOffset.y * 0.05 + rotatedOffset.z * 0.03),
                    cos(rotatedOffset.x * 0.04 - rotatedOffset.z * 0.05),
                    sin(rotatedOffset.x * 0.03 + rotatedOffset.y * 0.04)
                ) * 0.15;
                float3 warpedUv = rotatedOffset + warpOffset;
                // 比密度采样更细的尺度(×5)→ 云内细颗粒
                return CloudDetailTex.SampleLevel(samplerCloudDetailTex, warpedUv * (detailScale * 5.0), noiseLod);
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 camPos = _CamPos;
                float3 viewDir = normalize(i.viewDir);

                // 场景深度(观察射线米)= LinearEyeDepth / cos(θ),θ = 射线与相机前向夹角。
                // 与 Clouds pass 的 `viewLength * DepthTex` 同义(其 viewLength=1/cos(θ));
                // Blit uv 约定与 Composite 采样 SceneDepthTex 一致。
                float cosTheta = max(dot(viewDir, _CamFwd), 1e-3);
                float sceneDepth = DepthTex.SampleLevel(samplerDepthTex, i.uv, 0) / cosTheta;

                // 壳面采样现有宏观分布;覆盖近似不包含体积列积分,不保证两种表示轮廓相同。
                float sampleAlt = clamp(orbitSampleAltitude, 0.0, maxCloudHeight);
                if (sampleAlt < 1.0) sampleAlt = maxCloudHeight;   // 自动 = 云壳顶(体积云外边界)
                float2 shellHit = RaySphereIntersect(camPos, viewDir, surfaceRadius + sampleAlt);
                float tS = max(shellHit.x, 0.0);
                float3 pShell = camPos + viewDir * tS;
                // 射线没碰到采样球面 → 无云
                if (shellHit.y < 0.0)
                    return float4(0.0, 0.0, 0.0, 1.0);

                // 渲染顺序修正:craft/地形在壳点之前 → 2D 云被场景遮挡。
                // 与体积云 raymarch 的 depth 截断(`maxRayDist=min(maxRayDist,depth)`,
                // `maxRayDist<=startRayDist → 透明`)行为一致;否则 2D 云画在 craft 前。
                if (sceneDepth <= tS)
                    return float4(0.0, 0.0, 0.0, 1.0);

                // 壳点可见性:远交点 / 地面遮挡 / 场景深度 —— 采样点必须落在可见段内
                float2 groundI = RaySphereIntersect(camPos, viewDir, surfaceRadius);
                float tEnd = shellHit.y;
                if (groundI.y > 0.0 && groundI.x > 0.0 && groundI.x < tEnd)
                    tEnd = groundI.x;
                tEnd = min(tEnd, sceneDepth);
                if (tS > tEnd)
                    return float4(0.0, 0.0, 0.0, 1.0);

                float3 up = normalize(pShell - sphereCenter);

                // 宏观覆盖取现有分布;平均噪声只校准原预设,不把周期切片当全球云轮廓。
                float shapeMean = CloudShapeTex.SampleLevel(samplerCloudShapeTex, float3(0.5, 0.5, 0.5), 16);
                float detailMean = CloudDetailTex.SampleLevel(samplerCloudDetailTex, float3(0.5, 0.5, 0.5), 16);
                float macroShape = shapeMean - (1.0 - shapeMean) * (1.0 - shapeMean) * detailStrength * detailMean;
                float cov = SampleMacroCoverage(pShell, macroShape);
                float coverage = saturate(cov * orbitDensityBoost);

                // --- KSA 式法线浮雕:壳点处水平覆盖梯度(程序化 normal-map,同源) ---
                float3 tangent, bitangent;
                {
                    float3 refUp = abs(up.y) > 0.99 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
                    tangent = normalize(cross(up, refUp));
                    bitangent = cross(up, tangent);
                }
                // 梯度步长 ≈ 6% shapeScale,夹到 [150, 2500] 米(分布横向特征尺度)
                float arc = clamp(0.06 / max(cloudScale, 1e-6), 150.0, 2500.0);
                float3 pEast = pShell + tangent * arc;
                float3 pNorth = pShell + bitangent * arc;
                float cEast = SampleMacroCoverage(pEast, macroShape);
                float cNorth = SampleMacroCoverage(pNorth, macroShape);
                float2 grad = float2(cEast - cov, cNorth - cov) * orbitReliefStrength;
                float3 N = normalize(up - tangent * grad.x - bitangent * grad.y);

                // --- 细节噪声作为云内"纹理"(逐像素明暗变化) ---
                float albedo = 1.0;
                if (orbitDetailStrength > 0.0)
                {
                    uint detailW, detailH, detailD;
                    CloudDetailTex.GetDimensions(detailW, detailH, detailD);
                    float albedoLod = log2(max(1.0, detailW * 5.0 / 64.0));
                    albedo += orbitDetailStrength * (SampleDetailAlbedo(pShell, albedoLod) - 0.5);
                }

                // --- 光照:Lambertian(浮雕法线)+ phase 银边 ---
                float ndl = saturate(dot(N, -lightDir));
                float light = lerp(ambientLight, 1.0, ndl);
                float phaseValue = CloudPhase(dot(viewDir, -lightDir), multiScatterBlend);

                // 轨道壳面沿用太阳昼夜门控,夜侧不添加环境照明。
                // 用壳法线 up(宏观昼夜),不用浮雕 N(细节不驱动昼夜);
                // 晨昏线带宽 0.1 ≈ 游戏 _DuskReach = 0.1·AtmosSizeScale(无大气时为硬过渡)。
                float dayFactor = smoothstep(-0.1, 0.0, dot(up, -lightDir));

                // 调试模式:红 = 覆盖(实际显示),绿 = 壳面覆盖掩膜足迹(范围诊断)
                if (_OrbitDebugMode > 0.5)
                    return float4(coverage, saturate(cov * 5.0), 0.0, 1.0);

                float3 col = cloudColor.rgb * light * albedo * coverage * orbitBrightness * dayFactor;
                // 银边:晨昏线掠射窄带(游戏参考:无任何夜侧加亮项)。
                // 去掉外层重复的 silverLiningIntensity(CloudPhase 内部已乘一次);
                // (1.0-ndl) 加权仅当 ndl→0 时亮(背光轮廓),夜侧被 dayFactor 压到 0。
                col += cloudColor.rgb * phaseValue * coverage
                     * pow(saturate(1.0 - ndl), 3.0) * dayFactor * 0.5 * orbitBrightness;
                if (_CloudSunOcclusion > 0.5) col *= max(0.0, _CloudSolarColor.rgb);

                // 与体积云和 Composite 一致:alpha 表示透射率。
                return float4(col, 1.0 - coverage);
            }
            ENDCG
        }

        Pass
        {
            Name "Composite"
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
        
            #include "UnityCG.cginc"
            #include "../../Weather/Fog/Shader/FogCommon.cginc"
        
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
        
            Texture2D<float4> CloudLightningTex;
            SamplerState samplerCloudLightningTex;
            Texture2D<float4> UpscaledCloudTex;
            SamplerState samplerUpscaledCloudTex;
            Texture2D<float4> OrbitCloudTex;   // 轨道云(2D 壳着色)输出,按 _OrbitFade 与体积云交叉淡入
            SamplerState samplerOrbitCloudTex;
            Texture2D<float> SceneDepthTex;
            SamplerState samplerSceneDepthTex;
            sampler2D FogCloudDepthTex;
            float _VolkenFogComposite, _FogOrbitAltitude;
            sampler2D _MainTex;
            
            float3 sphereCenter;
            float surfaceRadius;
            float _NearThreshold;
            float _CompositeMode;  // 0.0 = Additive (零干扰), 1.0 = Standard (物理遮挡)
            float _CloudSunOcclusion;
            float _OrbitFade;      // 海拔淡入因子:0 = 纯体积云, 1 = 纯 2D 轨道云
            float _OrbitDebugMode; // 调试:>0.5 → 左右分屏(左=体积云,右=2D 轨道云/覆盖足迹)
            int _LightningSamples; // <2 时没有雷光输入,跳过雷光纹理采样
            
            float4 frag(v2f i) : SV_Target
            {
                float4 volClouds = UpscaledCloudTex.Sample(samplerUpscaledCloudTex, i.uv);
                // 雷光与轨道云只在对应输入存在时采样:它们占全屏纹理读取,关闭时不应付出。
                [branch] if (_LightningSamples >= 2)
                    volClouds.rgb += CloudLightningTex.Sample(samplerCloudLightningTex, i.uv).rgb;
                bool orbitSampled = _OrbitFade > 0.001 || _OrbitDebugMode > 0.5;
                float4 orbitClouds = volClouds;
                if (orbitSampled)
                    orbitClouds = OrbitCloudTex.Sample(samplerOrbitCloudTex, i.uv);
                float4 clouds = orbitSampled ? lerp(volClouds, orbitClouds, _OrbitFade) : volClouds;
                // 调试:无视淡入,左右分屏直接对比范围(须在过渡带内,体积云同帧渲染)
                if (_OrbitDebugMode > 0.5)
                    clouds = (i.uv.x < 0.5) ? volClouds : orbitClouds;
                float4 source = tex2D(_MainTex, i.uv);

                // === Additive Mode (零视觉干扰) ===
                // 直接将云光加到场景上,不改变场景透过率;不采样场景深度 / 雾。
                if (_CompositeMode < 0.5 && _VolkenFogComposite < 0.5)
                    return float4(source.rgb + clouds.rgb, source.a);

                float sceneDepth = SceneDepthTex.Sample(samplerSceneDepthTex, i.uv);

                if (_VolkenFogComposite > 0.5 && _VolkenFogEnabled > 0.5)
                {
                    float3 ray = VolkenFogRay(i.uv);
                    float volDistance = tex2D(FogCloudDepthTex,i.uv).r;
                    float r = _VolkenFogUpRadius.w, h = _VolkenFogOrigin.w;
                    float b = (r+h)*dot(_VolkenFogUpRadius.xyz,ray);
                    float c = (h-_FogOrbitAltitude)*(2*r+h+_FogOrbitAltitude);
                    float disc = b*b-c;
                    float orbitDistance = _VolkenFogRange.y;
                    if (disc>=0)
                    {
                        float root = sqrt(disc);
                        float q = -b-(b>=0 ? root : -root);
                        float other = abs(q)>1e-5 ? c/q : -b-root;
                        float entry = min(q,other), exit = max(q,other);
                        orbitDistance = entry>0 ? entry : max(0,exit);
                    }
                    float blend = _OrbitDebugMode>0.5 ? (i.uv.x<0.5 ? 0 : 1) : _OrbitFade;
                    float4 vf = float4(0,0,0,1), of = float4(0,0,0,1);
                    if (blend<1) vf = VolkenFogAtRay(i.uv,volDistance>0 ? volDistance : _VolkenFogRange.y,ray);
                    if (blend>0) of = VolkenFogAtRay(i.uv,orbitDistance,ray);
                    if (_CompositeMode<0.5)
                        return float4(source.rgb+lerp(volClouds.rgb*vf.a,orbitClouds.rgb*of.a,blend),source.a);
                    bool near = sceneDepth>0 && sceneDepth<_NearThreshold;
                    float factor = _CloudSunOcclusion > 0.5 ? 1.0 : (near ? lerp(0.2,1,smoothstep(0,_NearThreshold,sceneDepth)) : saturate(sceneDepth/5000));
                    float vt = lerp(1,volClouds.a,factor);
                    float ot = lerp(1,orbitClouds.a,factor);
                    float3 vc = vf.a*volClouds.rgb*factor+(1-vt)*vf.rgb;
                    float3 oc = of.a*orbitClouds.rgb*factor+(1-ot)*of.rgb;
                    return float4(source.rgb*lerp(vt,ot,blend)+lerp(vc,oc,blend),source.a);
                }

                // === Additive Mode (零视觉干扰) ===
                // 直接将云光加到场景上，不改变场景透过率
                if (_CompositeMode < 0.5)
                {
                    return float4(source.rgb + clouds.rgb, source.a);
                }

                // === Standard Mode (物理遮挡) ===
                if (_CloudSunOcclusion > 0.5)
                    return float4(source.rgb * clouds.a + clouds.rgb, source.a);
                float nearThreshold = _NearThreshold;
                if (sceneDepth > 0.0 && sceneDepth < nearThreshold)
                {
                    float nearFactor = smoothstep(0.0, nearThreshold, sceneDepth);
                    nearFactor = lerp(0.2, 1.0, nearFactor);
                    
                    float3 finalCloudColor = clouds.rgb * nearFactor;
                    float finalTransmittance = lerp(1.0, clouds.a, nearFactor);
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

        Pass
        {
            Name "ReflectionCloudAdditive"

            // 反射云的加法合成:只输出云 RGB 并 Blend One One 叠进反射 RT,
            // 省掉"临时 RT 合成 + 整幅复制回"的两次全幅操作,也不读场景深度 / 雾。

            Blend One One, Zero One
            ZWrite Off ZTest Always Cull Off

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

            Texture2D<float4> UpscaledCloudTex;
            SamplerState samplerUpscaledCloudTex;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(UpscaledCloudTex.Sample(samplerUpscaledCloudTex, i.uv).rgb, 0.0);
            }
            ENDCG
        }

        Pass
        {
            Name "ReflectionComposite"

            // 反射云合成:把低清 raymarch 结果 additively 叠加到反射 RT 上。
            // 与 Clouds pass 相同的 clip-space 三角形 + UV 翻转约定,保证采样区域对齐;
            // Blend One One = dst.rgb += src.rgb(只加颜色,不读回同一 RT)。
            Cull Off ZWrite Off ZTest Always
            Blend One One

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
                o.vertex = v.vertex;
                float2 uv = float2(v.vertex.x * 0.5 + 0.5, v.vertex.y * 0.5 + 0.5);
                uv.y = _ProjectionParams.x < 0.0 ? 1.0 - uv.y : uv.y;
                o.uv = uv;
                return o;
            }

            Texture2D<float4> UpscaledCloudTex;
            SamplerState samplerUpscaledCloudTex;

            float4 frag(v2f i) : SV_Target
            {
                return float4(UpscaledCloudTex.Sample(samplerUpscaledCloudTex, i.uv).rgb, 0.0);
            }
            ENDCG
        }

    }
}
