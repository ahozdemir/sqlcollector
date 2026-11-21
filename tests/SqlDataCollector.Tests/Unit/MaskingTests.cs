using SqlDataCollector.Privacy;

namespace SqlDataCollector.Tests.Unit;

public class MaskingTests
{
    private static readonly byte[] Key = new byte[32];

    [Theory]
    [InlineData(MaskMethod.Partial, 4, "TR330006100519786457841326", "**********************1326")]
    [InlineData(MaskMethod.Partial, 4, "123", "***")]
    [InlineData(MaskMethod.Email, 0, "ayse.yilmaz@corp.com", "a***@corp.com")]
    [InlineData(MaskMethod.Email, 0, "no-at-sign", "***")]
    [InlineData(MaskMethod.Redact, 0, "anything", "***")]
    [InlineData(MaskMethod.Null, 0, "anything", null)]
    public void Methods(MaskMethod method, int keep, string value, string? expected) =>
        Assert.Equal(expected, Masker.Apply(method, keep, value, Key));

    [Fact]
    public void Hash_IsDeterministic_KeyDependent_AndHex64()
    {
        var a = Masker.Apply(MaskMethod.Hash, 0, "10000000146", Key)!;
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, Masker.Apply(MaskMethod.Hash, 0, "10000000146", Key));
        var other = new byte[32]; other[0] = 1;
        Assert.NotEqual(a, Masker.Apply(MaskMethod.Hash, 0, "10000000146", other));
        Assert.Throws<InvalidOperationException>(() => Masker.Apply(MaskMethod.Hash, 0, "x", null));
    }

    [Theory]
    [InlineData("*Email*;*Mail", "CustomerEmail", true)]
    [InlineData("*Email*;*Mail", "eMail", true)]
    [InlineData("*Email*;*Mail", "MailingList", false)]
    [InlineData("Tc?n", "TCKN", true)]
    [InlineData("ERP.dbo.*", "ERP.dbo.Customers", true)]
    [InlineData("ERP.dbo.*", "CRM.dbo.Customers", false)]
    [InlineData("*", "anything.at.all", true)]
    public void WildcardMatching(string pattern, string text, bool expected) => Assert.Equal(expected, Wildcard.IsMatch(pattern, text));

    [Theory]
    [InlineData("ayse.yilmaz@corp.com", PiiKind.Email)]
    [InlineData("10000000146", PiiKind.NationalId)]
    [InlineData("TR33 0006 1005 1978 6457 8413 26", PiiKind.Iban)]
    [InlineData("4111 1111 1111 1111", PiiKind.CardNumber)]
    [InlineData("+90 532 123 45 67", PiiKind.Phone)]
    [InlineData("0532 123 45 67", PiiKind.Phone)]
    [InlineData("12345678901", null)]          // 11 digits, wrong TCKN checksum
    [InlineData("4111111111111112", null)]     // fails Luhn
    [InlineData("Istanbul", null)]
    [InlineData("2026-11-21", null)]
    public void Detect(string value, PiiKind? expected) => Assert.Equal(expected, PiiDetector.Detect(value));

    [Fact]
    public void Classify_NeedsAMajority_AndIgnoresPhonesInNumericColumns()
    {
        Assert.Equal(PiiKind.Email, PiiDetector.Classify(["a@b.com", "c@d.org", "x@y.net", null, "not mail"])?.Kind);
        Assert.Null(PiiDetector.Classify(["a@b.com", "x", "y", "z"]));                     // 25 %
        Assert.Null(PiiDetector.Classify(["a@b.com", "c@d.com"]));                          // too few values
        Assert.Null(PiiDetector.Classify(["5321234567", "5331234567", "5441234567"], textColumn: false));
        Assert.Equal(PiiKind.Phone, PiiDetector.Classify(["5321234567", "5331234567", "5441234567"])?.Kind);
    }

    [Fact]
    public void Policy_SpecificScopeWins_AndTargetsAreSeparate()
    {
        var everywhere = new MaskingRule { MaskingRuleId = 1, Name = "all mails", ColumnPattern = "*Email*", Method = MaskMethod.Redact };
        var erp = new MaskingRule { MaskingRuleId = 2, Name = "erp mails", ColumnPattern = "Email", ScopePattern = "ERP.*", Method = MaskMethod.Email };
        var collect = new MaskingRule { MaskingRuleId = 3, Name = "tckn", ColumnPattern = "Tckn", Method = MaskMethod.Hash, AppliesTo = MaskTarget.ExtractAndExports };
        var inactive = new MaskingRule { MaskingRuleId = 4, Name = "off", ColumnPattern = "Name", Method = MaskMethod.Null, IsActive = false };
        var policy = new MaskingPolicy([everywhere, erp, collect, inactive], Key, PrivacyGuardMode.Warn);

        var masks = policy.ForExport("ERP.dbo.Customers", ["Email", "Tckn", "Name"]);
        Assert.Equal("erp mails", masks[0]!.Rule.Name);
        Assert.Null(masks[1]);   // a collection rule: the DWH already holds masked values
        Assert.Null(masks[2]);   // inactive
        Assert.Equal("all mails", policy.ForExport("CRM.dbo.Contacts", ["Email"])[0]!.Rule.Name);

        var extract = policy.ForExtract(["ERP.dbo.Customers", "SdcDwh.erp.Customers"], ["Email", "Tckn"]);
        Assert.Null(extract[0]);
        Assert.Equal("tckn", extract[1]!.Rule.Name);
    }

    [Fact]
    public void Sampler_SkipsMaskedColumns()
    {
        var sampler = new PiiSampler(["Email", "Masked"], [false, true], [true, true], maxRows: 10);
        foreach (var v in new[] { "a@b.com", "c@d.com", "e@f.com" })
        {
            sampler.Add(0, v); sampler.Add(1, v); sampler.EndRow();
        }
        var f = Assert.Single(sampler.Findings());
        Assert.Equal(("Email", PiiKind.Email), (f.Column, f.Kind));
    }
}
