using System.IO.Compression;
using System.Text;
using Parquet;

namespace SqlDataCollector.Export;

/// <summary>
/// Counts the data rows actually contained in written export files (export integrity check): CSV records are
/// counted RFC 4180-aware (line breaks inside quoted fields do not end a record), Parquet rows from the row groups.
/// </summary>
public static class FileRowCounter
{
    /// <summary>Data rows in a CSV file written by <see cref="CsvPartWriter"/> (header excluded; .gz is decompressed).</summary>
    public static async Task<long> CountCsvAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        await using (stream)
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, 1 << 16);
            var records = await CountRecordsAsync(reader, ct);
            return Math.Max(0, records - 1);   // the first record is the header
        }
    }

    /// <summary>Records in CSV text: line feeds outside quoted fields, plus a last record without a final line break.</summary>
    public static async Task<long> CountRecordsAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[1 << 16];
        long records = 0;
        bool inQuotes = false, pending = false;
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (c == '"') inQuotes = !inQuotes;            // "" inside a field toggles twice: no net change
                else if (c == '\n' && !inQuotes) { records++; pending = false; continue; }
                if (c != '\r') pending = true;
            }
        }
        return pending ? records + 1 : records;
    }

    /// <summary>Rows in a Parquet file (sum of its row groups).</summary>
    public static async Task<long> CountParquetAsync(string path, CancellationToken ct)
    {
        await using var reader = await ParquetReader.CreateAsync(path, cancellationToken: ct);
        long rows = 0;
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using var rg = reader.OpenRowGroupReader(i);
            rows += rg.RowCount;
        }
        return rows;
    }
}
