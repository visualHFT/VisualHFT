using System.Globalization;
using VisualHFT.Commons.Helpers;
using Xunit;

namespace VisualHFT.Commons.Tests;

/// <summary>
/// Exchange wire formats are invariant: a venue sends "64818.74" with a '.' decimal separator no
/// matter where the machine reading it is. .NET's culture-less <c>double.Parse(s)</c> resolves to
/// <see cref="CultureInfo.CurrentCulture"/>, which on this desktop app is the OPERATOR'S OS LOCALE —
/// nothing in startup overrides it. On any comma-decimal locale (de-DE, fr-FR, es-AR, ru-RU, pt-BR …)
/// a culture-less parse of a venue price therefore either throws or, with TryParse, silently fails.
///
/// A throwing locale kills the connector at its first snapshot with
/// <c>'64818.74' was not in a correct format</c>. The silent variant is worse — a TryParse that
/// returns false and <c>continue</c>s drops every book level, leaving a CONNECTED connector with a
/// permanently empty book and no error anywhere.
///
/// These tests pin the contract on the shared helper. The companion
/// <see cref="ConnectorWireParseCultureGuardTests"/> pins that the connectors actually use it —
/// a correct helper nothing calls fixes nothing.
/// </summary>
public class WireNumberCultureTests
{
    // Comma-decimal locales. de-DE also uses '.' as the GROUP separator, which is
    // what turns a silent misparse into a wrong number rather than a clean failure.
    public static TheoryData<string> CommaDecimalCultures => new()
    {
        "de-DE", "fr-FR", "es-AR", "ru-RU", "pt-BR", "it-IT", "nl-NL", "tr-TR",
    };

    [Theory]
    [MemberData(nameof(CommaDecimalCultures))]
    public void ParseDouble_ReadsAVenuePrice_TheSameOnEveryLocale(string culture)
    {
        using var scope = new CultureScope(culture);

        Assert.Equal(64818.74d, WireNumber.ParseDouble("64818.74"));
    }

    [Theory]
    [MemberData(nameof(CommaDecimalCultures))]
    public void TryParseDouble_ReadsAVenueSize_TheSameOnEveryLocale(string culture)
    {
        using var scope = new CultureScope(culture);

        Assert.True(WireNumber.TryParseDouble("0.00123456", out double size),
            "A venue size string must parse on a comma-decimal machine. A TryParse that returns false "
            + "here is the silent variant of this defect: the caller skips the level and the book stays empty.");
        Assert.Equal(0.00123456d, size);
    }

    [Theory]
    [MemberData(nameof(CommaDecimalCultures))]
    public void TryParseDecimal_ReadsAVenueQuantity_TheSameOnEveryLocale(string culture)
    {
        using var scope = new CultureScope(culture);

        Assert.True(WireNumber.TryParseDecimal("1.5", out decimal qty));
        Assert.Equal(1.5m, qty);
    }

    [Theory]
    [MemberData(nameof(CommaDecimalCultures))]
    public void TryParseInt_ReadsAVenueInteger_TheSameOnEveryLocale(string culture)
    {
        using var scope = new CultureScope(culture);

        Assert.True(WireNumber.TryParseInt("42", out int value));
        Assert.Equal(42, value);
    }

    [Theory]
    [MemberData(nameof(CommaDecimalCultures))]
    public void TryParseLong_ReadsAVenueTimestamp_TheSameOnEveryLocale(string culture)
    {
        using var scope = new CultureScope(culture);

        Assert.True(WireNumber.TryParseLong("1725408000123", out long value));
        Assert.Equal(1725408000123L, value);
    }

    [Fact]
    public void ParseDouble_StillWorksOnAnInvariantMachine()
    {
        // Positive control: the fix must not be a fix that only works on the broken locales.
        using var scope = new CultureScope("en-US");

        Assert.Equal(64818.74d, WireNumber.ParseDouble("64818.74"));
    }

    [Theory]
    [MemberData(nameof(CommaDecimalCultures))]
    public void TryParseDouble_RejectsAValueThatIsNotAWireNumber(string culture)
    {
        // A comma-decimal string is NOT a venue format; accepting it would mean the helper had merely
        // widened the parse rather than pinned it to the wire's own convention.
        using var scope = new CultureScope(culture);

        Assert.False(WireNumber.TryParseDouble("64818,74", out _));
        Assert.False(WireNumber.TryParseDouble("not-a-number", out _));
        Assert.False(WireNumber.TryParseDouble(null, out _));
        Assert.False(WireNumber.TryParseDouble(string.Empty, out _));
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    [InlineData("1e400")]
    public void ANonFiniteValue_IsNeverAdmittedToABook(string raw)
    {
        // Pinning to the invariant culture WIDENS what parses: the invariant PositiveInfinitySymbol is
        // literally "Infinity", so a value en-US used to reject now comes back as a real double. An
        // infinity or a NaN in a price or a size corrupts every metric computed from the book and never
        // surfaces as an error, so it must be rejected here rather than stored.
        Assert.False(WireNumber.TryParseDouble(raw, out _));
        Assert.Throws<FormatException>(() => WireNumber.ParseDouble(raw));
    }

    [Fact]
    public void ANegativeIntegerParses_EvenWhereTheLocaleUsesItsOwnMinusSign()
    {
        // ar-SA and fa-IR write the minus sign as a different codepoint, so the culture-less overload
        // fails on an ordinary wire integer. This is what the invariant culture buys for the integer
        // helpers -- the NumberStyles are the framework default; the culture is not.
        foreach (string locale in new[] { "ar-SA", "fa-IR" })
        {
            using var scope = new CultureScope(locale);

            Assert.True(WireNumber.TryParseLong("-1725408000123", out long value), locale);
            Assert.Equal(-1725408000123L, value);
        }
    }

    /// <summary>Sets the ambient culture for the duration of one test and restores it after.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previous;
        }
    }
}
