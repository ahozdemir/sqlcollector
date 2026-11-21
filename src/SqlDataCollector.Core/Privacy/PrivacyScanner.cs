using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Privacy;

/// <summary>A column of a collected table whose sampled values look like personal data.</summary>
public sealed record PiiFinding(string Schema, string Table, string Column, PiiKind Kind, double Ratio, string Example, string? CoveredBy)
{
    public string ObjectName(string database) => $"{database}.{Schema}.{Table}";
}

/// <summary>
/// Samples the most recent rows of every collected table of a target database and reports columns that look like
/// personal data, with the masking rule that already covers them (if any). Read-only.
/// </summary>
public sealed class PrivacyScanner(SqlConnectionFactory factory, MetadataRepository repo, MaskingService masking)
{
    public async Task<IReadOnlyList<PiiFinding>> ScanAsync(int serverId, string database, int sampleRows = 200, CancellationToken ct = default)
    {
        var server = await repo.GetServerAsync(serverId) ?? throw new InvalidOperationException($"Server {serverId} not found.");
        var policy = await masking.GetPolicyAsync();
        var findings = new List<PiiFinding>();
        await using var conn = await factory.OpenAsync(server, database, forWrite: false, ct);

        foreach (var t in await repo.GetCollectedTablesAsync(serverId, database, null))
        {
            if (!await SqlHelpers.TableExistsAsync(conn, t.SchemaName, t.TableName, ct)) continue;
            await using var cmd = conn.CreateCommand();
            // latest loads first; a table that was not created by the collector (no _LoadId) is sampled as it comes
            var order = await SqlHelpers.HasLoadIdColumnAsync(conn, t.SchemaName, t.TableName, ct) ? $" ORDER BY {SqlIdent.Quote(SqlIdent.LoadIdColumn)} DESC" : "";
            cmd.CommandText = $"SELECT TOP (@n) * FROM {SqlIdent.Quote(t.SchemaName, t.TableName)}{order};";
            cmd.Parameters.AddWithValue("@n", sampleRows);
            cmd.CommandTimeout = 120;
            await using var r = await cmd.ExecuteReaderAsync(ct);

            var cols = Enumerable.Range(0, r.FieldCount)
                .Where(i => !SqlIdent.IsTechnical(r.GetName(i)) && IsCandidate(r.GetFieldType(i)))
                .Select(i => (Ordinal: i, Name: r.GetName(i), Text: r.GetFieldType(i) == typeof(string), Values: new List<string?>()))
                .ToList();
            if (cols.Count == 0) continue;
            while (await r.ReadAsync(ct))
                foreach (var c in cols)
                    c.Values.Add(r.IsDBNull(c.Ordinal) ? null : Masker.ToText(r.GetValue(c.Ordinal)));

            var objectName = $"{database}.{t.SchemaName}.{t.TableName}";
            foreach (var c in cols)
            {
                if (PiiDetector.Classify(c.Values, c.Text) is not { } hit) continue;
                var rule = policy.Find(c.Name, [objectName], MaskTarget.ExtractAndExports) ?? policy.Find(c.Name, [objectName], MaskTarget.Exports);
                var example = c.Values.First(v => PiiDetector.Detect(v) == hit.Kind)!;
                findings.Add(new PiiFinding(t.SchemaName, t.TableName, c.Name, hit.Kind, hit.Ratio,
                    Masker.Partial(example, Math.Min(4, example.Length / 3)),
                    rule is null ? null : $"{rule.Name} ({rule.Describe()}, {(rule.AppliesTo == MaskTarget.Exports ? "exports" : "collection + exports")})"));
            }
        }
        return findings;
    }

    private static bool IsCandidate(Type t) => t == typeof(string) || t == typeof(long) || t == typeof(decimal);
}
