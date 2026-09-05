using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace VisualHFT.Commons.Tests;

/// <summary>
/// Linkage guard for the wire-number contract in <see cref="WireNumberCultureTests"/>.
///
/// A correct <c>WireNumber</c> helper fixes nothing on its own — the defect only disappears when the
/// connectors stop reading exchange numbers with the operator's OS locale. Measured on .NET 10, the
/// culture-less <c>double.Parse("64818.74")</c> returns <b>6481874</b> on de-DE, es-AR, pt-BR, it-IT,
/// nl-NL and tr-TR (silent, 100× wrong) and throws on fr-FR and ru-RU, which kills a connector at
/// its first snapshot.
///
/// This scan is mechanically decidable and it DOES fail: it found locale-dependent call sites in the
/// BitStamp, Gemini and Binance connectors when it was written.
/// <see cref="TheGuardItself_FlagsALocaleDependentParse"/> keeps it honest by running the same logic
/// over lines that must and must not be flagged.
///
/// Escape hatch: a parse of genuine OPERATOR-TYPED text should follow the operator's locale. Mark
/// such a line with a trailing <c>// locale-ok:</c> comment saying why.
///
/// KNOWN BLIND SPOTS, stated rather than left to be discovered:
/// <list type="bullet">
/// <item>Scope is <c>VisualHFT.Plugins/MarketConnectors.*</c> only — not Commons, not <c>Studies.*</c>,
/// not the WPF app, not TriggerEngine, and nothing on the WRITE side (a <c>ToString()</c> that emits
/// the operator's decimal separator into a payload another component parses).</item>
/// <item><c>VisualHFT.Commons/Extensions/StringExtender.cs</c> exposes culture-less
/// <c>ToInt/ToDouble/ToDecimal</c> string overloads. A connector reaching a wire value through
/// <c>s.ToDouble()</c> would not be caught here: the receiver's type is not decidable from the text.</item>
/// <item>Values already parsed by a venue SDK are outside this entirely — they never pass through a
/// string in our code.</item>
/// <item><c>Convert.ToDouble</c>/<c>ToDecimal</c>/<c>ToInt64</c> on a STRING are culture-sensitive, but
/// the argument's type is not decidable from source text and flagging them produced false positives on
/// numeric arguments. They are not matched; a fixture below pins that decision so it stays deliberate.</item>
/// </list>
/// </summary>
public class ConnectorWireParseCultureGuardTests
{
    // double.Parse( / decimal.TryParse( / int.Parse( ... on the framework types.
    private static readonly Regex CultureLessParse = new(
        @"\b(?:double|decimal|float|single|int|uint|long|ulong|short|ushort|byte|sbyte)\s*\.\s*(?:Try)?Parse\s*\(",
        RegexOptions.Compiled);

    // Tokens that prove a format provider was chosen deliberately. CurrentCulture is NOT one of them:
    // naming it explicitly is the same defect written out longhand, so it is flagged below instead.
    private static readonly string[] ProviderTokens =
    {
        "InvariantCulture", "NumberFormatInfo", "IFormatProvider", "WireNumber",
    };

    private static readonly Regex ExplicitCurrentCulture = new(
        @"\bCultureInfo\s*\.\s*CurrentCulture\b|\bCultureInfo\s*\.\s*CurrentUICulture\b", RegexOptions.Compiled);

    private const string OptOutMarker = "// locale-ok:";

    [Fact]
    public void NoConnectorParsesExchangeNumbersWithTheOperatorsLocale()
    {
        var offenders = new List<string>();

        foreach (string file in EnumerateConnectorSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsOffender(lines, i, out string reason))
                    continue;

                offenders.Add($"{Relative(file)}:{i + 1}: {lines[i].Trim()}   [{reason}]");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} connector call site(s) read an exchange-supplied number with the "
            + "operator's OS locale. Route them through VisualHFT.Commons.Helpers.WireNumber (or pass "
            + $"CultureInfo.InvariantCulture explicitly); mark a genuine operator-input parse with '{OptOutMarker} <reason>':\n  - "
            + string.Join("\n  - ", offenders));
    }

    [Fact]
    public void TheGuardItself_FlagsALocaleDependentParse()
    {
        // A check that cannot fail is not a check. These fixtures run through the same decision the
        // repository scan uses, so a change that quietly defangs it fails here first.
        AssertFlagged("var p = double.Parse(item[0]);");
        AssertFlagged("if (!double.TryParse(item[1], out double q)) continue;");
        AssertFlagged("if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out var v))");
        AssertFlagged("var p = double.Parse(x[0]); // CultureInfo is handled elsewhere, honest");

        AssertClean("var p = WireNumber.ParseDouble(item[0]);");
        AssertClean("var p = double.Parse(item[0], CultureInfo.InvariantCulture);");
        AssertClean("if (!double.TryParse(uiSpeed, NumberStyles.Float, CultureInfo.CurrentCulture, out var s)) // locale-ok: operator-typed");
        AssertClean("var total = prices.Sum();");
        AssertClean("// double.Parse(item[0]) used to live here");
        // Documented limit: Convert.ToX is culture-sensitive only for a STRING argument, and the
        // argument's type is not decidable from the text. Flagging it produced false positives on
        // Convert.ToInt64(someDouble), which people would then silence with a dishonest opt-out marker.
        AssertClean("{ \"exp\", Convert.ToInt64((DateTime.UtcNow - epoch).TotalSeconds) },");
    }

    private static void AssertFlagged(string line)
    {
        Assert.True(IsOffender(new[] { line }, 0, out _), $"The guard did NOT flag: {line}");
    }

    private static void AssertClean(string line)
    {
        Assert.False(IsOffender(new[] { line }, 0, out string reason), $"The guard wrongly flagged ({reason}): {line}");
    }

    private static bool IsOffender(string[] lines, int index, out string reason)
    {
        reason = string.Empty;
        string raw = lines[index];
        if (raw.Contains(OptOutMarker, StringComparison.Ordinal))
            return false;

        // Decide on CODE only. A comment naming CultureInfo must not exempt the statement beside it,
        // and a parse that only appears inside a comment is not a call site.
        string code = StripLineComment(raw);
        if (!CultureLessParse.IsMatch(code))
            return false;

        string statement = ReadStatement(lines, index);
        if (ExplicitCurrentCulture.IsMatch(statement))
        {
            reason = "explicitly uses the operator's culture";
            return true;
        }

        if (ProviderTokens.Any(t => statement.Contains(t, StringComparison.Ordinal)))
            return false;

        reason = "no format provider — resolves to CultureInfo.CurrentCulture";
        return true;
    }

    private static string StripLineComment(string line)
    {
        int marker = line.IndexOf("//", StringComparison.Ordinal);
        return marker < 0 ? line : line.Substring(0, marker);
    }

    private static string ReadStatement(string[] lines, int start)
    {
        var text = StripLineComment(lines[start]);
        for (int j = start + 1; j < lines.Length && j < start + 5; j++)
        {
            if (text.TrimEnd().EndsWith(";", StringComparison.Ordinal)
                || text.TrimEnd().EndsWith(")", StringComparison.Ordinal))
            {
                break;
            }
            text += " " + StripLineComment(lines[j]);
        }
        return text;
    }

    private static IEnumerable<string> EnumerateConnectorSources()
    {
        string connectorsRoot = Path.Combine(GetRepoRoot(), "VisualHFT.Plugins");
        Assert.True(Directory.Exists(connectorsRoot),
            $"Connector root not found at '{connectorsRoot}'. This guard scans source on disk; if the "
            + "layout moved, repoint it rather than deleting the check.");

        var dirs = Directory.EnumerateDirectories(connectorsRoot, "MarketConnectors.*").ToList();
        Assert.True(dirs.Count > 0, $"No MarketConnectors.* projects found under '{connectorsRoot}'.");

        foreach (string dir in dirs)
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return file;
            }
        }
    }

    private static string Relative(string file)
    {
        return Path.GetRelativePath(GetRepoRoot(), file).Replace('\\', '/');
    }

    private static string GetRepoRoot([CallerFilePath] string thisTestFilePath = "")
    {
        string testDir = Path.GetDirectoryName(thisTestFilePath)
            ?? throw new InvalidOperationException("CallerFilePath did not resolve.");
        // VisualHFT.Commons.Tests/ -> tests/Unit/ -> tests/ -> repo root.
        return Path.GetFullPath(Path.Combine(testDir, "..", "..", ".."));
    }
}
