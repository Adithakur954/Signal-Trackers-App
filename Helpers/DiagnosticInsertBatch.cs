using System.Data.Common;
using System.Text;

namespace SignalTracker.Helper;

// Table and column names come only from the importer's fixed schema.
internal sealed class DiagnosticInsertBatch(
    DbConnection connection, DbTransaction? transaction, string table, string[] columns)
{
    private const int MaxRows = 200;
    private const long MaxPayloadBytes = 1024 * 1024;
    private readonly List<object?[]> rows = new(MaxRows);
    private long payloadBytes;

    internal int RowsWritten { get; private set; }

    internal void Add(object?[] values)
    {
        if (values.Length != columns.Length)
            throw new ArgumentException("Diagnostic column/value count mismatch.", nameof(values));

        // Allow for parameter escaping and SQL overhead as well as UTF-8 text.
        var rowBytes = values.Sum(value => value is string text
            ? 2L * Encoding.UTF8.GetByteCount(text) + 32 : 32L);
        if (rows.Count > 0 && payloadBytes + rowBytes > MaxPayloadBytes)
            Flush();
        rows.Add(values);
        payloadBytes += rowBytes;
        if (rows.Count >= MaxRows || payloadBytes >= MaxPayloadBytes)
            Flush();
    }

    internal void Flush()
    {
        if (rows.Count == 0) return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 180;
        var sql = new StringBuilder($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ");
        var parameterIndex = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rowIndex > 0) sql.Append(',');
            sql.Append('(');
            for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
            {
                if (columnIndex > 0) sql.Append(',');
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{parameterIndex++}";
                parameter.Value = rows[rowIndex][columnIndex] ?? DBNull.Value;
                command.Parameters.Add(parameter);
                sql.Append(parameter.ParameterName);
            }
            sql.Append(')');
        }
        command.CommandText = sql.Append(';').ToString();
        command.ExecuteNonQuery();
        RowsWritten += rows.Count;
        rows.Clear();
        payloadBytes = 0;
    }
}
