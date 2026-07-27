using System.Data.Common;
using System.Globalization;

namespace SignalTracker.Helper
{
    public static class PythonBridgeDbTool
    {
        public static void AddParam(DbCommand command, string name, object? value)
        {
            var param = command.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            command.Parameters.Add(param);
        }

        public static string BuildInClause(DbCommand command, IReadOnlyList<long> values, string parameterPrefix)
        {
            if (values.Count == 0)
            {
                return string.Empty;
            }

            var placeholders = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                var parameterName = $"@{parameterPrefix}{i}";
                placeholders[i] = parameterName;
                AddParam(command, parameterName, values[i]);
            }

            return string.Join(",", placeholders);
        }

        public static object? ConvertDbValue(object? rawVal)
        {
            if (rawVal == null || rawVal == DBNull.Value)
                return null;

            if (rawVal is DateTime dt)
            {
                return dt.Millisecond > 0
                    ? dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (rawVal is DateTimeOffset dto)
            {
                return dto.Millisecond > 0
                    ? dto.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)
                    : dto.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (rawVal is TimeSpan ts)
            {
                return ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            }

            var typeName = rawVal.GetType().Name;
            if (typeName.Contains("MySqlDateTime", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var isValProp = rawVal.GetType().GetProperty("IsValidDateTime")?.GetValue(rawVal);
                    if (isValProp is bool isValid && !isValid)
                        return null;

                    var getDtMethod = rawVal.GetType().GetMethod("GetDateTime");
                    if (getDtMethod != null)
                    {
                        var dtVal = (DateTime)getDtMethod.Invoke(rawVal, null)!;
                        return dtVal.Millisecond > 0
                            ? dtVal.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)
                            : dtVal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    }
                }
                catch { }

                if (DateTime.TryParse(rawVal.ToString(), out var parsedDt))
                {
                    return parsedDt.Millisecond > 0
                        ? parsedDt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)
                        : parsedDt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                }
            }

            return rawVal;
        }

        public static Dictionary<string, object?> SanitizeRow(Dictionary<string, object?> row)
        {
            var keys = row.Keys.ToList();
            foreach (var key in keys)
            {
                row[key] = ConvertDbValue(row[key]);
            }
            return row;
        }

        public static List<Dictionary<string, object?>> SanitizeRows(List<Dictionary<string, object?>> rows)
        {
            foreach (var row in rows)
            {
                SanitizeRow(row);
            }
            return rows;
        }

        public static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(
            DbDataReader reader,
            CancellationToken cancellationToken = default
        )
        {
            var rows = new List<Dictionary<string, object?>>();

            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken)
                        ? null
                        : ConvertDbValue(reader.GetValue(i));
                }
                rows.Add(row);
            }

            return rows;
        }
    }
}


