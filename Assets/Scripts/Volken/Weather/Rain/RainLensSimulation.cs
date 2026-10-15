using UnityEngine;

namespace Volken.Weather
{
    /// <summary>固定容量的屏幕水珠池;速度和尺寸以屏幕高度为单位,不依赖游戏或 GPU。</summary>
    internal sealed class RainLensSimulation
    {
        internal const int Capacity = 128;

        internal struct Drop
        {
            internal Vector2 position, velocity;
            internal float radius, age, lifetime, pinTime, trail;
            internal bool alive;
            internal float Opacity => Smooth(age / 0.12f) * (1f - Smooth((age / lifetime - 0.65f) / 0.35f));
        }

        internal readonly Drop[] drops = new Drop[Capacity];
        internal int Count { get; private set; }
        private uint _random;
        private float _emission;
        private int _nextSlot;

        internal RainLensSimulation(uint seed) { _random = seed == 0 ? 1u : seed; }

        internal void Clear()
        {
            for (int i = 0; i < Capacity; i++) drops[i] = default;
            Count = 0;
            _emission = 0f;
            _nextSlot = 0;
        }

        internal static float Smooth(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        internal static float Exposure(float upDot, Vector3 velocity, Vector3 forward, float threshold)
        {
            float upward = Smooth((upDot - 0.15f) / 0.60f);
            float speed = velocity.magnitude;
            float fast = Smooth((speed - threshold) / Mathf.Max(1f, threshold));
            float facing = speed > 0.001f ? Mathf.Clamp01(Vector3.Dot(velocity / speed, forward)) : 0f;
            return Mathf.Max(upward, fast * Mathf.Lerp(0.15f, 1f, facing));
        }

        internal static float EnvironmentFade(float altitude, bool hasWater, VolkenWeatherConfig.RainSection rain)
        {
            float height = rain.ceilingAltitude > 1f
                ? Mathf.Clamp01((rain.ceilingAltitude - altitude) / Mathf.Max(1f, rain.ceilingAltitude * rain.ceilingBand)) : 1f;
            float water = rain.underwaterGate && hasWater && altitude < 0f
                ? Mathf.Clamp01(1f + altitude / Mathf.Max(0.25f, rain.underwaterFade)) : 1f;
            return height * water;
        }

        internal void Tick(float dt, float spawnRate, Vector2 gravity, Vector2 air, float fast, float aspect,
            VolkenWeatherConfig.RainLensSection settings)
        {
            if (dt <= 0f) return;
            dt = Mathf.Min(dt, 0.1f);
            aspect = Mathf.Max(0.1f, aspect);
            for (int i = 0; i < Capacity; i++)
            {
                Drop drop = drops[i];
                if (!drop.alive) continue;
                drop.age += dt;
                if (drop.age >= drop.lifetime || drop.position.x < -0.1f || drop.position.x > 1.1f ||
                    drop.position.y < -0.1f || drop.position.y > 1.1f)
                {
                    drops[i] = default;
                    Count--;
                    continue;
                }
                if (drop.age > drop.pinTime)
                {
                    Vector2 outward = new Vector2((drop.position.x - 0.5f) * aspect, drop.position.y - 0.5f);
                    float size = Mathf.Clamp(drop.radius / settings.dropSize, 0.4f, 2f);
                    drop.velocity += (gravity * (0.10f * size) + air * 0.20f + outward * (fast * 0.10f)) * dt;
                    drop.velocity *= Mathf.Exp(-1.2f * dt);
                    drop.velocity = Vector2.ClampMagnitude(drop.velocity, 0.35f);
                    drop.position += new Vector2(drop.velocity.x / aspect, drop.velocity.y) * dt;
                    drop.trail = Mathf.Lerp(drop.trail, Mathf.Min(0.12f, drop.velocity.magnitude * 0.45f), 1f - Mathf.Exp(-8f * dt));
                }
                drops[i] = drop;
            }

            if (spawnRate <= 0f) { _emission = 0f; return; }
            _emission = Mathf.Min(_emission + Mathf.Min(80f, spawnRate) * dt, 8f);
            while (_emission >= 1f)
            {
                _emission -= 1f;
                if (Count >= Capacity) continue;
                for (int scan = 0; scan < Capacity; scan++)
                {
                    int slot = _nextSlot;
                    _nextSlot = (_nextSlot + 1) % Capacity;
                    if (drops[slot].alive) continue;
                    float size = Mathf.Lerp(0.45f, 1.5f, Random01());
                    drops[slot] = new Drop
                    {
                        alive = true,
                        position = new Vector2(Mathf.Lerp(0.03f, 0.97f, Random01()), Mathf.Lerp(0.03f, 0.97f, Random01())),
                        radius = settings.dropSize * size,
                        lifetime = settings.lifetime * Mathf.Lerp(0.7f, 1.3f, Random01()),
                        pinTime = Mathf.Lerp(0.25f, 1.8f, Random01()) / (size * (1f + fast * 2f)),
                    };
                    Count++;
                    break;
                }
            }
        }

        private float Random01()
        {
            _random ^= _random << 13;
            _random ^= _random >> 17;
            _random ^= _random << 5;
            return (_random & 0x00ffffffu) / 16777216f;
        }
    }
}
