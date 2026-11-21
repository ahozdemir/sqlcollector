using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlDataCollector.Privacy;

public enum MaskMethod : byte { Hash = 1, Partial = 2, Email = 3, Redact = 4, Null = 5 }

/// <summary>Where a rule applies: only in exported files, or already when collecting (the DWH holds masked values).</summary>
public enum MaskTarget : byte { Exports = 1, ExtractAndExports = 2 }

/// <summary>What the export guard does with likely personal data in a column that no rule masks.</summary>
public enum PrivacyGuardMode : byte { Off = 0, Warn = 1, Block = 2 }

/// <summary>etl.MaskingRule: which columns are masked how.</summary>
public sealed class MaskingRule
{
    public int MaskingRuleId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Column name patterns, ';' separated; * and ? wildcards, case-insensitive (e.g. "*Email*;*Mail").</summary>
    public string ColumnPattern { get; set; } = "";
    /// <summary>Object pattern "Database.Schema.Table" ("*" = everywhere; e.g. "ERP.dbo.*").</summary>
    public string ScopePattern { get; set; } = "*";
    public MaskMethod Method { get; set; } = MaskMethod.Hash;
    /// <summary>Partial: number of trailing characters left visible.</summary>
    public int KeepLast { get; set; } = 4;
    public MaskTarget AppliesTo { get; set; } = MaskTarget.Exports;
    public bool IsActive { get; set; } = true;
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }

    public string Describe() => Method switch
    {
        MaskMethod.Partial => $"Partial (last {KeepLast})",
        _ => Method.ToString()
    };
}

/// <summary>Case-insensitive * / ? wildcard matching; several patterns separated by ';'.</summary>
public static class Wildcard
{
    public static bool IsMatch(string patterns, string text) =>
        patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => Regex.IsMatch(text, "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
}

/// <summary>The masking functions. Input is the value as text; NULL stays NULL.</summary>
public static class Masker
{
    public const string Redacted = "***";

    public static string? Apply(MaskMethod method, int keepLast, string value, byte[]? hashKey) => method switch
    {
        MaskMethod.Hash => Hash(value, hashKey ?? throw new InvalidOperationException(
            "Hash masking needs Secrets:Key (the same in every process); it is the secret that makes the hash irreversible.")),
        MaskMethod.Partial => Partial(value, keepLast),
        MaskMethod.Email => Email(value),
        MaskMethod.Redact => Redacted,
        MaskMethod.Null => null,
        _ => Redacted
    };

    /// <summary>HMAC-SHA256 with a secret key, lower-case hex (64 chars): deterministic (joinable), not reversible without the key.</summary>
    public static string Hash(string value, byte[] key) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Keeps the last N characters, the others become '*' (length is kept; short values are fully masked).</summary>
    public static string Partial(string value, int keepLast)
    {
        keepLast = Math.Max(0, keepLast);
        if (value.Length <= keepLast) return new string('*', value.Length);
        return new string('*', value.Length - keepLast) + value[^keepLast..];
    }

    /// <summary>a***@domain.com; a value without '@' is redacted.</summary>
    public static string Email(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0) return Redacted;
        return value[0] + "***" + value[at..];
    }

    /// <summary>Text form of a value for masking (invariant, same as CSV output for common types).</summary>
    public static string ToText(object value) => value switch
    {
        string s => s,
        DateTime d => Export.CsvValueFormatter.FormatDateTime(d),
        DateTimeOffset o => Export.CsvValueFormatter.FormatDateTimeOffset(o),
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}

/// <summary>A resolved mask for one column.</summary>
public sealed record ColumnMask(string Column, MaskingRule Rule, byte[]? HashKey)
{
    public string? Apply(string value) => Masker.Apply(Rule.Method, Rule.KeepLast, value, HashKey);
    public string Describe() => $"{Column}: {Rule.Describe()} ({Rule.Name})";
}

/// <summary>Active rules plus the hash key and the guard mode, as one snapshot.</summary>
public sealed class MaskingPolicy(IReadOnlyList<MaskingRule> rules, byte[]? hashKey, PrivacyGuardMode guard)
{
    public static readonly MaskingPolicy None = new([], null, PrivacyGuardMode.Off);

    public IReadOnlyList<MaskingRule> Rules { get; } = rules.Where(r => r.IsActive)
        // object-specific rules win over "everywhere" rules; then the order they were created in
        .OrderBy(r => r.ScopePattern.Trim() == "*" ? 1 : 0).ThenBy(r => r.MaskingRuleId).ToList();
    public PrivacyGuardMode Guard { get; } = guard;
    public byte[]? HashKey { get; } = hashKey;

    /// <summary>The first matching rule for a column of any of the given objects ("Database.Schema.Table").</summary>
    public MaskingRule? Find(string column, IEnumerable<string> objects, MaskTarget target)
    {
        var objs = objects.ToList();
        return Rules.FirstOrDefault(r => r.AppliesTo == target && Wildcard.IsMatch(r.ColumnPattern, column)
            && objs.Any(o => Wildcard.IsMatch(r.ScopePattern, o)));
    }

    /// <summary>Masks for an export: rules for exported files (rules applied at collection already hold masked values in the DWH).</summary>
    public ColumnMask?[] ForExport(string objectName, IReadOnlyList<string> columns) =>
        columns.Select(c => Find(c, [objectName], MaskTarget.Exports) is { } r ? new ColumnMask(c, r, HashKey) : null).ToArray();

    /// <summary>Masks applied while collecting: matched against the source object and the target table.</summary>
    public ColumnMask?[] ForExtract(IReadOnlyList<string> objects, IReadOnlyList<string> columns) =>
        columns.Select(c => Find(c, objects, MaskTarget.ExtractAndExports) is { } r ? new ColumnMask(c, r, HashKey) : null).ToArray();
}

public enum PiiKind { Email, NationalId, Iban, CardNumber, Phone }

/// <summary>Recognises common personal data in text values (Turkey-centric: TCKN, TR IBAN, +90 phone numbers).</summary>
public static partial class PiiDetector
{
    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanRegex();

    public static string Describe(PiiKind k) => k switch
    {
        PiiKind.Email => "e-mail address",
        PiiKind.NationalId => "national ID (TCKN)",
        PiiKind.Iban => "IBAN",
        PiiKind.CardNumber => "payment card number",
        PiiKind.Phone => "phone number",
        _ => k.ToString()
    };

    /// <summary>Suggested masking for a detected kind.</summary>
    public static (MaskMethod Method, int KeepLast) Suggest(PiiKind k) => k switch
    {
        PiiKind.Email => (MaskMethod.Email, 0),
        PiiKind.NationalId => (MaskMethod.Hash, 0),
        PiiKind.CardNumber or PiiKind.Iban => (MaskMethod.Partial, 4),
        PiiKind.Phone => (MaskMethod.Partial, 2),
        _ => (MaskMethod.Hash, 0)
    };

    public static PiiKind? Detect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return null;
        var v = value.Trim();
        if (v.Contains('@')) return EmailRegex().IsMatch(v) ? PiiKind.Email : null;

        var compact = v.Replace(" ", "").Replace("-", "");
        if (IbanRegex().IsMatch(compact.ToUpperInvariant()) && IbanValid(compact.ToUpperInvariant())) return PiiKind.Iban;
        if (compact.Length == 11 && compact.All(char.IsAsciiDigit) && TcknValid(compact)) return PiiKind.NationalId;
        if (compact.Length is >= 13 and <= 19 && compact.All(char.IsAsciiDigit) && LuhnValid(compact)) return PiiKind.CardNumber;
        if (IsPhone(v)) return PiiKind.Phone;
        return null;
    }

    /// <summary>TCKN: 11 digits, first not 0, digit 10 and 11 are checksums.</summary>
    public static bool TcknValid(string s)
    {
        if (s.Length != 11 || s[0] == '0' || !s.All(char.IsAsciiDigit)) return false;
        int[] d = s.Select(c => c - '0').ToArray();
        int odd = d[0] + d[2] + d[4] + d[6] + d[8], even = d[1] + d[3] + d[5] + d[7];
        int d10 = ((odd * 7 - even) % 10 + 10) % 10;
        int d11 = d.Take(10).Sum() % 10;
        return d[9] == d10 && d[10] == d11;
    }

    public static bool LuhnValid(string s)
    {
        int sum = 0; bool dbl = false;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            int n = s[i] - '0';
            if (dbl) { n *= 2; if (n > 9) n -= 9; }
            sum += n; dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>ISO 13616 mod-97 check.</summary>
    public static bool IbanValid(string s)
    {
        var rearranged = s[4..] + s[..4];
        int mod = 0;
        foreach (var ch in rearranged)
        {
            var part = char.IsAsciiDigit(ch) ? (ch - '0').ToString(CultureInfo.InvariantCulture) : (ch - 'A' + 10).ToString(CultureInfo.InvariantCulture);
            foreach (var p in part) mod = (mod * 10 + (p - '0')) % 97;
        }
        return mod == 1;
    }

    /// <summary>Turkish mobile/landline formats: +90 5xx..., 0 5xx..., 5xx xxx xx xx (10 digits after the trunk prefix).</summary>
    private static bool IsPhone(string v)
    {
        if (v.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')' or '+'))) return false;
        var digits = new string(v.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("90", StringComparison.Ordinal) && digits.Length == 12) digits = digits[2..];
        else if (digits.StartsWith('0') && digits.Length == 11) digits = digits[1..];
        return digits.Length == 10 && digits[0] is '2' or '3' or '4' or '5' && (v.Contains(' ') || v.Contains('-') || v.Contains('(') || v.StartsWith('+') || digits[0] == '5');
    }

    /// <summary>
    /// The kind most values of a column match (at least half of the non-empty sample, minimum 3 values), or null.
    /// Numeric columns are only checked for national IDs and card numbers (a 10-digit number is not a phone number).
    /// </summary>
    public static (PiiKind Kind, double Ratio)? Classify(IEnumerable<string?> sample, bool textColumn = true)
    {
        var values = sample.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (values.Count < 3) return null;
        var best = values.Select(Detect).Where(k => k is not null && (textColumn || k is PiiKind.NationalId or PiiKind.CardNumber)).GroupBy(k => k!.Value)
            .Select(g => (Kind: g.Key, Count: g.Count())).OrderByDescending(x => x.Count).FirstOrDefault();
        if (best.Count == 0) return null;
        var ratio = (double)best.Count / values.Count;
        return ratio >= 0.5 ? (best.Kind, ratio) : null;
    }
}

/// <summary>Collects the first values of each unmasked column while a file is written, for the export guard.</summary>
public sealed class PiiSampler(IReadOnlyList<string> columns, IReadOnlyList<bool> skip, IReadOnlyList<bool> isText, int maxRows = 500)
{
    private readonly List<string?>[] _values = columns.Select(_ => new List<string?>()).ToArray();
    private int _rows;

    public bool Wants => _rows < maxRows;

    public void Add(int column, string? value)
    {
        if (!skip[column] && _rows < maxRows) _values[column].Add(value);
    }

    public void EndRow() => _rows++;

    public IReadOnlyList<(string Column, PiiKind Kind, double Ratio)> Findings() =>
        columns.Select((c, i) => (c, skip[i] ? null : PiiDetector.Classify(_values[i], isText[i])))
            .Where(x => x.Item2 is not null).Select(x => (x.c, x.Item2!.Value.Kind, x.Item2.Value.Ratio)).ToList();
}
