using UnityEngine;

namespace InfinityTerrain.Utilities
{
    /// <summary>
    /// Helper functions for working with Compute Shaders.
    /// </summary>
    public static class ComputeShaderHelper
    {
        /// <summary>
        /// Set a long value in compute shader as two uint values (lo and hi).
        /// Preserves two's complement bit pattern so negative coords stay deterministic.
        /// </summary>
        public static void SetLongAsUInt2(ComputeShader cs, string loName, string hiName, long value)
        {
            ulong u = unchecked((ulong)value);
            uint lo = (uint)u;
            uint hi = (uint)(u >> 32);
            cs.SetInt(loName, unchecked((int)lo));
            cs.SetInt(hiName, unchecked((int)hi));
        }

        /// <summary>
        /// Clamp a long value to int range.
        /// </summary>
        public static int ClampLongToInt(long v)
        {
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        /// <summary>
        /// Floor division for long values.
        /// </summary>
        public static long FloorDiv(long a, int b)
        {
            if (b <= 0) return 0;
            if (a >= 0) return a / b;
            return -(((-a) + b - 1) / b);
        }
    }
}

