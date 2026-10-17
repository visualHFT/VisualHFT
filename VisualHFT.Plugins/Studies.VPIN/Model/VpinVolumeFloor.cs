using System;
using System.Collections.Generic;

namespace VisualHFT.Studies.VPIN.Model
{
    /// <summary>
    /// Bucket volume must hold enough prints that a one-sided bucket is information, not arithmetic.
    /// Default V = 1 with SOLUSDT prints of size 1+ closes every bucket on a single aggressor,
    /// so VPIN is identically 1. Size V from the tape instead of trusting that factory default.
    /// </summary>
    public sealed class VpinVolumeFloor
    {
        public const int PrintsPerBucket = 20;
        public const int SampleCount = 40;

        private readonly List<decimal> _sizes = new(SampleCount);

        public bool IsReady { get; private set; }
        public decimal MedianPrint { get; private set; }

        public void Add(decimal size)
        {
            if (IsReady || size <= 0m)
                return;
            _sizes.Add(size);
            if (_sizes.Count < SampleCount)
                return;
            _sizes.Sort();
            MedianPrint = Median(_sizes);
            IsReady = true;
        }

        public decimal Recommend(decimal configured)
        {
            if (!IsReady)
                return configured > 0m ? configured : 0m;
            var floor = PrintsPerBucket * MedianPrint;
            if (floor <= 0m)
                return configured > 0m ? configured : 0m;
            return configured >= floor ? configured : floor;
        }

        public static decimal Median(IReadOnlyList<decimal> sorted)
        {
            if (sorted == null || sorted.Count == 0)
                return 0m;
            var middle = sorted.Count / 2;
            return sorted.Count % 2 != 0
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2m;
        }

        public static decimal Effective(decimal configured, decimal medianPrint)
        {
            var floor = PrintsPerBucket * medianPrint;
            if (floor <= 0m)
                return configured;
            return configured >= floor ? configured : floor;
        }
    }
}
