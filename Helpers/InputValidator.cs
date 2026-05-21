using System.Text.RegularExpressions;
using System.Web;

namespace SignalTracker.Helper
{
    public static class InputValidator
    {
       
        private static readonly Regex AllowedRemarksPattern = new Regex(@"^[a-zA-Z0-9\s.,!?()@#\-]*$", RegexOptions.Compiled);

        public static (bool isValid, string sanitized, string errorMessage) ValidateRemarks(string rawInput, string inputName, int MaxRemarksLength = 250)
        {
            string remarks = rawInput?.Trim() ?? "";

            if (remarks.Equals("undefined", StringComparison.OrdinalIgnoreCase))
                remarks = "";

            remarks = HttpUtility.HtmlEncode(remarks);

            if (remarks.Length > MaxRemarksLength)
            {
                return (false, remarks, $"{inputName} should not exceed {MaxRemarksLength} characters.");
            }

            if (!AllowedRemarksPattern.IsMatch(remarks))
            {
                return (false, remarks, $"{inputName} contains invalid characters. Only letters, numbers, spaces, and basic punctuation are allowed.");
            }

            return (true, remarks, string.Empty);
        }
    }
}
