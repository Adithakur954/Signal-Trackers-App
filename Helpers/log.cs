using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Helper
{
    public class Writelog
    {
        private readonly ApplicationDbContext _db;

        public Writelog(ApplicationDbContext db)
        {
            _db = db;
        }

        public int write_exception_log(int userId, string sourceFile, string functionName, DateTime errorDate, Exception ex)
        {
            try
            {
                var history = new exception_history();
                string error = GetInnermostExceptionMessage(ex);
                int lineNo = GetLineNumber(ex);
                error += $" at line no {lineNo}";

                history.user_id = userId;
                history.source_file = sourceFile;
                history.page = functionName;
                history.exception_date = errorDate;
                history.exception = error;

               _db.exception_history.Add(history);
                _db.SaveChanges();

                return 1; // Success
            }
            catch (Exception innerEx)
            {
                var fallbackError = GetInnermostExceptionMessage(innerEx);
                Console.Error.WriteLine($"write_exception_log failed for {sourceFile}.{functionName}: {fallbackError}");

                return 0; // Failure
            }
        }

        private static string GetInnermostExceptionMessage(Exception ex)
        {
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex.Message;
        }

        private static int GetLineNumber(Exception ex)
        {
            const string lineSearch = ":line ";
            int index = ex.StackTrace?.LastIndexOf(lineSearch) ?? -1;

            if (index != -1)
            {
                string lineNumberText = ex.StackTrace.Substring(index + lineSearch.Length);
                if (int.TryParse(lineNumberText, out int lineNumber))
                {
                    return lineNumber;
                }
            }
            return 0;
        }

        internal void write_log(string v1, string v2, string v3)
        {
            throw new NotImplementedException();
        }
    }
}
