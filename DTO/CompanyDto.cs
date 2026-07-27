namespace SignalTracker.DTOs
{
    public class CompanyDto
    {
        public string country_code{ get; set;}
        public string  isd_code{get; set;}

        public int id { get; set; }
        public string? company_name { get; set; }
        public string? contact_person { get; set; }
        public string? mobile { get; set; }
        public string? email { get; set; }
        public string? address { get; set; }
        public string? pincode { get; set; }
        public string? gst_id { get; set; }
        public string? company_code { get; set; }
        public int? license_validity_in_months { get; set; }
        public int? total_granted_licenses { get; set; }
        public int? total_used_licenses { get; set; }
        public string? otp_phone_number { get; set; }
        public int? ask_for_otp { get; set; }
        public string? blacklisted_phone_number { get; set; }
        public string? remarks { get; set; }
        public DateTime? created_on { get; set; }
        public int? status { get; set; }
        public DateTime? last_login { get; set; }
    }
    public class CreateCompanyRequest
{
    public string company_name { get; set; }
    public string contact_person { get; set; }
    public string mobile { get; set; }
    public string email { get; set; }
    public string password { get; set; }
    public string address { get; set; }
    public string pincode { get; set; }
    public string gst_id { get; set; }
    public string company_code { get; set; }
     public string country_code{ get; set;}
      public string  isd_code{get; set;}

    public int license_validity_in_months { get; set; }
    public int total_granted_licenses { get; set; }
    public int total_used_licenses { get; set; }

    public string otp_phone_number { get; set; }
    public bool ask_for_otp { get; set; }

    public string blacklisted_phone_number { get; set; }
    public string remarks { get; set; }

    public bool status { get; set; }
}


public class SaveCompanyRequest
    {
        public string? password { get; set; }

public int total_used_licenses { get; set; }
        public int? id { get; set; }   // null = create, value = update
        public string company_name { get; set; }
        public string contact_person { get; set; }
        public string mobile { get; set; }
        public string email { get; set; }
        public string address { get; set; }
        public string pincode { get; set; }
        public string gst_id { get; set; }
        public int total_granted_licenses { get; set; }
        public int ask_for_otp { get; set; }
        public string blacklisted_phone_number { get; set; }
        public string country_code{ get; set;}
      public string  isd_code{get; set;}
        public string remarks { get; set; }
        public string otp_phone_number { get; set; }
        public int license_validity_in_months { get; set; }
        public int status { get; set; }
    }
public class GrantLicenseRequest
    {
        public int tbl_company_id { get; set; }
        public int granted_licenses { get; set; }
        public string country_code{ get; set;}
      public string  isd_code{get; set;}
        public decimal per_license_rate { get; set; }
        public string remarks { get; set; }
    }

    public class UpdateCompanyStatusRequest
    {
        public int status { get; set; }
    }
}

