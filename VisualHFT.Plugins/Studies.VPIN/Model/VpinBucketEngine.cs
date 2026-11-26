using System;

namespace VisualHFT.Studies.VPIN.Model
{
    /// <summary>
    /// Computes VPIN over volume buckets built from a public trade tape, in the corrected form.
    ///
    /// THE PUBLISHED CONSTRUCTION
    /// Easley, Lopez de Prado and O'Hara (2012) define the metric as the mean absolute volume
    /// imbalance across the last n completed buckets of equal traded volume V:
    ///
    ///     VPIN = (1/n) * SUM |V_buy_i - V_sell_i| / V
    ///
    /// The volume clock is what makes the metric a real-time one: a bucket closes when volume
    /// reaches V, not when a wall-clock interval expires, so the reading advances with activity
    /// rather than with time. This engine keeps that construction, including splitting a print
    /// that straddles a bucket boundary and carrying the remainder into the next bucket.
    ///
    /// WHERE IT DEPARTS FROM THE PAPER, AND WHY
    /// The paper classifies a bucket's volume in bulk, splitting it between buy and sell from the
    /// bucket's price change scaled by the volatility of price changes, and never looks at an
    /// individual print's side. Andersen and Bondarenko (2015) showed that this classifier is a
    /// function of volatility, so a metric built on it tracks volatility mechanically; Chakrabarty,
    /// Pascual and Shkilko (2015) found the tick rule produces more accurate estimates of the same
    /// quantity. This engine therefore classifies with the tick rule: a print above the previous
    /// print is a buy, below is a sell, and an unchanged price repeats the previous side.
    ///
    /// A level shift follows from that, and callers must state it. The bulk method splits a bucket's
    /// volume fractionally between the two sides, while this assigns each whole print to one of them,
    /// which can only produce buckets at least as one-sided. Expect this to read higher than a
    /// bulk-classified figure on the same tape - expect, not measure: no such comparison has been run,
    /// and the product ships no bulk classifier to compare against. The legacy form is not one either;
    /// it also assigns whole prints, differing only in how it picks the side. Treat published absolute
    /// thresholds as untransferable and compare ranks and percentiles instead.
    ///
    /// The exchange's own aggressor flag is deliberately not used. The connectors that supply it do
    /// not agree on what it means, so a tile built on it would read backwards on some venues. The
    /// tick rule needs nothing but the print stream.
    ///
    /// WHY BUCKET SIZE DECIDES THE NUMBER
    /// A bucket holding B equally sized prints can only take the imbalance values 0, 2/B, 4/B and so
    /// on, and on coin-flip sides its expected value is 1.000 at B = 1, 0.500 at B = 2 and 0.375 at
    /// B = 4, falling toward zero only as B grows. A bucket small enough to hold one print therefore
    /// reads 1 forever on random flow, by arithmetic and not by measurement. The engine samples the
    /// first prints it classifies, takes their volume-weighted average size (the sum of squared sizes
    /// divided by the sum of sizes), and refuses to publish while the configured bucket volume is below
    /// <see cref="MinimumPrintsPerBucket"/> times that size. Weighting by volume measures the size of trade
    /// a unit of volume typically arrives in, so a few large prints cannot fill a bucket on their own; with
    /// equal sizes it is the plain size.
    ///
    /// AUTOMATIC BUCKET VOLUME
    /// <see cref="CreateAutoSized"/> sets the bucket volume to exactly that floor: it keeps two running totals over the
    /// sampled prints, takes V from the same statistic, and starts the first bucket with the next print. The sampled
    /// prints size the bucket and are not bucketed. V and the floor are one stored value, and V stays fixed for the
    /// life of the engine.
    ///
    /// WARM-UP
    /// The paper defines the metric only over a full window, so nothing is publishable until n
    /// buckets have closed. At the paper's own sizing that is roughly one trading day of volume.
    /// <see cref="WarmUpVolumeRequired"/> is that cost expressed as volume; dividing it by the
    /// instrument's volume rate gives it in wall time.
    ///
    /// THREADING
    /// This class is not thread-safe and does not try to be. It is driven from the trade callback
    /// alone, under the study's own lock. It is deliberately private to this plugin and shared with
    /// nothing outside it.
    /// </summary>
    internal sealed class VpinBucketEngine
    {
        /// <summary>
        /// Prints per bucket the configured bucket volume must be able to hold before a value is
        /// published. This is the same target already used to size this metric's buckets for file
        /// replay, kept identical so the two paths do not disagree about what counts as a usable
        /// bucket. Twenty is far enough above the saturation point that the expected imbalance on
        /// uninformative flow is a small number rather than 1.
        /// </summary>
        public const int DefaultMinimumPrintsPerBucket = 20;

        /// <summary>
        /// Prints sampled before the volume-weighted print size, and with it the bucket volume floor, is
        /// settled. The sample is two running totals, so the sizing allocates nothing and costs nothing per
        /// print after the sample closes.
        /// </summary>
        public const int DefaultPrintSizeSampleCount = 10_000;

        private decimal _bucketVolumeSize;
        private readonly int _minimumPrintsPerBucket;
        private readonly int _printSizeSampleCount;

        private readonly decimal[] _bucketImbalances;
        private int _bufferIndex;
        private int _bufferCount;
        private decimal _rollingSum;
        private long _completedBucketCount;

        private decimal _currentBuyVolume;
        private decimal _currentSellVolume;
        private decimal _currentBucketVolume;

        private decimal _previousPrice;
        private bool _hasPreviousPrice;
        private bool _lastSideIsBuy;
        private bool _hasLastSide;

        // The sample is two running totals in double: a decimal sum of squares overflows once one size passes about 2.8e12.
        private double _sampleSumOfSizes;
        private double _sampleSumOfSquaredSizes;
        private int _sampledCount;
        private decimal _volumeWeightedPrintSize;
        private decimal _requiredBucketVolumeSize;
        private bool _isFloorResolved;
        private readonly bool _isAutoSized;

        /// <param name="bucketVolumeSize">Traded volume V that closes a bucket. Must be positive.</param>
        /// <param name="windowSize">Number of completed buckets n averaged into the value. Must be positive.</param>
        /// <param name="minimumPrintsPerBucket">
        /// Prints the bucket volume must be able to hold, measured against the volume-weighted print size.
        /// Zero switches the floor off, which is useful when the caller has already sized V from a
        /// known volume profile and wants no sampling delay.
        /// </param>
        /// <param name="printSizeSampleCount">Prints sampled to settle the volume-weighted print size.</param>
        public VpinBucketEngine(
            decimal bucketVolumeSize,
            int windowSize,
            int minimumPrintsPerBucket = DefaultMinimumPrintsPerBucket,
            int printSizeSampleCount = DefaultPrintSizeSampleCount)
        {
            if (bucketVolumeSize <= 0m)
                throw new ArgumentOutOfRangeException(nameof(bucketVolumeSize), "Bucket volume size must be positive.");
            if (windowSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");
            if (minimumPrintsPerBucket < 0)
                throw new ArgumentOutOfRangeException(nameof(minimumPrintsPerBucket), "Minimum prints per bucket cannot be negative.");
            if (minimumPrintsPerBucket > 0 && printSizeSampleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(printSizeSampleCount), "Print size sample count must be positive when the floor is enabled.");

            _bucketVolumeSize = bucketVolumeSize;
            _minimumPrintsPerBucket = minimumPrintsPerBucket;
            _printSizeSampleCount = printSizeSampleCount;
            _bucketImbalances = new decimal[windowSize];
            _isFloorResolved = minimumPrintsPerBucket == 0;
        }

        /// <summary>
        /// An engine that sizes its own bucket: V = <paramref name="printsPerBucket"/> times the volume-weighted average size
        /// of the first <paramref name="printSizeSampleCount"/> classified prints. Those prints size the bucket and are not
        /// bucketed; the first bucket starts with the next classified print.
        /// </summary>
        public static VpinBucketEngine CreateAutoSized(
            int windowSize,
            int printsPerBucket = DefaultMinimumPrintsPerBucket,
            int printSizeSampleCount = DefaultPrintSizeSampleCount)
        {
            if (windowSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");
            if (printsPerBucket <= 0)
                throw new ArgumentOutOfRangeException(nameof(printsPerBucket), "Prints per bucket must be positive.");
            if (printSizeSampleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(printSizeSampleCount), "Print size sample count must be positive.");
            return new VpinBucketEngine(windowSize, printsPerBucket, printSizeSampleCount);
        }

        private VpinBucketEngine(int windowSize, int printsPerBucket, int printSizeSampleCount)
        {
            _minimumPrintsPerBucket = printsPerBucket;
            _printSizeSampleCount = printSizeSampleCount;
            _bucketImbalances = new decimal[windowSize];
            _isAutoSized = true;
        }

        /// <summary>Traded volume that closes a bucket. Zero while an automatic bucket is still being sized.</summary>
        public decimal BucketVolumeSize => _bucketVolumeSize;

        /// <summary>True while an automatic bucket is still being sized; the prints sampled meanwhile are not bucketed.</summary>
        public bool IsSizingBucket => _isAutoSized && !_isFloorResolved;

        /// <summary>True while a bucket volume set by hand waits for the sample that checks it against the floor; nothing is publishable until then.</summary>
        public bool IsCheckingFloor => !_isAutoSized && !_isFloorResolved;

        /// <summary>Classified prints sampled so far toward the floor, up to <see cref="PrintSizeSampleCount"/>.</summary>
        public int SampledPrintCount => _isFloorResolved ? _printSizeSampleCount : _sampledCount;

        /// <summary>Prints sampled to settle the volume-weighted print size.</summary>
        public int PrintSizeSampleCount => _printSizeSampleCount;

        /// <summary>Completed buckets averaged into the value once the window is full.</summary>
        public int WindowSize => _bucketImbalances.Length;

        /// <summary>Prints per bucket the configured bucket volume must hold. Zero means no floor.</summary>
        public int MinimumPrintsPerBucket => _minimumPrintsPerBucket;

        /// <summary>Completed buckets so far, counted from the first print and never evicted.</summary>
        public long CompletedBucketCount => _completedBucketCount;

        /// <summary>Completed buckets currently inside the rolling window.</summary>
        public int BucketsInWindow => _bufferCount;

        /// <summary>True once the last <see cref="AddTrade"/> closed at least one bucket.</summary>
        public bool BucketJustClosed { get; private set; }

        /// <summary>True once n buckets have closed, which is the first instant the metric is defined.</summary>
        public bool IsWindowFull => _bufferCount == _bucketImbalances.Length;

        /// <summary>
        /// Volume that must trade before the first value can appear: one full window of buckets.
        /// Divide by the instrument's volume rate to express the warm-up in wall time.
        /// </summary>
        public decimal WarmUpVolumeRequired => _bucketImbalances.Length * _bucketVolumeSize;

        /// <summary>
        /// Volume-weighted average print size over the sample (the sum of squared sizes divided by the sum of
        /// sizes), or zero while the sample is still filling. Reported so the caller can tell the user what
        /// bucket volume the instrument actually calls for.
        /// </summary>
        public decimal VolumeWeightedPrintSize => _volumeWeightedPrintSize;

        /// <summary>
        /// Smallest bucket volume that clears the floor, or zero while the sample is still filling.
        /// </summary>
        public decimal RequiredBucketVolumeSize => _isFloorResolved ? _requiredBucketVolumeSize : 0m;

        /// <summary>
        /// True when the sample has settled and the configured bucket volume is too small to hold
        /// <see cref="MinimumPrintsPerBucket"/> prints. A bucket that small reports an artefact of
        /// its own size rather than a property of the flow, so no value is published while this holds.
        /// </summary>
        public bool IsBucketSizeBelowFloor => _minimumPrintsPerBucket > 0 && _isFloorResolved && _bucketVolumeSize < RequiredBucketVolumeSize;

        /// <summary>
        /// The window mean, defined only once <see cref="IsWindowFull"/>. Before that it is the mean
        /// of however many buckets have closed, which is NOT the published metric — read
        /// <see cref="HasPublishableValue"/> before showing it to anyone.
        /// </summary>
        public decimal Value => _bufferCount > 0 ? _rollingSum / _bufferCount : 0m;

        /// <summary>
        /// True when the value means what the tooltip says it means: a full window has closed and
        /// the bucket volume is large enough for the imbalance to describe the flow.
        /// </summary>
        public bool HasPublishableValue => IsWindowFull && _isFloorResolved && !IsBucketSizeBelowFloor;

        /// <summary>
        /// Feeds one public trade print. Prints that cannot be classified contribute no volume: the
        /// first print of a session has nothing to compare against and only sets the reference, and
        /// a second print at that same price has no earlier side to repeat.
        /// </summary>
        public void AddTrade(decimal price, decimal size)
        {
            BucketJustClosed = false;

            // A print with no size is not a print. It returns BEFORE the reference price is updated
            // below, deliberately: the tick rule must compare the next real print against the last
            // real one, so a zero-sized record must not become the thing the next print is measured
            // against. Moving this below the assignment silently changes classification.
            if (size <= 0m)
                return;

            bool classified = TryClassify(price, out bool isBuy);

            _previousPrice = price;
            _hasPreviousPrice = true;

            if (!classified)
                return;

            _lastSideIsBuy = isBuy;
            _hasLastSide = true;

            if (!_isFloorResolved)
            {
                SamplePrintSize(size);
                if (_isAutoSized)
                {
                    // The sampled prints size the bucket and are not bucketed: the first bucket starts with the next print.
                    // Every sampled size is positive (the check above drops the rest), so V is positive.
                    if (_isFloorResolved)
                        _bucketVolumeSize = _requiredBucketVolumeSize;
                    return;
                }
            }

            BucketPrint(size, isBuy);
        }

        private void BucketPrint(decimal size, bool isBuy)
        {
            if (isBuy)
                _currentBuyVolume += size;
            else
                _currentSellVolume += size;
            _currentBucketVolume += size;

            if (_currentBucketVolume < _bucketVolumeSize)
                return;

            // Close the bucket this print completed, trimming the overflow off the side that received
            // it and carrying that overflow into the next bucket, as the paper specifies.
            decimal overflow = _currentBucketVolume - _bucketVolumeSize;
            if (isBuy)
                _currentBuyVolume -= overflow;
            else
                _currentSellVolume -= overflow;
            _currentBucketVolume = _bucketVolumeSize;
            CloseBucket();

            // Whatever is left over belongs entirely to this one print, so every further whole bucket
            // it fills holds one side only and has an imbalance of exactly 1. That is arithmetic, and
            // it is counted rather than iterated: this runs inline on the connector's producer thread
            // under the study's lock, and a bucket volume far below the print size would otherwise spin
            // here for as many turns as the ratio between them, stalling every other trade subscriber.
            if (overflow >= _bucketVolumeSize)
            {
                decimal wholeBuckets = decimal.Floor(overflow / _bucketVolumeSize);
                overflow -= wholeBuckets * _bucketVolumeSize;
                CloseOneSidedBuckets(wholeBuckets);
            }

            _currentBuyVolume = isBuy ? overflow : 0m;
            _currentSellVolume = isBuy ? 0m : overflow;
            _currentBucketVolume = overflow;
        }

        /// <summary>
        /// The tick rule: up from the previous print is a buy, down is a sell, and an unchanged
        /// price repeats the side of the last classified print.
        /// </summary>
        private bool TryClassify(decimal price, out bool isBuy)
        {
            isBuy = false;
            if (!_hasPreviousPrice)
                return false;

            if (price > _previousPrice)
            {
                isBuy = true;
                return true;
            }
            if (price < _previousPrice)
            {
                isBuy = false;
                return true;
            }

            if (!_hasLastSide)
                return false;

            isBuy = _lastSideIsBuy;
            return true;
        }

        private void CloseBucket()
        {
            decimal imbalance = Math.Abs(_currentBuyVolume - _currentSellVolume) / _bucketVolumeSize;

            if (_bufferCount == _bucketImbalances.Length)
                _rollingSum -= _bucketImbalances[_bufferIndex];
            else
                _bufferCount++;

            _bucketImbalances[_bufferIndex] = imbalance;
            _rollingSum += imbalance;
            _bufferIndex = (_bufferIndex + 1) % _bucketImbalances.Length;

            _completedBucketCount++;
            BucketJustClosed = true;
        }

        /// <summary>
        /// Records <paramref name="count"/> consecutive buckets that each held one side only, and so
        /// each have an imbalance of exactly 1, without walking them one at a time. Only the last
        /// <see cref="WindowSize"/> of them can still be inside the window - anything before that would
        /// be overwritten by what follows - so at most that many slots are touched however large the
        /// count is.
        /// </summary>
        private void CloseOneSidedBuckets(decimal count)
        {
            if (count <= 0m)
                return;

            int slotsToWrite = count >= _bucketImbalances.Length
                ? _bucketImbalances.Length
                : (int)count;

            for (int i = 0; i < slotsToWrite; i++)
            {
                if (_bufferCount == _bucketImbalances.Length)
                    _rollingSum -= _bucketImbalances[_bufferIndex];
                else
                    _bufferCount++;

                _bucketImbalances[_bufferIndex] = 1m;
                _rollingSum += 1m;
                _bufferIndex = (_bufferIndex + 1) % _bucketImbalances.Length;
            }

            // The running total is only ever reported, never used in the calculation, so it saturates
            // rather than throwing on a count that no longer fits.
            decimal headroom = long.MaxValue - _completedBucketCount;
            _completedBucketCount += count >= headroom ? (long)headroom : (long)count;

            BucketJustClosed = true;
        }

        private void SamplePrintSize(decimal size)
        {
            double q = (double)size;
            _sampleSumOfSizes += q;
            _sampleSumOfSquaredSizes += q * q;
            if (++_sampledCount < _printSizeSampleCount)
                return;

            // The floor is stored once, so an automatic V set from it can never fall below it by rounding.
            _volumeWeightedPrintSize = VolumeWeightedSize(_sampleSumOfSizes, _sampleSumOfSquaredSizes);
            _requiredBucketVolumeSize = _minimumPrintsPerBucket * _volumeWeightedPrintSize;
            _isFloorResolved = true;
        }

        /// <summary>
        /// The volume-weighted average size, sum(q^2) / sum(q), from a sample's two running totals. The ratio is converted to
        /// decimal once. Zero when the sizes sum to zero.
        /// </summary>
        internal static decimal VolumeWeightedSize(double sumOfSizes, double sumOfSquaredSizes) =>
            sumOfSizes > 0d ? (decimal)(sumOfSquaredSizes / sumOfSizes) : 0m;
    }
}
