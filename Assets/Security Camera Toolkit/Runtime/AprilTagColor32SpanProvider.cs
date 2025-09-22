using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

#if BURST_PRESENT
using Unity.Burst;
#endif

/// <summary>
/// 从 I422 的 Y 平面生成可复用的 RGBA Color32 缓冲：
/// - 识别侧：GetLatestColor32Span(out w, out h)（零分配）
/// - 调试侧：GetLatestTexture2D()（返回一张 Texture2D(RGBA32)）
/// 额外：支持 FlipY（默认 true 修正倒立），可选 expandLimitedRange（把 Y=16..235 映射到 0..255）
/// </summary>
public sealed class AprilTagColor32SpanProvider : IDisposable
{
    private NativeArray<Color32> _rgba;
    private int _width, _height;
    private bool _hasFrame;

    // 调试纹理缓存（主线程使用）
    private Texture2D _debugTex;

    // ===== 可调选项 =====
    /// <summary>是否对源亮度图做垂直翻转（修正倒立）。默认 true。</summary>
    public bool FlipY { get; set; } = true;

    /// <summary>是否把视频常见的 16..235 亮度范围映射到 0..255（提升对比度）。默认 false。</summary>
    public bool ExpandLimitedRange { get; set; } = false;

    /// <summary>
    /// yPlane: I422 的 Y 平面（长度应 >= strideY * height）
    /// width/height: 亮度图分辨率
    /// strideY: 每行字节数（常见 == width；若有对齐可能 > width）
    /// </summary>
    public void UpdateFromI422_Y(ReadOnlySpan<byte> yPlane, int width, int height, int strideY)
    {
        // ===== 输入健壮性检查 =====
        if (width <= 0 || height <= 0)
        {
            Debug.LogError($"[AprilTagColor32SpanProvider] Invalid size: {width}x{height}");
            return;
        }
        if (strideY < width)
        {
            Debug.LogError($"[AprilTagColor32SpanProvider] Invalid lumaStride: {strideY} < width:{width}");
            return;
        }
        int needed = strideY * height;
        if (yPlane.Length < needed)
        {
            Debug.LogError($"[AprilTagColor32SpanProvider] Y plane too short: {yPlane.Length} < {needed} (strideY*height).");
            return;
        }

        EnsureBuffer(width, height);

        // 统一用“按行 Job”，同时支持紧密与对齐、翻转与范围扩展
        var param = new ExpandYToRgbaStridedJob.Params
        {
            Width = width,
            Height = height,
            StrideY = strideY,
            FlipY = FlipY ? 1 : 0,
            ExpandRange = ExpandLimitedRange ? 1 : 0
        };

        using var yTemp = SpanToTempNativeArray(yPlane); // 不产生托管数组
        var job = new ExpandYToRgbaStridedJob { P = param, Y = yTemp, RGBA = _rgba };

        try
        {
            job.Schedule(height, 1).Complete();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AprilTagColor32SpanProvider] Burst job failed. Fallback scalar. {ex.GetType().Name}: {ex.Message}");
            ExpandYToRgbaScalar(yPlane, width, height, strideY, FlipY, ExpandLimitedRange);
        }

        _hasFrame = true;
    }

    /// <summary> 取得最新帧的只读 Color32 视图；在下一次 Update 调用前有效。 </summary>
    public ReadOnlySpan<Color32> GetLatestColor32Span(out int width, out int height)
    {
        if (!_hasFrame || !_rgba.IsCreated)
        {
            width = height = 0;
            return ReadOnlySpan<Color32>.Empty;
        }

        width = _width; height = _height;
        unsafe
        {
            void* ptr = _rgba.GetUnsafeReadOnlyPtr();
            return new ReadOnlySpan<Color32>(ptr, _rgba.Length);
        }
    }

    /// <summary>
    /// 【调试可视化】把内部 RGBA 缓冲写入一张 Texture2D(RGBA32) 并返回（主线程调用）。
    /// 可直接赋给 RawImage.texture；若当前没有有效帧，返回 null。
    /// </summary>
    public Texture2D GetLatestTexture2D()
    {
        if (!_hasFrame || !_rgba.IsCreated) return null;

        // 尺寸匹配则复用，不匹配则 Reinitialize 或重建
        if (_debugTex == null ||
            _debugTex.width != _width ||
            _debugTex.height != _height ||
            _debugTex.format != TextureFormat.RGBA32)
        {
            if (_debugTex != null)
            {
                #if UNITY_2020_2_OR_NEWER
                if (_debugTex.Reinitialize(_width, _height))
                {
                    _debugTex.Apply(false, false);
                }
                else
                {
                    UnityEngine.Object.Destroy(_debugTex);
                    _debugTex = null;
                }
                #else
                UnityEngine.Object.Destroy(_debugTex);
                _debugTex = null;
                #endif
            }
            if (_debugTex == null)
            {
                _debugTex = new Texture2D(_width, _height, TextureFormat.RGBA32, false, true);
                _debugTex.wrapMode = TextureWrapMode.Clamp;
                _debugTex.filterMode = FilterMode.Bilinear;
            }
        }

        _debugTex.SetPixelData(_rgba, 0);
        _debugTex.Apply(false, false);
        return _debugTex;
    }

    public void Dispose()
    {
        if (_rgba.IsCreated) _rgba.Dispose();
        _rgba = default;
        _hasFrame = false;
        _width = _height = 0;

        if (_debugTex != null)
        {
            UnityEngine.Object.Destroy(_debugTex);
            _debugTex = null;
        }
    }

    // ---------- 内部实现 ----------

    private void EnsureBuffer(int w, int h)
    {
        if (!_rgba.IsCreated || _width != w || _height != h)
        {
            if (_rgba.IsCreated) _rgba.Dispose();
            _rgba = new NativeArray<Color32>(w * h, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _width = w; _height = h;
            _hasFrame = false;
        }
    }

    // 把 ReadOnlySpan<byte> 拷到 TempJob NativeArray（无中间托管数组）
    private static TempNativeArray<byte> SpanToTempNativeArray(ReadOnlySpan<byte> span)
    {
        var temp = new TempNativeArray<byte>(span.Length);
        unsafe
        {
            void* dst = temp.Array.GetUnsafePtr();
            fixed (byte* src = span)
            {
                UnsafeUtility.MemCpy(dst, src, span.Length);
            }
        }
        return temp;
    }

    private readonly struct TempNativeArray<T> : IDisposable where T : struct
    {
        public readonly NativeArray<T> Array;
        public TempNativeArray(int length)
        {
            Array = new NativeArray<T>(length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        }
        public void Dispose()
        {
            if (Array.IsCreated) Array.Dispose();
        }
        public static implicit operator NativeArray<T>(TempNativeArray<T> t) => t.Array;
    }

    // ===== Job：统一处理 有/无对齐 + 垂直翻转 + 可选范围扩展 =====
    #if BURST_PRESENT
    [BurstCompile]
    #endif
    private struct ExpandYToRgbaStridedJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Y;
        [NativeDisableParallelForRestriction]
        public NativeArray<Color32> RGBA;

        [StructLayout(LayoutKind.Sequential)]
        public struct Params
        {
            public int Width;
            public int Height;
            public int StrideY;
            public int FlipY;        // 0/1
            public int ExpandRange;  // 0/1
        }
        [ReadOnly] public Params P;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Expand(byte y)
        {
            // 把 16..235 映射到 0..255；否则原样返回
            int v = y - 16;
            if (v < 0) v = 0;
            // 219 -> 255 的缩放，四舍五入
            int x = (v * 255 + 109) / 219;
            if (x < 0) x = 0; else if (x > 255) x = 255;
            return (byte)x;
        }

        public void Execute(int row)
        {
            int w = P.Width;
            int stride = P.StrideY;

            int srcRowStart = row * stride;
            int dstRowIndex = (P.FlipY != 0) ? (P.Height - 1 - row) : row;
            int dstRowStart = dstRowIndex * w;

            if (P.ExpandRange != 0)
            {
                for (int x = 0; x < w; x++)
                {
                    byte y = Expand(Y[srcRowStart + x]);
                    RGBA[dstRowStart + x] = new Color32(y, y, y, 255);
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    byte y = Y[srcRowStart + x];
                    RGBA[dstRowStart + x] = new Color32(y, y, y, 255);
                }
            }
        }
    }

    // ===== 标量回退（无 Burst/无 Job 时也能跑） =====
    private void ExpandYToRgbaScalar(ReadOnlySpan<byte> yPlane, int width, int height, int strideY, bool flipY, bool expandRange)
    {
        for (int r = 0; r < height; r++)
        {
            int src = r * strideY;
            int dstRowIndex = flipY ? (height - 1 - r) : r;
            int dst = dstRowIndex * width;

            if (expandRange)
            {
                for (int c = 0; c < width; c++, src++, dst++)
                {
                    byte y = yPlane[src];
                    int v = y - 16; if (v < 0) v = 0;
                    int x = (v * 255 + 109) / 219; if (x < 0) x = 0; else if (x > 255) x = 255;
                    byte yy = (byte)x;
                    _rgba[dst] = new Color32(yy, yy, yy, 255);
                }
            }
            else
            {
                for (int c = 0; c < width; c++, src++, dst++)
                {
                    byte y = yPlane[src];
                    _rgba[dst] = new Color32(y, y, y, 255);
                }
            }
        }
    }
}
