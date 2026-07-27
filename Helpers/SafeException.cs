using System;

namespace SignalTracker.Helper
{
    public static class SafeException
    {
        public static string Get(Exception? ex)
        {
            if (ex == null) return "An internal server error occurred.";
            // Only reveal actual exception messages in Development environment
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (!string.IsNullOrEmpty(env) && env.Equals("Development", StringComparison.OrdinalIgnoreCase))
                return ex.Message;
            return "An internal server error occurred.";
        }

        public static string GetInnermost(Exception? ex)
        {
            if (ex == null) return "An internal server error occurred.";
            while (ex.InnerException != null) ex = ex.InnerException;
            return Get(ex);
        }
    }
}


