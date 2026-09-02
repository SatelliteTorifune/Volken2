using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 方案 C:最优采样序列(移植自 KSA UpscalingPixelSequence.FindOptimalSamplingSequence)。
/// 返回 0..gridX*gridY-1 的一个排列:每步选"与已选所有点(含周期镜像邻居)最小距离之和最大"的格子。
/// 效果:连续几帧采样的格子尽量远离,时域上互相补位。
/// 在重建/格网尺寸变化时算一次并缓存,每帧按 frameNumber 取一个。
/// </summary>
public static class UpscalingPixelSequence
{
    /// <summary>生成最优采样序列。grid 至少为 1x1(此时返回 {0})。</summary>
    public static int[] FindOptimalSamplingSequence(int gridDimensionX, int gridDimensionY)
    {
        if (gridDimensionX < 1) gridDimensionX = 1;
        if (gridDimensionY < 1) gridDimensionY = 1;

        int count = gridDimensionX * gridDimensionY;
        var order = new List<int> { 0 };
        var remaining = new List<int>();
        for (int k = 1; k < count; k++) remaining.Add(k);

        while (remaining.Count > 0)
        {
            int best = remaining[0];
            float bestScore = -1f;
            foreach (int candidate in remaining)
            {
                float score = 0f;
                foreach (int chosen in order)
                    score += CalculateMinDistance(chosen, candidate, gridDimensionX, gridDimensionY);
                if (score > bestScore) { bestScore = score; best = candidate; }
            }
            order.Add(best);
            remaining.Remove(best);
        }
        return order.ToArray();
    }

    /// <summary>两点之间的最小距离(考虑 ±1 个周期的镜像邻居,保证图块间无缝)。</summary>
    static float CalculateMinDistance(int prev, int cur, int gx, int gy)
    {
        int px = prev % gx, py = prev / gx;
        int cx = cur % gx, cy = cur / gx;
        float min = float.PositiveInfinity;
        // 周期边界:邻居在 ±1 格(含镜像)里取最小距离 → 保证图块间无缝衔接
        for (int i = -1; i <= 1; i++)
        for (int j = -1; j <= 1; j++)
        {
            float dx = px - (cx + i * gx);
            float dy = py - (cy + j * gy);
            min = Mathf.Min(min, Mathf.Sqrt(dx * dx + dy * dy));
        }
        return min;
    }
}
