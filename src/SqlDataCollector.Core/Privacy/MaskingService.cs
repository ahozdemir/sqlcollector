using System.Data.Common;
using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Privacy;

/// <summary>Loads the masking rules, the guard mode (etl.SystemSetting) and the hash key into a policy snapshot.</summary>
public sealed class MaskingService(MetadataRepository repo, SecretProtector secrets)
{
    public const string GuardSetting = "Privacy.ExportGuard";
    public const PrivacyGuardMode DefaultGuard = PrivacyGuardMode.Warn;

    public async Task<MaskingPolicy> GetPolicyAsync()
    {
        var rules = await repo.GetMaskingRulesAsync();
        return new MaskingPolicy(rules, secrets.DeriveKey("masking"), ParseGuard(await repo.GetSystemSettingsAsync()));
    }

    public async Task<PrivacyGuardMode> GetGuardAsync() => ParseGuard(await repo.GetSystemSettingsAsync());

    public Task SaveGuardAsync(PrivacyGuardMode mode, string user) =>
        repo.SaveSystemSettingsAsync(new Dictionary<string, string?> { [GuardSetting] = mode.ToString() }, user);

    private static PrivacyGuardMode ParseGuard(IReadOnlyDictionary<string, string?> settings) =>
        settings.TryGetValue(GuardSetting, out var v) && Enum.TryParse<PrivacyGuardMode>(v, true, out var m) ? m : DefaultGuard;
}

/// <summary>Target column definition of a column masked while collecting: text, sized for the masked value.</summary>
public sealed class MaskedDbColumn : DbColumn
{
    public MaskedDbColumn(DbColumn source, MaskMethod method)
    {
        ColumnName = source.ColumnName;
        ColumnOrdinal = source.ColumnOrdinal;
        AllowDBNull = true;
        DataType = typeof(string);
        DataTypeName = "nvarchar";
        var t = (source.DataTypeName ?? "").ToLowerInvariant();
        bool text = t is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";
        int size = source.ColumnSize is > 0 and <= 4000 ? source.ColumnSize.Value : -1;
        ColumnSize = method switch
        {
            MaskMethod.Hash => 64,
            MaskMethod.Redact when !text || size is > 0 and < 3 => 10,
            MaskMethod.Email when text && size > 0 => Math.Min(size + 3, 4000),
            _ when text => size,          // Partial keeps the length; (max) stays (max)
            _ => 100                      // numbers, dates, ... as text
        };
    }

    /// <summary>Source schema with masked columns replaced (Null keeps the original type: its values are just NULL).</summary>
    public static IReadOnlyList<DbColumn> Apply(IReadOnlyList<DbColumn> schema, IReadOnlyList<ColumnMask?> masks) =>
        schema.Select((c, i) => masks[i] is { } m && m.Rule.Method != MaskMethod.Null ? new MaskedDbColumn(c, m.Rule.Method) : c).ToList();
}
