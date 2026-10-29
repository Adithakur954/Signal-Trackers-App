using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

// Local operator entry point only. Never mapped to an HTTP endpoint.
public static class RfMobileCredentialConsole
{
    public static RfMobileCredentialRequest ReadRequest(IConfiguration configuration)
    {
        var days = configuration["rf-mobile-valid-days"];
        var userId = configuration["rf-mobile-user-id"];
        if ((days != null && !int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            || (userId != null && !int.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new RfLogRequestException(400, "Validity and user ID must be positive integers.");
        var request = new RfMobileCredentialRequest
        {
            MobileNumber = configuration["rf-mobile-number"] ?? "",
            CompanyCode = configuration["rf-mobile-company-code"] ?? "",
            DeviceLabel = configuration["rf-mobile-device-label"] ?? "",
            ValidDays = days == null ? 90 : int.Parse(days, CultureInfo.InvariantCulture),
            UserId = userId == null ? null : int.Parse(userId, CultureInfo.InvariantCulture)
        };
        if (!Validator.TryValidateObject(request, new ValidationContext(request), [], validateAllProperties: true))
            throw new RfLogRequestException(400, "Supply a mobile number, company code, device label, optional positive user ID and validity of 1 to 365 days.");
        return request;
    }

    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Local RF credential provisioning requires administrator PowerShell on the Windows backend server.");
            return 1;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("Run PowerShell as Administrator on the backend server. No portal email or password is required.");
            return 1;
        }
        try
        {
            var region = configuration["rf-mobile-region"];
            if (region is not ("IN" or "TW")) throw new RfLogRequestException(400, "Specify rf-mobile-region as IN or TW.");
            var request = ReadRequest(configuration);
            var connectionString = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region));
            if (string.IsNullOrWhiteSpace(connectionString)) throw new RfLogRequestException(503, "The selected regional database is not configured.");
            await using var conn = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString)
                { GuidFormat = MySqlGuidFormat.None, ConnectionTimeout = 10 }.ConnectionString);
            // Resolve existing associations without accepting a caller-supplied
            // company ID or changing user/company records.
            var owners = (await conn.QueryAsync<RfMobileOwner>(new CommandDefinition("""
                SELECT u.id UserId,u.company_id CompanyId FROM tbl_user u JOIN tbl_company c ON c.id=u.company_id
                WHERE u.isactive=1 AND COALESCE(u.is_deleted,0)=0 AND COALESCE(c.status,0)<>99
                  AND TRIM(u.mobile)=@mobile AND TRIM(c.company_code)=@code
                  AND (@UserId IS NULL OR u.id=@UserId) LIMIT 2
                """, new { mobile = request.MobileNumber.Trim(), code = request.CompanyCode.Trim(), request.UserId }, cancellationToken: ct))).ToArray();
            if (owners.Length == 0) throw new RfLogRequestException(403, "No active user matches this mobile/company in the selected regional database. Correct the account mapping first.");
            if (owners.Length != 1) throw new RfLogRequestException(409, "Multiple users match. Supply rf-mobile-user-id.");
            var owner = owners[0];
            var service = new RfMobileCredentialService(new RfMobileCredentialStore(configuration), TimeProvider.System);
            var issued = await service.IssueAsync(new RfLogActor("local-server-administrator", owner.CompanyId, null, region), request, ct);
            // Raw token shown once to the local operator; never log/redirect it.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                credential = RfMobileCredentialService.Metadata(issued.Credential),
                tokenType = "Bearer", uploadToken = issued.Token
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (RfLogRequestException ex)
        {
            Console.Error.WriteLine($"RF credential provisioning failed ({ex.Status}): {ex.Message}");
            return 1;
        }
        catch (MySqlException)
        {
            Console.Error.WriteLine("RF credential database operation failed. Verify regional settings, connectivity and migration 006. No credentials were printed.");
            return 1;
        }
    }
}
