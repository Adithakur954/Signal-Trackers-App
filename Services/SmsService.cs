using System.Net.Http;

namespace SignalTracker.Services
{
    public interface ISmsService
    {
        Task SendOtpAsync(string phoneNumber, string otp, CancellationToken ct = default);
    }

    public sealed class SmsDeliveryException : Exception
    {
        public SmsDeliveryException(string message, string? providerResponse = null) : base(message)
        {
            ProviderResponse = providerResponse;
        }

        public string? ProviderResponse { get; }
    }

    public sealed class SmsService : ISmsService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmsService> _logger;

        public SmsService(HttpClient httpClient, IConfiguration configuration, ILogger<SmsService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendOtpAsync(string phoneNumber, string otp, CancellationToken ct = default)
        {
            var endpoint = GetSetting("SMS_API_ENDPOINT_URL");
            var apiKey = GetSetting("SMS_API_KEY");
            var senderId = GetSetting("SMS_SENDER_ID");
            var templateId = GetSetting("SMS_TEMPLATE_ID");
            var entityId = GetSetting("SMS_ENTITY_ID");
            var smsPhoneNumber = FormatPhoneForSms(phoneNumber);

            _logger.LogInformation("SMS Phone Format Debug - Input: {Input}, Formatted: {Formatted}, CountryCode: {CountryCode}",
                phoneNumber, smsPhoneNumber, GetSetting("SMS_DEFAULT_COUNTRY_CODE"));

            if (string.IsNullOrWhiteSpace(endpoint) ||
                string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(senderId))
            {
                throw new SmsDeliveryException("SMS service is not configured.");
            }

            var message = $"Use {otp} to login to your account It is valid for 10 min. Do not share this with anyone for security reasons. - Dapto";
            var requestUri = BuildProviderUrl(endpoint, apiKey, senderId, templateId, entityId, smsPhoneNumber, message);

            _logger.LogInformation("Sending SMS to provider: {Url}", requestUri);

            using var response = await _httpClient.GetAsync(requestUri, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("SMS provider response {StatusCode}: {Body}", response.StatusCode, body);

            if (!response.IsSuccessStatusCode || LooksLikeProviderFailure(body))
            {
                _logger.LogWarning("SMS provider returned {StatusCode}: {Body}", response.StatusCode, body);
                throw new SmsDeliveryException("Unable to send OTP SMS.", body);
            }
        }

        private string? GetSetting(string key)
            => _configuration[key] ?? Environment.GetEnvironmentVariable(key);

        private string FormatPhoneForSms(string phoneNumber)
        {
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            var defaultCountryCode = GetSetting("SMS_DEFAULT_COUNTRY_CODE") ?? "91";
            var stripCountryCode = GetBool("SMS_STRIP_COUNTRY_CODE", true);

            // Strip leading country code to get to bare 10-digit number
            // e.g. 916307252260 â†’ 6307252260, +918130653366 â†’ 8130653366
            if (stripCountryCode
                && !string.IsNullOrWhiteSpace(defaultCountryCode)
                && digits.Length > defaultCountryCode.Length
                && digits.StartsWith(defaultCountryCode))
            {
                digits = digits[defaultCountryCode.Length..];
            }

            return digits;
        }

        private bool GetBool(string key, bool fallback)
        {
            var value = GetSetting(key);
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static bool LooksLikeProviderFailure(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;

            var normalized = body.Trim().ToLowerInvariant();
            return normalized.Contains("fail", StringComparison.Ordinal)
                || normalized.Contains("error", StringComparison.Ordinal)
                || normalized.Contains("invalid", StringComparison.Ordinal)
                || normalized.Contains("insufficient", StringComparison.Ordinal)
                || normalized.Contains("unauthor", StringComparison.Ordinal)
                || normalized.Contains("denied", StringComparison.Ordinal)
                || normalized.Contains("blacklist", StringComparison.Ordinal);
        }

        private string BuildProviderUrl(
            string endpoint,
            string apiKey,
            string senderId,
            string? templateId,
            string? entityId,
            string phoneNumber,
            string message)
        {
            var query = new Dictionary<string, string?>
            {
                [GetSetting("SMS_SENDER_ID_PARAMETER") ?? "sender"]    = senderId,
                [GetSetting("SMS_NUMBER_PARAMETER")    ?? "numbers"]   = phoneNumber,
                [GetSetting("SMS_MESSAGE_TYPE_PARAMETER") ?? "messagetype"] = GetSetting("SMS_MESSAGE_TYPE_VALUE") ?? "TXT",
                [GetSetting("SMS_MESSAGE_PARAMETER")   ?? "message"]   = message,
                [GetSetting("SMS_RESPONSE_PARAMETER")  ?? "response"]  = GetSetting("SMS_RESPONSE_VALUE") ?? "Y",
                [GetSetting("SMS_API_KEY_PARAMETER")   ?? "apikey"]    = apiKey,
                [GetSetting("SMS_TEMPLATE_ID_PARAMETER") ?? "templateid"] = templateId,
                [GetSetting("SMS_ENTITY_ID_PARAMETER") ?? "entityid"]  = entityId
            };

            var separator = endpoint.Contains('?') ? "&" : "?";
            var queryString = string.Join("&", query
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

            return endpoint + separator + queryString;
        }
    }
}


