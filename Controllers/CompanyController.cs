using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;
using SignalTracker.DTOs;
using Microsoft.Extensions.Configuration;

namespace SignalTracker.Controllers
{
    [Route("api/company")]
    [ApiController]
    [Authorize]
    public class CompanyController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly UserScopeService _userScope;
        private readonly LicenseFeatureService _licenseFeatureService;
        private readonly IConfiguration _configuration;

        public CompanyController(
            ApplicationDbContext db,
            UserScopeService userScope,
            LicenseFeatureService licenseFeatureService,
            IConfiguration configuration)
        {
            _db = db;
            _userScope = userScope;
            _licenseFeatureService = licenseFeatureService;
            _configuration = configuration;
        }

        [HttpGet("GetAll")]
        public async Task<IActionResult> GetAllCompanies([FromQuery] int? id = null)
        {
            try
            {
                int targetCompanyId = _userScope.GetTargetCompanyId(User, id);
                var query = _db.tbl_company.AsNoTracking().AsQueryable();

                // Exclude soft-deleted companies
               // query = query.Where(c => c.status != 0);

                if (targetCompanyId > 0)
                {
                    query = query.Where(c => c.id == targetCompanyId);
                }

                var companies = await query
                    .Select(c => new CompanyDto
                    {
                        id = c.id,
                        company_name = c.company_name,
                        contact_person = c.contact_person,
                        mobile = c.mobile,
                        email = c.email,
                        address = c.address,
                        pincode = c.pincode,
                        gst_id = c.gst_id,
                        company_code = c.company_code,
                        country_code=c.country_code,
                        isd_code=c.isd_code,
                        license_validity_in_months = c.license_validity_in_months,
                        total_granted_licenses = c.total_granted_licenses,
                        total_used_licenses = c.total_used_licenses,
                        otp_phone_number = c.otp_phone_number,
                        ask_for_otp = c.ask_for_otp,
                        blacklisted_phone_number = c.blacklisted_phone_number,
                        remarks = c.remarks,
                        created_on = c.created_on,
                        status = c.status,
                        last_login = c.last_login
                    })
                    .OrderBy(c => c.company_name)
                    .ToListAsync();

                return Ok(new { Status = 1, Message = "Success", Data = companies });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error fetching companies: " + ex.Message });
            }
        }
      [HttpPost("SaveCompanyDetails")]
        public async Task<IActionResult> SaveCompany([FromBody] SaveCompanyRequest request)
        {
            try
            {
                // Security Check
                if (!_userScope.IsSuperAdmin(User))
                {
                    return StatusCode(403, new { Status = 0, Message = "Only Super Admin can create or update companies" });
                }

                if (request == null)
                    return BadRequest(new { Status = 0, Message = "Invalid request data" });

                // ============================================================
                //  CREATE NEW COMPANY
                // ============================================================
                if (request.id == null || request.id == 0)
                {
                    // A. Check for Duplicate Email
                    bool emailExists = await _db.tbl_company
                        .AnyAsync(x => x.email == request.email && x.status != 99);

                    if (emailExists)
                        return Conflict(new { Status = 0, Message = "Company already exists with this email" });

                    // B. Validate Password
                    if (string.IsNullOrEmpty(request.password))
                        return BadRequest(new { Status = 0, Message = "Password is required for new companies" });

                    string hashedPassword = PasswordSecurity.HashPassword(request.password);

                    // C. Generate Unique Company Code
                    string companyCode;
                    do
                    {
                        companyCode = new Random().Next(1000, 9999).ToString();
                    }
                    while (await _db.tbl_company.AnyAsync(x => x.company_code == companyCode));

                    string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

                    // D. Create Company Object
                    var company = new tbl_company
                    {
                        company_name = request.company_name,
                        contact_person = request.contact_person,
                        mobile = request.mobile,
                        email = request.email,
                        password = hashedPassword,
                        country_code=request.country_code,
                        isd_code=request.isd_code,
                        address = request.address,
                        pincode = request.pincode,
                        gst_id = request.gst_id,
                        
                        company_code = companyCode,
                        license_validity_in_months = request.license_validity_in_months,
                        
                        // FIX 1: Accessing the property correctly now that it's in the DTO
                        total_granted_licenses = request.total_granted_licenses > 0 ? request.total_granted_licenses : 10, 
                        total_used_licenses = 0,

                        // FIX 2: Removed ternary '?' check on int. Direct assignment.
                        ask_for_otp = request.ask_for_otp, 
                        otp_phone_number = request.otp_phone_number,
                        blacklisted_phone_number = request.blacklisted_phone_number,
                        remarks = request.remarks,

                        created_on = DateTime.UtcNow,
                        
                        // FIX 3: Removed ternary '?' check on int. Direct assignment.
                        status = request.status, 
                        
                        token = token,
                        uid = Guid.NewGuid().ToString()
                    };

                    _db.tbl_company.Add(company);
                    await _db.SaveChangesAsync();

                    // E. Create Default Admin User
                    var user = new tbl_user
                    {
                        name = request.contact_person,
                        mobile = request.mobile,
                        email = request.email,
                        password = hashedPassword,
                        company_id = company.id,
                        country_code=request.country_code,
                        isd_code=request.isd_code,
                        date_created = DateTime.UtcNow,
                        isactive = 1,
                        m_user_type_id = 2, // Company Admin
                        token = token,
                        uid = Guid.NewGuid().ToString()
                    };

                    _db.tbl_user.Add(user);
                    await _db.SaveChangesAsync();

                    // ============================================================
                    //  MULTI-DATABASE SUPPORT: If TW, save to Secondary DB
                    // ============================================================
                    if (!string.IsNullOrEmpty(request.country_code) && request.country_code.ToUpper() == "TW")
                    {
                        try
                        {
                            var secondaryConnectionString = _configuration.GetConnectionString("MySqlConnection2");
                            secondaryConnectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(secondaryConnectionString);
                            if (!string.IsNullOrEmpty(secondaryConnectionString))
                            {
                                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                                var serverVersion = new MySqlServerVersion(new Version(8, 0, 29));
                                optionsBuilder.UseMySql(secondaryConnectionString, serverVersion);

                                using (var secondaryDb = new ApplicationDbContext(optionsBuilder.Options))
                                {
                                    // 1. Create Company in Secondary DB
                                    // Note: We create a NEW object to avoid tracking issues with the previous context
                                    var twCompany = new tbl_company
                                    {
                                        company_name = request.company_name,
                                        contact_person = request.contact_person,
                                        mobile = request.mobile,
                                        email = request.email,
                                        password = hashedPassword,
                                        country_code = request.country_code,
                                        isd_code = request.isd_code,
                                        address = request.address,
                                        pincode = request.pincode,
                                        gst_id = request.gst_id,
                                        company_code = companyCode, // Keep same code
                                        license_validity_in_months = request.license_validity_in_months,
                                        total_granted_licenses = request.total_granted_licenses > 0 ? request.total_granted_licenses : 10,
                                        total_used_licenses = 0,
                                        ask_for_otp = request.ask_for_otp,
                                        otp_phone_number = request.otp_phone_number,
                                        blacklisted_phone_number = request.blacklisted_phone_number,
                                        remarks = request.remarks,
                                        created_on = DateTime.UtcNow,
                                        status = request.status,
                                        token = token,
                                        uid = Guid.NewGuid().ToString() // New UUID for new DB entry
                                    };

                                    secondaryDb.tbl_company.Add(twCompany);
                                    await secondaryDb.SaveChangesAsync();

                                    // 2. Create User in Secondary DB
                                    var twUser = new tbl_user
                                    {
                                        name = request.contact_person,
                                        mobile = request.mobile,
                                        email = request.email,
                                        password = hashedPassword,
                                        company_id = twCompany.id, // Use the new ID from Secondary DB
                                        country_code = request.country_code,
                                        isd_code = request.isd_code,
                                        date_created = DateTime.UtcNow,
                                        isactive = 1,
                                        m_user_type_id = 2, 
                                        token = token,
                                        uid = Guid.NewGuid().ToString()
                                    };

                                    secondaryDb.tbl_user.Add(twUser);
                                    await secondaryDb.SaveChangesAsync();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log error but don't fail the request since primary succeeded
                            Console.WriteLine($"Error saving to TW database: {ex.Message}");
                        }
                    }

                    return Ok(new
                    {
                        Status = 1,
                        Message = "Company and Default User created successfully",
                        CompanyId = company.id,
                        CompanyCode = companyCode
                    });
                }
                // ============================================================
                //  UPDATE EXISTING COMPANY
                // ============================================================
                // ============================================================
//  UPDATE EXISTING COMPANY (WITH LICENSE UPDATE SUPPORT)
// ============================================================
else
{
    var existingCompany = await _db.tbl_company.FindAsync(request.id);
    if (existingCompany == null)
        return NotFound(new { Status = 0, Message = "Company not found" });

    // ==============================
    // BASIC COMPANY DETAILS
    // ==============================
    existingCompany.company_name = request.company_name;
    existingCompany.contact_person = request.contact_person;
    existingCompany.mobile = request.mobile;
    existingCompany.address = request.address;
    existingCompany.pincode = request.pincode;
    existingCompany.gst_id = request.gst_id;
    existingCompany.country_code = request.country_code;
    existingCompany.isd_code = request.isd_code;
    existingCompany.ask_for_otp = request.ask_for_otp;
    existingCompany.otp_phone_number = request.otp_phone_number;
    existingCompany.blacklisted_phone_number = request.blacklisted_phone_number;
    existingCompany.remarks = request.remarks;
    existingCompany.license_validity_in_months = request.license_validity_in_months;
    existingCompany.status = request.status;

    // ==============================
    // PASSWORD UPDATE (OPTIONAL)
    // ==============================
    if (!string.IsNullOrEmpty(request.password))
    {
        var hashedPassword = PasswordSecurity.HashPassword(request.password);
        existingCompany.password = hashedPassword;

        // Keep the default company admin user in sync with company password
        var adminUsers = await _db.tbl_user
            .Where(u => u.company_id == existingCompany.id && u.m_user_type_id == 2)
            .ToListAsync();

        foreach (var u in adminUsers)
        {
            u.password = hashedPassword;
        }
    }

    // ==============================
    //  LICENSE UPDATE LOGIC
    // ==============================

    // 1 Update total granted licenses
    if (request.total_granted_licenses >= 0)
    {
        if (request.total_granted_licenses < existingCompany.total_used_licenses)
        {
            return BadRequest(new
            {
                Status = 0,
                Message = "Total granted licenses cannot be less than used licenses"
            });
        }

        existingCompany.total_granted_licenses = request.total_granted_licenses;
    }

    // 2️ Update total used licenses
    if (request.total_used_licenses >= 0)
    {
        if (request.total_used_licenses > existingCompany.total_granted_licenses)
        {
            return BadRequest(new
            {
                Status = 0,
                Message = "Used licenses cannot exceed granted licenses"
            });
        }

        existingCompany.total_used_licenses = request.total_used_licenses;
    }

    _db.tbl_company.Update(existingCompany);
    await _db.SaveChangesAsync();

    return Ok(new
    {
        Status = 1,
        Message = "Company and License details updated successfully"
    });
} }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error saving company: " + ex.Message });
            }
        }
        // Delete Company 
        // ======================================================
// 5️ DELETE COMPANY (SOFT DELETE)
// ======================================================
[HttpDelete("deleteCompany")]
public async Task<IActionResult> DeleteCompany([FromQuery] int id)
{
    try
    {
        //  Only Super Admin can delete
        if (!_userScope.IsSuperAdmin(User))
        {
            return StatusCode(403, new
            {
                Status = 0,
                Message = "Only Super Admin can delete companies"
            });
        }

        var company = await _db.tbl_company.FirstOrDefaultAsync(x => x.id == id);
        if (company == null)
        {
            return NotFound(new
            {
                Status = 0,
                Message = "Company not found"
            });
        }

        //  Hard delete path: remove company and related data
        // Note: this permanently removes rows from the database.

        // 1) Remove related issued licenses
        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM tbl_company_user_license_issued
              WHERE tbl_company_id = {0}",
            id);

        // 2) Remove related license grant history (optional, but keeps data clean)
        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM tbl_company_license_grant_history
              WHERE tbl_company_id = {0}",
            id);

        // 3) Remove sessions for users of this company (FK: tbl_session.user_id -> tbl_user.id)
        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE s FROM tbl_session s
              INNER JOIN tbl_user u ON s.user_id = u.id
              WHERE u.company_id = {0}",
            id);

        // 4) Remove related users
        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM tbl_user
              WHERE company_id = {0}",
            id);

        // 4) Remove the company itself
        _db.tbl_company.Remove(company);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            Status = 1,
            Message = "Company deleted successfully"
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error deleting company",
            Error = ex.Message
        });
    }
}

                                //Licensing Grant  Part 


 // ======================================================
// GET GRANT LICENSE HISTORY
// ======================================================
[HttpGet("grantLicenseHistory")]
public async Task<IActionResult> GetGrantLicenseHistory([FromQuery] int? companyId = null)
{
    try
    {
        // Resolve which company user is allowed to see
        int targetCompanyId = _userScope.GetTargetCompanyId(User, companyId);

        var query =
            from lic in _db.tbl_company_license_grant_history.AsNoTracking()
            join comp in _db.tbl_company.AsNoTracking()
                on lic.tbl_company_id equals comp.id
            select new
            {
                company_id = comp.id,
                company_name = comp.company_name,
                lic.date_time,
                lic.granted_licenses,
                lic.per_license_rate,
                lic.remarks
            };

        // If company scope is resolved, filter it
        if (targetCompanyId > 0)
            query = query.Where(x => x.company_id == targetCompanyId);

        var data = await query
            .OrderByDescending(x => x.date_time)
            .ToListAsync();

        return Ok(new
        {
            Status = 1,
            Data = data
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error fetching license history",
            Error = ex.Message
        });
    }
}

// ======================================================
// GRANT LICENSE TO COMPANY
// ======================================================
[HttpPost("grantLicense")]
public async Task<IActionResult> GrantLicense([FromBody] GrantLicenseRequest request)
{
    try
    {
        //  Resolve company scope (SuperAdmin vs Company Admin)
        int companyId = _userScope.GetTargetCompanyId(User, request.tbl_company_id);

        //  Fetch company WITHOUT tracking status
        var company = await _db.tbl_company
            .Where(c => c.id == companyId)
            .Select(c => new
            {
                c.id,
                c.total_granted_licenses
            })
            .FirstOrDefaultAsync();

        if (company == null)
        {
            return BadRequest(new
            {
                Status = 0,
                Message = "Invalid company"
            });
        }

        // ===============================
        // UPDATE COMPANY LICENSE COUNT
        // ===============================
        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE tbl_company 
              SET total_granted_licenses = total_granted_licenses + {0}
              WHERE id = {1}",
            request.granted_licenses,
            companyId
        );

        // ===============================
        // INSERT LICENSE HISTORY (NO STATUS)
        // ===============================
        await _db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO tbl_company_license_grant_history
              (tbl_company_id, granted_licenses, per_license_rate, remarks, date_time)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            companyId,
            request.granted_licenses,
            request.per_license_rate,
            request.remarks,
            DateTime.UtcNow
        );

        return Ok(new
        {
            Status = 1,
            Message = "License granted successfully"
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error granting license",
            Error = ex.InnerException?.Message ?? ex.Message
        });
    }
}

// ======================================================
// GET USED LICENSES
// ======================================================
[HttpGet("usedLicenses")]
public async Task<IActionResult> GetUsedLicenses(
    [FromQuery] string? code,
    [FromQuery] string? name,
    [FromQuery] string? email,
    [FromQuery] string? mobile,
    [FromQuery] string? company,
    [FromQuery] int? companyId,
    [FromQuery] int status = 1
)
{
    try
    {
        // Resolve scope (SuperAdmin vs CompanyAdmin)
        int targetCompanyId = _userScope.GetTargetCompanyId(User, companyId);

        var query =
            from lic in _db.tbl_company_user_license_issued.AsNoTracking()
            join usr in _db.tbl_user.AsNoTracking()
                on lic.tbl_user_id equals usr.id
            join comp in _db.tbl_company.AsNoTracking()
                on lic.tbl_company_id equals comp.id
            where usr.isactive != 2
            select new
            {
                license_id = lic.id,
                lic.license_code,
                lic.valid_till,
                lic.created_on,
                license_status = lic.status,

                user_id = usr.id,
                user_name = usr.name,
                user_email = usr.email,
                user_mobile = usr.mobile,
                user_isactive = usr.isactive,

                company_id = comp.id,
                company_name = comp.company_name
            };

        //  Filters
        if (!string.IsNullOrWhiteSpace(code))
            query = query.Where(x => x.license_code != null &&
                                     x.license_code.ToLower().Contains(code.ToLower()));

        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(x => x.user_name != null &&
                                     x.user_name.ToLower().Contains(name.ToLower()));

        if (!string.IsNullOrWhiteSpace(email))
            query = query.Where(x => x.user_email != null &&
                                     x.user_email.ToLower().Contains(email.ToLower()));

        if (!string.IsNullOrWhiteSpace(mobile))
            query = query.Where(x => x.user_mobile != null &&
                                     x.user_mobile.ToLower().Contains(mobile.ToLower()));

        if (!string.IsNullOrWhiteSpace(company))
            query = query.Where(x => x.company_name != null &&
                                     x.company_name.ToLower().Contains(company.ToLower()));

        //  Company scope enforcement
        if (targetCompanyId > 0)
            query = query.Where(x => x.company_id == targetCompanyId);

        var data = await query
            .OrderByDescending(x => x.created_on)
            .ToListAsync();

        var licenseIds = data.Select(x => x.license_id).ToList();
        var featuresByLicenseId = await _licenseFeatureService.GetFeaturesForLicenseIdsAsync(licenseIds);

        var enriched = data.Select(x =>
        {
            var features = featuresByLicenseId.TryGetValue(x.license_id, out var vals)
                ? vals
                : new List<string>();
            return new
            {
                x.license_id,
                x.license_code,
                x.valid_till,
                x.created_on,
                x.license_status,
                x.user_id,
                x.user_name,
                x.user_email,
                x.user_mobile,
                x.user_isactive,
                x.company_id,
                x.company_name,
                feature_codes = string.Join(",", features),
                enabled_features = features
            };
        }).ToList();

        return Ok(new
        {
            Status = 1,
            Data = enriched
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error fetching used licenses",
            Error = ex.InnerException?.Message ?? ex.Message
        });
    }
}

// ======================================================
// REVOKE LICENSE
// ======================================================
// ======================================================
// REVOKE LICENSE
// ======================================================
// FIX 1: Corrected spelling from "revokeLicesnse" to "revokeLicense"
// ======================================================
// REVOKE LICENSE
// ======================================================
// Removed /{licenseId} from the route and added [FromQuery]
[HttpPost("revokeLicense")]
public async Task<IActionResult> RevokeLicense([FromQuery] int licenseId)
{
    try
    {
        //  1. Resolve Company Context
        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);

        //  2. Fetch the License
        var license = await _db.tbl_company_user_license_issued
            .Where(l => l.id == licenseId)
            .Select(l => new
            {
                l.id,
                l.status,
                l.tbl_company_id
            })
            .FirstOrDefaultAsync();

        // 3. Validate License Exists
        if (license == null)
        {
            // If it hits this, your frontend will receive the custom JSON message
            return NotFound(new
            {
                Status = 0,
                Message = "License not found"
            });
        }

        // 4. Security: Prevent revoking if already revoked (Status 2)
        if (license.status == 2)
        {
            return BadRequest(new
            {
                Status = 0,
                Message = "License is already revoked or restricted"
            });
        }

        // 999622222222222222222222222222222222222222222222222222222222222222222222222222 5. Security: Ensure Company Admin owns this license
        if (targetCompanyId > 0 && license.tbl_company_id != targetCompanyId)
        {
            return StatusCode(403, new { Status = 0, Message = "You are not authorized to revoke this license" });
        }

        // ===============================
        // 6. EXECUTE REVOCATION
        // ===============================
        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE tbl_company_user_license_issued
              SET status = 2
              WHERE id = {0}",
            licenseId
        );

        return Ok(new
        {
            Status = 1,
            Message = "License revoked successfully"
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error revoking license",
            Error = ex.Message
        });
    }
}

// ======================================================
// UPDATE USER (NAME/EMAIL/MOBILE/PASSWORD/STATUS)
// ======================================================
[HttpPut("updateUser")]
public async Task<IActionResult> UpdateUser([FromQuery] int userId, [FromBody] UpdateCompanyUserRequest request)
{
    try
    {
        if (request == null)
            return BadRequest(new { Status = 0, Message = "Invalid request data" });

        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);

        var user = await _db.tbl_user.FirstOrDefaultAsync(u => u.id == userId && u.isactive != 2);
        if (user == null)
            return NotFound(new { Status = 0, Message = "User not found" });

        if (targetCompanyId > 0 && user.company_id != targetCompanyId)
            return StatusCode(403, new { Status = 0, Message = "You are not authorized to update this user" });

        if (!string.IsNullOrWhiteSpace(request.email))
        {
            var email = request.email.Trim();
            bool emailExists = await _db.tbl_user
                .AsNoTracking()
                .AnyAsync(x => x.id != userId && x.isactive != 2 && x.email == email);

            if (emailExists)
                return Conflict(new { Status = 0, Message = "Another user already exists with this email" });

            user.email = email;
        }

        if (!string.IsNullOrWhiteSpace(request.name))
            user.name = request.name.Trim();

        if (!string.IsNullOrWhiteSpace(request.mobile))
            user.mobile = request.mobile.Trim();

        if (!string.IsNullOrWhiteSpace(request.country_code))
            user.country_code = request.country_code.Trim();

        if (!string.IsNullOrWhiteSpace(request.isd_code))
            user.isd_code = request.isd_code.Trim();

        if (request.isactive.HasValue)
        {
            var status = request.isactive.Value;
            if (status != 0 && status != 1 && status != 2)
                return BadRequest(new { Status = 0, Message = "Invalid user status. Allowed values: 0, 1, 2" });

            user.isactive = status;
        }

        if (request.m_user_type_id.HasValue)
        {
            if (!_userScope.IsSuperAdmin(User))
                return StatusCode(403, new { Status = 0, Message = "Only Super Admin can change user type" });

            user.m_user_type_id = request.m_user_type_id.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.password))
            user.password = PasswordSecurity.HashPassword(request.password);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            Status = 1,
            Message = "User updated successfully",
            Data = new
            {
                user.id,
                user.name,
                user.email,
                user.mobile,
                user.company_id,
                user.isactive,
                user.m_user_type_id
            }
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error updating user",
            Error = ex.Message
        });
    }
}

// ======================================================
// REVOKE USER (DEACTIVATE + REVOKE ALL ISSUED LICENSES)
// ======================================================
[HttpPost("revokeUser")]
public async Task<IActionResult> RevokeUser([FromQuery] int userId)
{
    try
    {
        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);

        var user = await _db.tbl_user.FirstOrDefaultAsync(u => u.id == userId && u.isactive != 2);
        if (user == null)
            return NotFound(new { Status = 0, Message = "User not found" });

        if (targetCompanyId > 0 && user.company_id != targetCompanyId)
            return StatusCode(403, new { Status = 0, Message = "You are not authorized to revoke this user" });

        if (user.isactive == 0)
            return Ok(new { Status = 1, Message = "User is already revoked" });

        user.isactive = 0;

        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE tbl_company_user_license_issued
              SET status = 2
              WHERE tbl_user_id = {0} AND status <> 2",
            userId
        );

        await _db.SaveChangesAsync();

        return Ok(new { Status = 1, Message = "User revoked successfully" });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error revoking user",
            Error = ex.Message
        });
    }
}

// ======================================================
// DELETE USER (SOFT DELETE + REVOKE ALL ISSUED LICENSES)
// ======================================================
[HttpDelete("deleteUser")]
public async Task<IActionResult> DeleteUser([FromQuery] int userId)
{
    try
    {
        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);

        var user = await _db.tbl_user.FirstOrDefaultAsync(u => u.id == userId);
        if (user == null)
            return NotFound(new { Status = 0, Message = "User not found" });

        if (targetCompanyId > 0 && user.company_id != targetCompanyId)
            return StatusCode(403, new { Status = 0, Message = "You are not authorized to delete this user" });

        if (user.isactive == 2)
            return Ok(new { Status = 1, Message = "User is already deleted" });

        user.isactive = 2;

        await _db.Database.ExecuteSqlRawAsync(
            @"UPDATE tbl_company_user_license_issued
              SET status = 2
              WHERE tbl_user_id = {0} AND status <> 2",
            userId
        );

        await _db.SaveChangesAsync();

        return Ok(new { Status = 1, Message = "User deleted successfully" });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error deleting user",
            Error = ex.Message
        });
    }
}

// ======================================================
// UPDATE ISSUED LICENSE (VALID_TILL / STATUS)
// ======================================================
[HttpPut("updateIssuedLicense")]
public async Task<IActionResult> UpdateIssuedLicense([FromQuery] int licenseId, [FromBody] UpdateIssuedLicenseRequest request)
{
    try
    {
        if (request == null)
            return BadRequest(new { Status = 0, Message = "Invalid request data" });

        var hasFeaturePayload =
            request.features != null ||
            request.feature_list != null ||
            request.enabled_features != null ||
            request.permissions != null ||
            request.feature_codes != null ||
            request.features_csv != null;

        var requestedFeatures = LicenseFeatureService.ExtractFeaturesFromRequest(
            features: request.features,
            featureList: request.feature_list,
            enabledFeatures: request.enabled_features,
            permissions: request.permissions,
            featureCodes: request.feature_codes,
            featuresCsv: request.features_csv);
        if (hasFeaturePayload && requestedFeatures == null)
            requestedFeatures = new List<string>();

        if (!request.valid_till.HasValue && !request.status.HasValue && !hasFeaturePayload)
            return BadRequest(new { Status = 0, Message = "Provide at least one field: valid_till or status or features" });

        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);

        var license = await _db.tbl_company_user_license_issued
            .FirstOrDefaultAsync(l => l.id == licenseId);

        if (license == null)
            return NotFound(new { Status = 0, Message = "License not found" });

        // Company admin can update only own company license rows
        if (targetCompanyId > 0 && license.tbl_company_id != targetCompanyId)
            return StatusCode(403, new { Status = 0, Message = "You are not authorized to update this license" });

        if (request.valid_till.HasValue)
            license.valid_till = request.valid_till.Value;

        if (request.status.HasValue)
        {
            if (request.status.Value != 0 && request.status.Value != 1 && request.status.Value != 2)
                return BadRequest(new { Status = 0, Message = "Invalid status. Allowed values: 0, 1, 2" });

            license.status = request.status.Value;
        }

        await _db.SaveChangesAsync();

        if (requestedFeatures != null)
            await _licenseFeatureService.UpsertLicenseFeaturesAsync(license.id, requestedFeatures);

        var enabledFeatures = await _licenseFeatureService.GetFeaturesForLicenseIdsAsync(new[] { license.id });
        var features = enabledFeatures.TryGetValue(license.id, out var list) ? list : new List<string>();

        return Ok(new
        {
            Status = 1,
            Message = "License updated successfully",
            Data = new
            {
                license.id,
                license.tbl_company_id,
                license.tbl_user_id,
                license.license_code,
                license.valid_till,
                license.status,
                license.created_on,
                feature_codes = string.Join(",", features),
                enabled_features = features
            }
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error updating issued license",
            Error = ex.Message
        });
    }
}

public class UpdateCompanyUserRequest
{
    public string? name { get; set; }
    public string? email { get; set; }
    public string? mobile { get; set; }
    public string? country_code { get; set; }
    public string? isd_code { get; set; }
    public string? password { get; set; }
    public int? isactive { get; set; }
    public int? m_user_type_id { get; set; }
}

public class UpdateIssuedLicenseRequest
{
    public DateTime? valid_till { get; set; }
    public int? status { get; set; }
    public List<string>? features { get; set; }
    public List<string>? feature_list { get; set; }
    public List<string>? enabled_features { get; set; }
    public List<string>? permissions { get; set; }
    public string? feature_codes { get; set; }
    public string? features_csv { get; set; }
}
}}
