// RainParticles.shader —— 阶段 3:BIRP 实例化雨滴 shader(正式版,SP2 第一手属性名 + **世界系构轴**)。
//
// 【构轴铁律(2026-09-29 sp2d4 第一手实锤)】雨丝朝向 = **世界空间旋转矩阵** _RotationMatrix
//   (SP2 ParticleDomain.AlignStreaks 同款:列0=侧向1、列1=雨丝轴×拉伸、列2=侧向2),
//   列1 承载网格局部 y(长轴)并已烘焙拉伸。雨丝朝向锁在**世界系**,与相机无关:
//     · 转镜头/暂停/缩放都不改变雨丝朝向(不再是屏幕平面投影 → 不再"贴相机");
//     · 垂直向下看时雨丝与视线近于平行 → 呈点状,不会糊满屏幕。
//    曾用的屏幕平面构轴(阶段 1 Version A)已废弃:世界系固定向量的屏幕投影随视角摆动,
//     真机表现为"雨的朝向随摄像机角速度变 + 像贴在镜头上"。
//
// 【与阶段 2 骨架的关系】顶点路径不变(compute → RenderMeshIndirect → SV_InstanceID 读 _Positions)。
// 【其余要点】① SP2 第一手属性名(_MainTex/_Emission/_MainColor/_InvFade, 不叫 _Color);
//   ② 软粒子(阶段 3.2):采样 CloudRenderer.LinearSceneDepth(RFloat,LinearEyeDepth 米,
//      C# 每帧 SetTexture;深度图未就绪时 C# 置 _InvFade=0 关闭采样);
//   ③ UV 不平铺(阶段 3.3):网格 UV 全 0..1,长度由矩阵列1 表达;uv.y 0=头(亮)→1=尾(淡),
//      头 = +轴 = 相对运动前方(与 SP2 一致:亮头拖尾);
//   ④ 无随机滚转角(SP2 无 roll:侧向由世界 up 参考叉乘得到,十字截面朝向统一,不会叠成"面条")。
//
// 【坐标系铁律(2026-09-29 jnoCode 实锤)】"下" = 径向行星中心 = craft.GravityNormal(帧空间),
//   不是世界 (0,-1,0);下落/构轴方向全部来自 C# 侧的 down。
Shader "Volken/RainParticles"
{
    Properties
    {
        _MainTex ("Main Texture (RGB, A=alpha)", 2D) = "white" {}
        _Emission ("Emission Boost", Range(0, 3)) = 0
        _MainColor ("Main Color", Color) = (0.72, 0.82, 1, 1)
        _InvFade ("Soft Particles Factor", Range(0.01, 3)) = 1
        _Falloff ("Tail Fade", Range(0, 1)) = 0.9
        // 纵深线索(治"快速缩放时像一层平面"):域内距离衰减 / 逐粒长宽变化 / 逐粒滚转(默认关)
        _DistanceFade ("Distance Fade (depth cue)", Range(0, 1)) = 0.35
        _StreakVariation ("Per-streak Size Variation", Range(0, 1)) = 0.5
        _StreakRoll ("Per-streak Roll", Range(0, 1)) = 0
        // 亚像素雨丝会被光栅化成断续点线,人眼会把断点连成"斜的流"(实测:宽 0.0055m ≈ 0.5px 时最明显)。
        //   这里按距离把宽度撑到屏幕宽度 ≥ 本值(像素);**默认 0 = 关**(它曾因退化 pass 把四边形撑到公里级 → GPU 设备丢失)。
        _MinStreakWidthPx ("Min On-screen Streak Width (px)", Range(0, 4)) = 0
        // 部署探针(不是视觉效果):C# SELFCHECK 读它确认**包里装的是新版 shader** ——
        //   只在 HLSL 里声明的 uniform(_RotationMatrix/_Positions/_FadeAmount/_LinearSceneDepth)
        //   不在 Properties 表里,HasProperty 未必看得到,不能用来判版本。
        //   本 shader 语义每次改动就 +1(5 = 最小屏幕宽度守卫,6 = _RandomData 消毒:yzw 夹到 [0,1] + NaN 归零,
        //                            7 = 水下粒子剔除:粒子中心低于海平面 → 四边形塌成零面积)。
        _ShaderVer ("Shader Build (部署探针)", Float) = 7
        // 域边界淡出(SP2 把 _DomainPos/_DomainRadius 传给 material,唯一合理用途 = 隐藏球域边界/回收突现):
        //   _EdgeFade = 0 关;>0 时在 [1-_EdgeFade, 1]·R 区间把 alpha 渐隐到 0。
        _EdgeFade ("Domain Edge Fade", Range(0, 0.5)) = 0.2
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0   // StructuredBuffer 需要 SM5

            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Positions;   // 与 compute 里 _Positions 同一 buffer(xyz=帧空间位置)
            StructuredBuffer<float4> _RandomData;  // 同一 buffer 的读视图:yzw = 逐粒长/宽倍率与滚转

            sampler2D _MainTex;
            fixed4 _MainColor;
            float _Emission;
            float _InvFade;
            float _Falloff;
            float _FadeAmount;         // 全局透明度(阶段 5 的淡入淡出走这里;SP2 material 同名)
            float4x4 _RotationMatrix;  // 世界系构轴(C# 每帧按 AlignStreaks 算;列1 已含拉伸)
            float3 _DomainPos;         // 球域中心(=相机位置;SP2 material 同名)
            float _DomainRadius;       // 球域半径
            float _EdgeFade;           // 边界淡出宽度比例(0 = 关)
            float _DistanceFade;
            float _StreakVariation;
            float _StreakRoll;
            float _MinStreakWidthPx;
            float3 _SeaCenter;         // 海平面球心(帧空间,与 _Positions 同空间)
            float _SeaRadius;          // 海平面半径(米;0 = 行星无水 → 不做水下剔除)
            float3 _SeaUp;             // 海平面法线(径向向上)= −down;截断雨丝用

            sampler2D _LinearSceneDepth;   // CloudRenderer.combinedDepthTex(RFloat, LinearEyeDepth 米)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screenPos : TEXCOORD1;   // 深度采样用(ComputeScreenPos)
                float eyeDepth : TEXCOORD2;     // 粒子自身线性眼空间深度(正米)
                float edgeFade : TEXCOORD3;     // 域边界淡出系数(顶点算好插值;粒子级量,插值基本恒定)
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                // 世界系构轴:local = 列0*x + 列1*y + 列2*z(列1 = 雨丝轴×拉伸,承载网格长轴 y)。
                // 【列约定证明(免得以后再纠结转置)】HLSL mul(M,v) 的结果 = 各"行"与 v 点积;
                //   仿射变换要求平移落在 M 的第 3 列(HLSL 里 M[i][3])—— 这与 Unity C# 的
                //   Matrix4x4.SetColumn(3, t)(= [0..2][3])索引完全一致;同一 shader 里
                //   mul(UNITY_MATRIX_V, worldPos) 能正确带上平移(eyeDepth 实测正确)即佐证。
                //   故 SetColumn(1, dir) 乘 v.y ✓。
                float3 local = mul((float3x3)_RotationMatrix, v.vertex.xyz);
                // 逐粒外观:长度/宽度倍率 + 绕长轴滚转 —— 整片雨丝完全平行+等长是"像一层平面"的主因之一
                //yzw 由 compute 的 Randomize 写入。**包里 compute 若是旧版(没写 yzw),这里读到的是未初始化显存**
                //    → 天文数字/NaN 倍率把网格"宽"方向拉成几百米 → 整片出现"斜的针"(实测踩过)。所以必须先消毒。
                float4 raw = _RandomData[instanceID];
                bool rndBad = !(raw.x == raw.x && raw.y == raw.y && raw.z == raw.z && raw.w == raw.w);   // NaN != NaN
                float4 rnd = rndBad ? float4(0.5, 0.5, 0.5, 0.5) : clamp(raw, 0.0, 1.0);
                float lenScale = 1.0 + (rnd.y * 2.0 - 1.0) * _StreakVariation * 0.5;
                float widScale = 1.0 + (rnd.z * 2.0 - 1.0) * _StreakVariation * 0.35;
                // 亚像素守卫:把宽度撑到屏幕宽度 ≥ _MinStreakWidthPx 像素(否则雨丝栅格化成断续点线 → 人眼连成"斜流")。
                //必须防退化 pass:_ScreenParams.y / 投影 _m11 为 0(或相机距离异常)时本式会算出天文数字的宽度,
                //    顶点被撑到公里级 → 十万个巨型四边形 → GPU 设备丢失(0x887A0005)实测踩过。所以:门槛 + 硬上限。
                if (_MinStreakWidthPx > 0.0 && _ScreenParams.y >= 8.0)
                {
                    float m11 = abs(UNITY_MATRIX_P._m11);
                    float halfW = max(abs(v.vertex.x), abs(v.vertex.z));
                    float camDist = clamp(length(_Positions[instanceID].xyz - _WorldSpaceCameraPos), 0.2, 500.0);
                    if (m11 > 1e-3 && halfW > 1e-5)
                    {
                        float pxPerMeter = _ScreenParams.y * 0.5 * m11 / camDist;
                        float wantWorldW = _MinStreakWidthPx / pxPerMeter;
                        widScale = min(widScale * max(1.0, wantWorldW / (2.0 * halfW * widScale)), widScale * 8.0 + 4.0);
                    }
                }
                float2 xz = float2(local.x * widScale, local.z * widScale);
                float roll = rnd.w * 6.2831853 * _StreakRoll;
                float rollCos = cos(roll);
                float rollSin = sin(roll);
                local = float3(xz.x * rollCos - xz.y * rollSin, local.y * lenScale, xz.x * rollSin + xz.y * rollCos);

                // 水下剔除 + 水面截断:海平面以下的雨丝不该存在(相机在水面上时,球域有一半泡在水里;
                //   水体是透明队列、不写深度,挡不住它们 → 会看到"水里也在下雨")。
                //   ① 粒子中心在水面下 → 整个四边形塌成零面积(光栅化不出像素;不要改用 alpha=0,透明混合仍留痕)。
                //   ② 中心在水面上、但尾端扎进水里 → 把那一端收到水面上(否则长雨丝会插进水面以下)。
                //   ③ 只用**粒子中心**判在水上/水下,单个粒子整体一起处理,不会把雨丝切成锯齿。
                //   axisUp = 列1(雨丝轴×拉伸)与径向"上"的点积:倾斜雨丝(StreamMode)也按实际倾角折算。
                if (_SeaRadius > 0.0)
                {
                    float3 partPos = _Positions[instanceID].xyz;
                    float seaAlt = length(partPos - _SeaCenter) - _SeaRadius;
                    if (seaAlt < 0.0)
                    {
                        local = 0.0;
                    }
                    else
                    {
                        float3 axis = float3(_RotationMatrix._m01, _RotationMatrix._m11, _RotationMatrix._m21);
                        float axisUp = dot(axis, _SeaUp);
                        if (abs(axisUp) > 1e-3 && seaAlt + local.y * axisUp < 0.0)
                        {
                            local.y = -seaAlt / axisUp;
                        }
                    }
                }
                //  w 必须为 1(是"位置"不是"方向"):UnityWorldToClipPos 内部会强制 w=1 所以
                //   w=0 时 clip 不炸,但 mul(UNITY_MATRIX_V, worldPos) 会丢掉平移 → eyeDepth 全错。
                float4 worldPos = float4(_Positions[instanceID].xyz + local, 1.0);

                o.pos = UnityWorldToClipPos(worldPos);
                o.uv = v.uv;      // 全 0..1,不平铺(uv.y 0=头(亮)→1=尾(淡))
                o.screenPos = ComputeScreenPos(o.pos);
                o.eyeDepth = -mul(UNITY_MATRIX_V, worldPos).z;   // 正米,与 combinedDepthTex 同约定

                // 域边界淡出:粒子在球域外围渐隐 → 球域边界与"出域回收"的突现都不显形。
                // (按**粒子中心**算,不含网格顶点偏移 —— 单个粒子的淡出系数是常量,不会拉花。)
                // 再叠一层**整段域内的距离衰减**当纵深线索:否则 10m 与 40m 的雨丝一样亮,整片读成平面。
                float radial = length(_Positions[instanceID].xyz - _DomainPos);
                float safeR = max(1e-3, _DomainRadius);
                float edge = (_EdgeFade > 0.0001)
                    ? saturate((1.0 - radial / safeR) / max(1e-3, _EdgeFade))
                    : 1.0;
                o.edgeFade = edge * (1.0 - _DistanceFade * saturate(radial / safeR));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _MainColor;
                col.rgb *= 1.0 + _Emission;
                col.a *= _FadeAmount;
                col.a *= i.edgeFade;   // 域边界淡出(隐藏球域边界/回收突现)
                // 尾淡(软边):头亮尾淡,不平铺所以不会夹边糊块
                col.a *= 1.0 - saturate(i.uv.y * _Falloff);
                // 软粒子(阶段 3.2):_InvFade<=0 时(深度图未就绪)不采样,退化为普通透明
                if (_InvFade > 0.0)
                {
                    float sceneDepth = tex2D(_LinearSceneDepth, i.screenPos.xy / i.screenPos.w).r;  // 米
                    col.a *= saturate((sceneDepth - i.eyeDepth) * _InvFade);
                }
                return col;
            }
            ENDCG
        }
    }
}
