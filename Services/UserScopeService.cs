using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SignalTracker.Services
{
    public class UserScopeService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public UserScopeService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        // Constants for Roles
        public const int ROLE_SUPER_ADMIN = 3;
        public const int ROLE_ADMIN = 2;
        public const int ROLE_USER = 1;

        /// <summary>
        /// Resolve the effective company scope for the current user.
        /// Super admins may optionally scope to a requested company; regular users
        /// are always restricted to their own company.
        /// </summary>
        public int GetTargetCompanyId(ClaimsPrincipal user, int? requestedCompanyId)
        {
            if (IsSuperAdmin(user))
            {
                // Super admins are global by default. They only scope down when
                // a specific company is explicitly requested.
                return requestedCompanyId.GetValueOrDefault(0);
            }

            return GetCompanyId(user);
        }
        public bool IsSuperAdmin(ClaimsPrincipal user)
        {
            var userTypeId =
                GetIntClaim(user, "UserTypeId", "m_user_type_id")
                ?? _httpContextAccessor.HttpContext?.Session?.GetInt32("UserType")
                ?? ParseInt(_httpContextAccessor.HttpContext?.Session?.GetString("UserType"))
                ?? 0;

            return userTypeId == ROLE_SUPER_ADMIN;
        }

        public int GetCurrentUserId(ClaimsPrincipal user)
        {
            return GetIntClaim(user, "UserId")
                ?? _httpContextAccessor.HttpContext?.Session?.GetInt32("UserID")
                ?? ParseInt(_httpContextAccessor.HttpContext?.Session?.GetString("UserID"))
                ?? 0;
        }

        internal int GetCompanyId(ClaimsPrincipal user)
        {
            return GetIntClaim(user, "CompanyId", "company_id")
                ?? _httpContextAccessor.HttpContext?.Session?.GetInt32("CompanyId")
                ?? ParseInt(_httpContextAccessor.HttpContext?.Session?.GetString("CompanyId"))
                ?? 0;
        }

        private static string? GetStringClaim(ClaimsPrincipal user, params string[] claimTypes)
        {
            foreach (var claimType in claimTypes)
            {
                var value = user.FindFirst(claimType)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static int? GetIntClaim(ClaimsPrincipal user, params string[] claimTypes)
        {
            var raw = GetStringClaim(user, claimTypes);
            return ParseInt(raw);
        }

        private static int? ParseInt(string? value)
        {
            return int.TryParse(value, out var parsed) ? parsed : null;
        }
    }
}


