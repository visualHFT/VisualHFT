using System;

namespace VisualHFT.Model
{
    public partial class BaseStudyModel
    {
        private DateTime _timestamp;
        private decimal _value;
        private string _format;
        private string _valueColor = null;
        private decimal _marketMidPrice;
        private bool _hasData = false;


        /// <summary>
        /// Optional custom formatter. When set, UI uses this instead of Format string.
        /// </summary>
        public Func<decimal, string> CustomFormatter { get; set; }

        public BaseStudyModel()
        {
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set => _timestamp = value;
        }

        public decimal Value
        {
            get => _value;
            set
            {
                _value = value;
                _hasData = true;
            }
        }

        /// <summary>
        /// Standard .NET format string (e.g., "n1", "F2").
        /// Ignored if CustomFormatter is set.
        /// </summary>
        public string Format
        {
            get => _format;
            set => _format = value;
        }

        /// <summary>
        /// Indicates whether real data has been received.
        /// False = waiting for data (".")
        /// True = has valid data or had data but now stale ("...")
        /// </summary>
        public bool HasData => _hasData;

        /// <summary>
        /// Indicates an error state ("Err").
        /// When true, UI should display error indicator.
        /// </summary>
        public bool HasError { get; set; }

        /// <summary>
        /// Indicates stale data state ("...").
        /// When true, data was received but is now stale.
        /// </summary>
        public bool IsStale { get; set; }

        public string ValueColor
        {
            get => _valueColor;
            set => _valueColor = value;
        }

        public decimal MarketMidPrice
        {
            get => _marketMidPrice;
            set => _marketMidPrice = value;
        }

        public string Tooltip { get; set; }
        public string Tag { get; set; }
        public bool IsIndependentMetric { get; set; }
        public bool AddItemSkippingAggregation { get; set; }

        public void copyFrom(BaseStudyModel e)
        {
            _timestamp = e._timestamp;
            _value = e._value;
            _format = e._format;
            _hasData = e._hasData;
            CustomFormatter = e.CustomFormatter;
            _valueColor = e._valueColor;
            _marketMidPrice = e._marketMidPrice;
            HasError = e.HasError;
            IsStale = e.IsStale;
            Tooltip = e.Tooltip;
            Tag = e.Tag;
            IsIndependentMetric = e.IsIndependentMetric;
            AddItemSkippingAggregation = e.AddItemSkippingAggregation;
        }

        public void Reset()
        {
            _timestamp = DateTime.MinValue;
            _value = 0;
            _format = null;
            _hasData = false;
            CustomFormatter = null;
            _valueColor = null;
            _marketMidPrice = 0;
            HasError = false;
            IsStale = false;
            Tooltip = null;
            Tag = null;
            IsIndependentMetric = false;
            AddItemSkippingAggregation = false;
        }
    }
}