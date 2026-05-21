using System.Data.Common;

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
                        : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return rows;
        }
    }
}
