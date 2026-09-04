using System.Globalization;

namespace VisualHFT.Commons.Helpers
{
    /// <summary>
    /// Numeric parsing for values that arrive off an exchange wire.
    ///
    /// Exchange wire formats are invariant — a venue writes "64818.74" with a '.' decimal separator
    /// regardless of where the machine reading it sits. The framework's culture-less overloads
    /// (<c>double.Parse(s)</c>, <c>decimal.TryParse(s, out v)</c>) resolve to
    /// <see cref="CultureInfo.CurrentCulture"/>, which in this desktop app is the operator's OS locale:
    /// nothing in startup overrides it.
    ///
    /// Measured on .NET 10, parsing the venue price "64818.74" with the culture-less overload:
    /// <list type="bullet">
    /// <item>de-DE, es-AR, pt-BR, it-IT, nl-NL, tr-TR — returns <b>6481874</b>. No exception. Those
    /// locales use '.' as the GROUP separator, so a price silently comes back 100× too large and every
    /// metric derived from the book is quietly wrong.</item>
    /// <item>fr-FR, ru-RU — throws <c>FormatException: '64818.74' was not in a correct format</c>, so
    /// the connector dies at its first snapshot instead of producing a wrong book.</item>
    /// </list>
    /// The silent case is the common one, so "it only crashes on some machines" understates this
    /// badly.
    ///
    /// Two deliberate narrowings, both measured rather than assumed:
    /// <list type="bullet">
    /// <item>Thousands separators are NOT accepted. No venue sends them, and allowing them would make
    /// "64818,74" parse as sixty-four million under the invariant culture instead of being rejected as
    /// the malformed value it is.</item>
    /// <item>Non-finite results are rejected. Pinning to the invariant culture otherwise WIDENS the
    /// parse: the invariant <c>PositiveInfinitySymbol</c> is literally "Infinity", so a string en-US
    /// used to reject would come back as a real double. An infinity or a NaN in a price or a size
    /// corrupts everything computed from the book and never surfaces as an error. "1e400" overflows to
    /// infinity under every culture and is rejected here too.</item>
    /// </list>
    ///
    /// For the integer helpers the <see cref="NumberStyles"/> are the framework default; the invariant
    /// culture is the part that matters. Measured: ar-SA and fa-IR write the minus sign as a different
    /// codepoint, so the culture-less overload fails on an ordinary negative wire integer.
    ///
    /// Every numeric parse of exchange-supplied text goes through here.
    /// </summary>
    public static class WireNumber
    {
        private const NumberStyles RealStyles = NumberStyles.AllowLeadingWhite
                                                | NumberStyles.AllowTrailingWhite
                                                | NumberStyles.AllowLeadingSign
                                                | NumberStyles.AllowDecimalPoint
                                                | NumberStyles.AllowExponent;

        public static double ParseDouble(string value)
        {
            double result = double.Parse(value, RealStyles, CultureInfo.InvariantCulture);
            if (!double.IsFinite(result))
            {
                throw new FormatException(
                    $"The input string '{value}' parsed to a non-finite value, which is not a valid price or size.");
            }
            return result;
        }

        public static bool TryParseDouble(string value, out double result)
        {
            if (!double.TryParse(value, RealStyles, CultureInfo.InvariantCulture, out result))
                return false;

            if (double.IsFinite(result))
                return true;

            result = 0d;
            return false;
        }

        public static bool TryParseDecimal(string value, out decimal result)
        {
            return decimal.TryParse(value, RealStyles, CultureInfo.InvariantCulture, out result);
        }

        public static bool TryParseInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        public static bool TryParseLong(string value, out long result)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }
}
