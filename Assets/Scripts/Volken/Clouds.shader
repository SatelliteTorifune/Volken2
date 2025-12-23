Shader "Hidden/Clouds"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
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

                //                      vvv depth is stored nonlinearly, this function converts it to a useful value
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

            vert2Frag vert(appdata v)
            {
                vert2Frag o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                // generate the world space view vectors for the edges of the frustum
                o.viewDir = mul(unity_CameraInvProjection, float4(v.uv * 2 - 1, 0, -1));
                o.viewDir = mul(unity_CameraToWorld, float4(o.viewDir, 0));
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

            Texture2D<float4> BlueNoiseTex;
            SamplerState samplerBlueNoiseTex;

            Texture2D<float4> HistoryTex;
            SamplerState samplerHistoryTex;
            Texture2D<float> HistoryDepthTex;
            SamplerState samplerHistoryDepthTex;

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
            //Cloud Layers
            float2 cloudLayerHeights;
            float2 cloudLayerSpreads;
            float2 cloudLayerStrengths;

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
            float globalRotationAngular;
            float currentRotation;

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
            float BeersPowder(float d, float amb) {
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
            
                float shape = CloudShapeTex.SampleLevel(samplerCloudShapeTex, rotatedOffset * cloudScale, 0);
                float detail = CloudDetailTex.SampleLevel(samplerCloudDetailTex, rotatedOffset * detailScale, 0);
                shape -= (1.0 - shape) * (1.0 - shape) * detailStrength * detailFalloff * detail;
            
                float3 dir = normalize(rotatedOffset);
                float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);
            
                //yes the wind and the angular are somehow conflic...but lmao i dgaf
                //no joking,but that's the best effect for now if you don't want to see the pile of shit in northern pole
                // Strong east/west wind (full strength)
                spherical.x += cloudOffset.x;
            
                //TODO the velocity of the cloud should be with direction
                // Safe north/south wind: attenuated by latitude (zero at poles, max at equator)
                float latFactor = sin(spherical.y * 3.14159265);  // ranges 0 at poles to 1 at equator
                spherical.y += cloudOffset.z * 0.25 * latFactor;  // adjust 0.25 for desired strength
            
                float2 layers = cloudLayerStrengths * PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);
            
                float2 falloffExponent = ((r - surfaceRadius) - cloudLayerHeights) / cloudLayerSpreads;
                float2 falloff = exp(-falloffExponent * falloffExponent);
                
                return ((shape * (falloff.x + falloff.y) + layers.x * falloff.x + layers.y * falloff.y) + cloudCoverage - 1.0) * cloudDensity;
            }

            float SampleDensityCheap(float3 worldPos) 
            {
                float3 offset = worldPos - sphereCenter;
                float r = length(offset);
            
                float cosAngle = cos(currentRotation);
                float sinAngle = sin(currentRotation);
                float3 rotatedOffset = float3
                (
                    offset.x * cosAngle - offset.z * sinAngle,
                    offset.y,
                    offset.x * sinAngle + offset.z * cosAngle
                );
            
                float shape = CloudShapeTex.SampleLevel(samplerCloudShapeTex, rotatedOffset * cloudScale, 0);
            
                float3 dir = normalize(rotatedOffset);
                float2 spherical = float2(0.5 * (atan2(dir.z, dir.x) / 3.14159265 + 1.0), acos(dir.y) / 3.14159265);
            
                spherical.x += cloudOffset.x;
            
                float latFactor = sin(spherical.y * 3.14159265);
                spherical.y += cloudOffset.z * 0.25 * latFactor;
            
                float2 layers = cloudLayerStrengths * PlanetMapTex.SampleLevel(samplerPlanetMapTex, spherical, 0);
            
                float2 falloffExponent = ((r - surfaceRadius) - cloudLayerHeights) / cloudLayerSpreads;
                float2 falloff = exp(-falloffExponent * falloffExponent);
            
                return ((shape * (falloff.x + falloff.y) + layers.x * falloff.x + layers.y * falloff.y) + cloudCoverage - 1.0) * cloudDensity;
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

                for (int i = 0; i < lightSamples; i++) {
                    rayPos += step * rayDir;
                    d += step * max(0.0, SampleDensityCheap(rayPos));
                }

                return float2(d, intersect.y - max(0.0, intersect.x));
            }

            float4 frag(vert2Frag i) : SV_Target 
            {
                float3 camPos = _WorldSpaceCameraPos;
                float viewLength = length(i.viewDir);
                float3 viewDir = i.viewDir / viewLength;

                float2 intersect = RaySphereIntersect(camPos, viewDir, surfaceRadius + maxCloudHeight);

                float firstCloudDepth = maxDepth;  // Track first cloud intersection depth
                bool foundCloud = false;
                
                // no intersection in front of the camera
                if (intersect.y < 0.0) {
                    return float4(0.0, 0.0, 0.0, 1.0);
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
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                // offset the sample ray starting position using blue noise to avoid banding
                float blueNoise = BlueNoiseTex.SampleLevel(samplerBlueNoiseTex, blueNoiseScale * i.uv + blueNoiseOffset, 0).r;
                float rayDist = startRayDist + blueNoiseStrength * stepSize * (blueNoise - 0.5) * 1.5;

                // precompute phase values
                float phaseValue = Phase(dot(viewDir, -lightDir));

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
                        //lightTransmittance = BeersPowder(lightSample.x, amb) * exp(-lightSample.y * lightSample.y * scatterCoeff);
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
                float4 history = HistoryTex.SampleLevel(samplerHistoryTex, reprojUV.xy, 0);
                
                float currentDepth = DepthTex.Sample(samplerDepthTex, i.uv);
                float historyDepth = HistoryDepthTex.Sample(samplerHistoryDepthTex, reprojUV.xy);
                float depthDiff = abs(currentDepth - historyDepth) / max(currentDepth, 0.001);
                float depthWeight = depthDiff < historyDepthThreshold ? 1.0 : 0.0;
                
                bool badSample = cloudSurfaceDist >= maxRayDist || (min(reprojUV.x,reprojUV.y) < 0.0) || (max(reprojUV.x,reprojUV.y) > 1.0);
                float finalHistoryBlend = badSample ? 0.0 : historyBlend * depthWeight;
                
                return (1.0 - finalHistoryBlend) * raymarchOutput + finalHistoryBlend * history;

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
        
            float4 frag(v2f i) : SV_Target
            {
                float4 clouds = UpscaledCloudTex.Sample(samplerUpscaledCloudTex, i.uv);
                float4 source = tex2D(_MainTex, i.uv);
                float sceneDepth = SceneDepthTex.Sample(samplerSceneDepthTex, i.uv);
                
                // 添加深度检测，避免与近处透明物体冲突
                float depthThreshold = 5000.0;  
                float depthMask = saturate(sceneDepth / depthThreshold);
                
                // 关键修改：只在远处渲染云
                float3 maskedCloudColor = clouds.rgb * depthMask;
                float maskedTransmittance = lerp(1.0, clouds.a, depthMask);
                
                // Composite
                return float4(source.rgb * maskedTransmittance + maskedCloudColor, source.a);
            }
            ENDCG

        }
    }
}