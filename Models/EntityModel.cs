using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace SignalTracker.Models
{
    // ========================= Users & Auth =========================
    public class tbl_user
    {
        internal object app_version;
        internal DateTime signup_date;

        public  string country_code{ get; set;}
         public string  isd_code{get; set;}
        public int id { get; set; }
        public string? uid { get; set; }
        public string? token { get; set; }
        public string name { get; set; } = string.Empty;
        public string? password { get; set; }
        public string? email { get; set; }

        public string? make { get; set; }
        public string? model { get; set; }
        public string? os { get; set; }
        public string? operator_name { get; set; }

        public int? company_id { get; set; }
        public string? mobile { get; set; }
        public int isactive { get; set; }
        public int m_user_type_id { get; set; }
        public DateTime? last_login { get; set; }
        public DateTime? date_created { get; set; }

        public string? device_id { get; set; }
        public string? gcm_id { get; set; }
        public bool is_deleted { get; set; }
        public DateTime? deletion_requested_at { get; set; }
    }

    public class tbl_user_deletion_otp
    {
        public long id { get; set; }
        public int user_id { get; set; }
        public string phone_number { get; set; } = string.Empty;
        public string otp_hash { get; set; } = string.Empty;
        public DateTime expires_at { get; set; }
        public int attempt_count { get; set; }
        public int max_attempts { get; set; } = 5;
        public DateTime resend_available_at { get; set; }
        public DateTime? consumed_at { get; set; }
        public DateTime? blocked_at { get; set; }
        public DateTime created_at { get; set; }
    }

    public class tbl_user_deletion_token
    {
        public long id { get; set; }
        public int user_id { get; set; }
        public string phone_number { get; set; } = string.Empty;
        public string token_hash { get; set; } = string.Empty;
        public DateTime expires_at { get; set; }
        public DateTime? used_at { get; set; }
        public DateTime created_at { get; set; }
    }

    public class tbl_user_deletion_audit
    {
        public long id { get; set; }
        public int? user_id { get; set; }
        public string? phone_number { get; set; }
        public string event_type { get; set; } = string.Empty;
        public string event_status { get; set; } = string.Empty;
        public string? ip_address { get; set; }
        public string? user_agent { get; set; }
        public string? message { get; set; }
        public DateTime created_at { get; set; }
    }

    public class tbl_user_login_audit_details
    {
        public int id { get; set; }
        public string username { get; set; } = string.Empty;
        public string ip_address { get; set; } = string.Empty;
        public int login_status { get; set; }
        public DateTime date_of_creation { get; set; }
    }

public class AddSitePredictionModel
{
    [JsonPropertyName("projectId")]
    public int ProjectId { get; set; }

    [JsonPropertyName("site")]
    public string? Site { get; set; }

    [JsonPropertyName("nodeId")]
    public string? NodeId { get; set; }

    [JsonPropertyName("node_id")]
    public string? NodeIdSnake { get; set; }

    [JsonPropertyName("nodeb_id")]
    public string? NodeBId { get; set; }

    [JsonPropertyName("cluster")]
    public string? Cluster { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("operator")]
    public string? Operator { get; set; }

    [JsonPropertyName("operatorName")]
    public string? OperatorName { get; set; }

    [JsonPropertyName("operator_name")]
    public string? OperatorNameSnake { get; set; }

    [JsonPropertyName("bands")]
    public List<string> Bands { get; set; } = new();

    [JsonPropertyName("sectors")]
    public List<int> Sectors { get; set; } = new();

    [JsonPropertyName("cellIds")]
    public List<int> CellIds { get; set; } = new();

    [JsonPropertyName("cell_ids")]
    public List<int> CellIdsSnake { get; set; } = new();

    [JsonPropertyName("azimuths")]
    public List<int> Azimuths { get; set; } = new();

    [JsonPropertyName("heights")]
    public List<double> Heights { get; set; } = new();

    [JsonPropertyName("mechanicalTilts")]
    public List<double> MechanicalTilts { get; set; } = new();

    [JsonPropertyName("electricalTilts")]
    public List<double> ElectricalTilts { get; set; } = new();

    [JsonPropertyName("technology")]
    public string? Technology { get; set; }

    [JsonPropertyName("technologies")]
    public List<TechnologyData> Technologies { get; set; } = new();

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

public class TechnologyData
{
    [JsonPropertyName("technology")]
    public string? Technology { get; set; }

    [JsonPropertyName("idValues")]
    public List<int> IdValues { get; set; } = new();

    [JsonPropertyName("earfcn")]
    public string? Earfcn { get; set; }
}
    public class m_user_type
    {
        public int id { get; set; }
        public string type { get; set; } = string.Empty;
        public int m_status_id { get; set; }
    }

    public class m_email_setting
    {
        public int ID { get; set; }
        public string SMTPServer { get; set; } = string.Empty;
        public string SMTPPort { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public bool SSLayer { get; set; }
        public string received_email_on { get; set; } = string.Empty;
        public int m_Status_ID { get; set; }
        public DateTime Date_of_Creation { get; set; }
    }

    public class exception_history
    {
        public int id { get; set; }
        public int user_id { get; set; }
        public string source_file { get; set; } = string.Empty;
        public string page { get; set; } = string.Empty;
        public string exception { get; set; } = string.Empty;
        public DateTime exception_date { get; set; }
    }

    // ========================= Sessions =========================
    public class tbl_session
    {
        public decimal rsrp;
        public decimal rsrq;
        public decimal sinr;

        public int? id { get; set; }
        public int user_id { get; set; }
        public DateTime? start_time { get; set; }
        public DateTime? end_time { get; set; }
        public float? start_lat { get; set; }
        public float? start_lon { get; set; }
        public float? end_lat { get; set; }
        public float? end_lon { get; set; }
        public float? distance { get; set; }
        public int? capture_frequency { get; set; }
        public string? type { get; set; }
        public string? notes { get; set; }
        public string? start_address { get; set; }
        public string? end_address { get; set; }
        public DateTime? uploaded_on { get; set; }
        public string? tbl_upload_id { get; set; }
        public string? ps_json { get; set; }
        public string? cs_json { get; set; }


        // navigation property for network log rows linked to this session
        public List<tbl_network_log> network_logs { get; set; } = new();
    }

    public class SessionWithUserDTO
    {
        public int id { get; set; }
        public int user_id { get; set; }
        public DateTime? start_time { get; set; }
        public DateTime? end_time { get; set; }
        public float? start_lat { get; set; }
        public float? start_lon { get; set; }
        public float? end_lat { get; set; }
        public float? end_lon { get; set; }
        public float? distance { get; set; }
        public int? capture_frequency { get; set; }
        public string? type { get; set; }
        public string? notes { get; set; }
        public string? start_address { get; set; }
        public string? end_address { get; set; }
        public DateTime? uploaded_on { get; set; }
        public string? tbl_upload_id { get; set; }
        public string? name { get; set; }
        public string? mobile { get; set; }
        public string? make { get; set; }
        public string? model { get; set; }
        public string? os { get; set; }
        public string? operator_name { get; set; }
    }


public class tbl_company
    {
        public int id { get; set; }
        public string? company_name { get; set; }
        public string? contact_person { get; set; }
        public string country_code{ get; set;}
        public string  isd_code{get; set;}
        public string? mobile { get; set; }
        public string? email { get; set; }
        public string? password { get; set; }
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
        public string? token { get; set; }
        public string? uid { get; set; }

        
    }

    // ========================= LTE-5G Neighbour DTO =========================

public class tbl_company_user_license_issued
{
    public int id { get; set; }

    public int tbl_company_id { get; set; }

    public int tbl_user_id { get; set; }

    public string? license_code { get; set; }

    public DateTime valid_till { get; set; }

    public DateTime created_on { get; set; }

    public int status { get; set; }
}

public class tbl_company_license_grant_history
    {
        public int id { get; set; }

        public int tbl_company_id { get; set; }   // FK

        public int granted_licenses { get; set; }

        public  int  per_license_rate { get; set; }

        [NotMapped]
        public string? license_code { get; set; }

        public string? remarks { get; set; }

        public DateTime date_time { get; set; }

        public int status { get; set; }
    }
 [Keyless]
    public class LTE5GNeighbourDto
{
    public int id { get; set; }
    public int session_id { get; set; }
    public DateTime timestamp { get; set; }
    public double lat { get; set; }
    public double lon { get; set; }
    public string indoor_outdoor { get; set; }
    public string provider { get; set; }

    // Primary KPIs (Added ? for null safety)
    public string primary_network { get; set; }
    public string primary_band { get; set; }
    public string primary_pci { get; set; }
    public double? primary_rsrp { get; set; }
    public double? primary_rsrq { get; set; }
    public double? primary_sinr { get; set; }
    public double? mos { get; set; }
    public decimal? dl_tpt { get; set; }
    public decimal? ul_tpt { get; set; }

    // Neighbour KPIs (Added ? for null safety)
    public string neighbour_network { get; set; }
    public string neighbour_provider { get; set; }
    public string neighbour_band { get; set; }
    public string neighbour_pci { get; set; }
    public double? neighbour_rsrp { get; set; }
    public double? neighbour_rsrq { get; set; }
    public double? neighbour_sinr { get; set; }
    public decimal? neighbour_dl_tpt { get; set; }
    public decimal? neighbour_ul_tpt { get; set; }
}

    // ========================= Network Logs =========================
    public class tbl_network_log
    {
        internal string? band_resolved;
        internal object created_by;

        public int id { get; set; }
        
        public int? level{ get; set; }
         
          [NotMapped]
         public double? dl_tpt_num { get; set; }
    
     [NotMapped]
     public double? ul_tpt_num { get; set; }

         public string? network_id { get; set; }
        public string? indoor_outdoor { get; set; }
         public string?nodeb_id { get;set;}
          public string? cell_id{get;set;}
     
        [NotMapped]
        public float? speed{ get; set;}


        public  int?   session_id { get; set; }
        // relationship back to session
        public tbl_session? session { get; set; }
        public DateTime? timestamp { get; set; }
        public float? lat { get; set; }
        public float? lon { get; set; }
        public float? altitude { get; set; }
        public int? battery { get; set; }
        public string? dls { get; set; }
        public string? uls { get; set; }
        public string? call_state { get; set; }
        public string? hotspot { get; set; }
        public string? apps { get; set; }
        public int? num_cells { get; set; }
        public string? network { get; set; }
        public int? m_mcc { get; set; }
        public int? m_mnc { get; set; }
        public string? m_alpha_long { get; set; }
        public string? m_alpha_short { get; set; }
        public string? mci { get; set; }
        public string? pci { get; set; }
        public string? tac { get; set; }
        public string? earfcn { get; set; }
        public float? rssi { get; set; }
        public float? rsrp { get; set; }
        public float? rsrq { get; set; }
        public float? sinr { get; set; }
        public string? total_rx_kb { get; set; }
        public string? total_tx_kb { get; set; }
        public float? mos { get; set; }
        public float? jitter { get; set; }
        public float? latency { get; set; }
        public float? packet_loss { get; set; }
        public string? dl_tpt { get; set; }
        public string? ul_tpt { get; set; }
        public string? volte_call { get; set; }
        public string? band { get; set; }
        public float? cqi { get; set; }
        public string? bler { get; set; }
        public string? primary_cell_info_1 { get; set; }
        public string? primary_cell_info_2 { get; set; }
        public string? primary_cell_info_3 { get; set; }
        public string? all_neigbor_cell_info { get; set; }
        public string? image_path { get; set; }
        public int? polygon_id { get; set; }
        public int? company_id { get; set; }
        public string? ta { get; set; }
        public int? mcc { get; set; }
        public int? mnc { get; set; }
        public string? gps_fix_type { get; set; }
        public float? gps_hdop { get; set; }
        public float? gps_vdop { get; set; }
        public string? phone_antenna_gain { get; set; }
        public float? csi_rsrp { get; set; }
        public float? csi_rsrq { get; set; }
        public float? csi_sinr { get; set; }
        public string? primary { get; set; }
        public string? app_name { get; set; }
        public string? bw { get; set; }
        public long? tbl_sub_session_ps_id { get; set; }
        public long? tbl_sub_session_cs_id { get; set; }
        public string? extra_json { get; set; }


         public float? Speed {get;set;}
    }


[Keyless] 
public class KpiDistributionRow
{      public long value { get; set; }    
    public int count { get; set; }
    public double percentage { get; set; }
    public int cumulative_count { get; set; }
}

public class N78NeighbourSimpleDto
{
    public int session_id { get; set; }
    public DateTime? timestamp { get; set; }
    public double? lat { get; set; }
    public double? lon { get; set; }

    public string provider { get; set; }
    public string primary_network { get; set; }
    public string primary_band { get; set; }

    // âœ… KPIs from PRIMARY table
    public double? rsrp { get; set; }
    public double? rsrq { get; set; }
    public double? sinr { get; set; }
    public double? mos { get; set; }
    public double? dl_tpt { get; set; }
    public double? ul_tpt { get; set; }

    public string neighbour_network { get; set; }
    public string neighbour_band { get; set; }
}


  public class tbl_network_log_neighbour
    {
        internal string? band_resolved;
          public  string primary{get;set;}

        public int id { get; set; }
         
          [NotMapped]
         public double? dl_tpt_num { get; set; }
    
     [NotMapped]
     public double? ul_tpt_num { get; set; }

         public string? network_id { get; set; }
        public string? indoor_outdoor { get; set; }
         public string?nodeb_id { get;set;}
          public string cell_id{get;set;}
     
         [NotMapped]
         public float? speed{ get; set;}


        public  int   session_id { get; set; }
        public DateTime? timestamp { get; set; }
        public float? lat { get; set; }
        public float? lon { get; set; }
        public float? altitude { get; set; }
        public int? battery { get; set; }
        public string? dls { get; set; }
        public string? uls { get; set; }
        public string? call_state { get; set; }
        public string? hotspot { get; set; }
        public string? apps { get; set; }
        public int? num_cells { get; set; }
        public string? network { get; set; }
        public int? m_mcc { get; set; }
        public int? m_mnc { get; set; }
        public string? m_alpha_long { get; set; }
        public string? m_alpha_short { get; set; }
        public string? mci { get; set; }
        public string? pci { get; set; }
        public string? tac { get; set; }
        public string? earfcn { get; set; }
        public float? rssi { get; set; }
        public float? rsrp { get; set; }
        public float? rsrq { get; set; }
        public float? sinr { get; set; }
        public string? total_rx_kb { get; set; }
        public string? total_tx_kb { get; set; }
        public float? mos { get; set; }
        public float? jitter { get; set; }
        public float? latency { get; set; }
        public float? packet_loss { get; set; }
        public string? dl_tpt { get; set; }
        public string? ul_tpt { get; set; }
        public string? volte_call { get; set; }
        public string? band { get; set; }
        public float? cqi { get; set; }
        public string? bler { get; set; }
        public string? primary_cell_info_1 { get; set; }
        public string? primary_cell_info_2 { get; set; }
        public string? all_neigbor_cell_info { get; set; }
        public string? image_path { get; set; }
        public int? polygon_id { get; set; }
        public long? tbl_sub_session_ps_id { get; set; }
        public long? tbl_sub_session_cs_id { get; set; }
        public string? extra_json { get; set; }
         public float? Speed {get;set;}
    }

    public class tbl_sub_session
    {
        public long id { get; set; }
        public int? user_id { get; set; }
        public int? session_id { get; set; }
        public int? sub_session_id { get; set; }
        public byte? type { get; set; }
        public DateTime? start_time { get; set; }
        public DateTime? end_time { get; set; }
        public string? json_data { get; set; }
        public byte? status { get; set; }
        public double? start_lat { get; set; }
        public double? start_lon { get; set; }
        public double? end_lat { get; set; }
        public double? end_lon { get; set; }
    }

public class LatLonDistributionDto
{
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int Count { get; set; }
    public decimal Percentage { get; set; }
    public int CumulativeCount { get; set; }
    public decimal CumulativePercentage { get; set; }
}

public class N78NeighbourDto
    {
        public int id { get; set; }
        public int? session_id { get; set; }
                public DateTime? timestamp { get; set; }

        public double? lat { get; set; }
        public double? lon { get; set; }

        public string? indoor_outdoor { get; set; }
        public string? network { get; set; }
        public string? network_type { get; set; }
        public string? m_alpha_long { get; set; }

        public double? rsrp { get; set; }
        public double? rsrq { get; set; }
        public double? sinr { get; set; }
        public double? mos { get; set; }

        public double? neighbour_lat { get; set; }
        public double? neighbour_lon { get; set; }
        public string? neighbour_band { get; set; }

        public double distance_meters { get; set; }
    }


public class IndoorOutdoorSessionFilter
{
    public string? IndoorOutdoor { get; set; }   // "INDOOR" or "OUTDOOR"
    public string? SessionIds { get; set; }      // "101,102,103"
    public string? sessionIds { get; set; }
    public string? session_ids { get; set; }
    public string? session_Ids { get; set; }
    public string? sessionId { get; set; }
    public string? Operator { get; set; }       // OPTIONAL (JIO / AIRTEL / VI)
    public string? Technology { get; set; }     // OPTIONAL (4G / 5G / NSA / SA)

    public string? GetRawSessionIds()
    {
        return new[] { SessionIds, sessionIds, session_ids, session_Ids, sessionId }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}


public class IndoorOutdoorLogDto
{
    public int session_id { get; set; }
    public DateTime? timestamp { get; set; }
    public string? indoor_outdoor { get; set; }
    public string? operator_name { get; set; }

    public float? rsrp { get; set; }
    public float? rsrq { get; set; }
    public float? sinr { get; set; }
    public float? mos { get; set; }

    public string? dl_tpt { get; set; }   // string in DB
    public string? ul_tpt { get; set; }   // string in DB

    public string? apps { get; set; }

    public string? band { get; set; }
    public string? network { get; set; }
    public string? primary_cell_info_1 { get; set; }
   
}


      public class tbl_dashboard_cache
       {
    public int id { get; set; }
    public string cache_key { get; set; } = null!;
    public string json_data { get; set; } = null!;
    public DateTime last_updated { get; set; }
     }

    public class log_network
    {
        public string? timestamp { get; set; }
        public string? lat { get; set; }
        public string? lon { get; set; }
        public string? battery { get; set; }
        public string? dls { get; set; }
        public string? uls { get; set; }
        public string? call_state { get; set; }
        public string? hotspot { get; set; }
        public string? apps { get; set; }
        public string? num_cells { get; set; }
        public string? network { get; set; }
        public string? m_mcc { get; set; }
        public string? m_mnc { get; set; }
        public string? m_alpha_long { get; set; }
        public string? m_alpha_short { get; set; }
        public string? mci { get; set; }
        public string? pci { get; set; }
        public string? tac { get; set; }
        public string? earfcn { get; set; }
        public string? rssi { get; set; }
        public string? rsrp { get; set; }
        public string? rsrq { get; set; }
        public string? sinr { get; set; }
        public string? total_rx_kb { get; set; }
        public string? total_tx_kb { get; set; }
        public string? mos { get; set; }
        public string? jitter { get; set; }
        public string? latency { get; set; }
        public string? packet_loss { get; set; }
        public string? dl_tpt { get; set; }
        public string? ul_tpt { get; set; }
        public string? volte_call { get; set; }
        public string? band { get; set; }
        public string? cqi { get; set; }
        public string? bler { get; set; }
        public string? primary_cell_info_1 { get; set; }
        public string? primary_cell_info_2 { get; set; }
        public string? all_neigbor_cell_info { get; set; }
        public string? image_path { get; set; }
        public string? polygon_id { get; set; }
        public string?nodeb_id { get;set;}
    }

    // ========================= Prediction Data =========================
    public class PredictionPointDto
    {
        public int tbl_project_id { get; set; }
        public double? lat { get; set; }
        public double? lon { get; set; }
        public double? rsrp { get; set; }
        public double? rsrq { get; set; }
        public double? sinr { get; set; }
        public string? band { get; set; }
        public string? earfcn { get; set; }
    }

    public class tbl_prediction_data
    {
        internal object mos;

        public int id { get; set; }
        // IMPORTANT: project id MUST be int? (not float?)
        public int? tbl_project_id { get; set; }
        public String? network{ get; set; }
        public float? lat { get; set; }
        public float? lon { get; set; }
        public float? rsrp { get; set; }
        public float? rsrq { get; set; }
        public float? sinr { get; set; }
        public string? serving_cell { get; set; }
        public string? azimuth { get; set; }
        public string? tx_power { get; set; }
        public string? height { get; set; }
        public string? band { get; set; }
        public string? earfcn { get; set; }
        public string? reference_signal_power { get; set; }
        public string? pci { get; set; }
        public string? mtilt { get; set; }
        public string? etilt { get; set; }
        public DateTime? timestamp { get; set; }
        
    }

    public class PredictionLogQuery
    {
        public int? projectId { get; set; }
        public string? token { get; set; }
        public DateTime? fromDate { get; set; }
        public DateTime? toDate { get; set; }
        public string? providers { get; set; }
        public string? technology { get; set; }
        public string? metric { get; set; } = "RSRP";
        public bool isBestTechnology { get; set; }
        public string? Band { get; set; }
        public string? EARFCN { get; set; }
        public string? State { get; set; }
        public int pointsInsideBuilding { get; set; } = 0;
        public bool loadFilters { get; set; } = false;
        public JsonElement? coverageHoleJson { get; set; }
        public double? coverageHole { get; set; }
    }


[Table("tbl_lte_prediction_results")] // Forces exact table name
    public class tbl_lte_prediction_results
    {
        [Key]
        [Column("id", TypeName = "bigint unsigned")]
        public ulong Id { get; set; }

        [Column("job_id", TypeName = "varchar(64)")]
        public string JobId { get; set; } = string.Empty;

        [Column("lat")]
        public double Lat { get; set; }

        [Column("lon")]
        public double Lon { get; set; }

        // THESE ARE THE CRITICAL FIXES:
        [Column("pred_rsrp")] 
        public double? PredRsrp { get; set; }

        [Column("pred_rsrq")]
        public double? PredRsrq { get; set; }

        [Column("pred_sinr")]
        public double? PredSinr { get; set; }

        [Column("project_id", TypeName = "bigint")]
        public long ProjectId { get; set; }

        [Column("site_id", TypeName = "varchar(50)")]
        public string? SiteId { get; set; }

        [Column("created_at", TypeName = "timestamp")]
        public DateTime? CreatedAt { get; set; }
    }
    [Table("site_prediction")]
    public class site_prediction
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
      
      
    // primary key
    public int id { get; set; }

    public int tbl_project_id { get; set; }
    public int tbl_upload_id { get; set; }

    public int? site { get; set; }
    public int? site_name { get; set; }
    public string? sector { get; set; }
    public string? cell_id { get; set; }
    public double? longitude { get; set; }
    public double? latitude { get; set; }
    public int? tac { get; set; }
    public int? pci { get; set; }
    public int? azimuth { get; set; }
    public int? height { get; set; }
    public int? bw { get; set; }
    public int? m_tilt { get; set; }
    public int? e_tilt { get; set; }
    [Column("tx_power")]
    public double? maximum_transmission_power_of_resource { get; set; }
    public double? real_transmit_power_of_resource { get; set; }
    public double? reference_signal_power { get; set; }
    public string? frequency { get; set; }
    public int? band { get; set; }
    public int? earfcn { get; set; }

    // IP network log fields
    public string? Timestamp { get; set; }
    public string? SourceIP { get; set; }
    public string? DestinationIP { get; set; }
    public string? SourcePort { get; set; }
    public string? DestinationPort { get; set; }
    public string? Protocol { get; set; }
    public string? PacketSize { get; set; }
    public string? Flags { get; set; }
    public string? TimeToLive { get; set; }
    public string? Length { get; set; }
    public string? Info { get; set; }

    // phone / app metrics â€“ store as string for now
    public string? Battery { get; set; }
    public string? Network { get; set; }
    public string? dls { get; set; }
    public string? uls { get; set; }
    public string? total_rx_kb { get; set; }
    public string? total_tx_kb { get; set; }
    public string? HotSpot { get; set; }
    public string? Apps { get; set; }
    public string? MOS { get; set; }

    public string? RSRP { get; set; }
    public string? RSRQ { get; set; }
    public string? SINR { get; set; }

    public string? cluster { get; set; }
    public string? site_color { get; set; }
    public string? technology { get; set; }
}

    [Table("site_prediction_optimized")]
    public class site_prediction_optimized
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int id { get; set; }

        public int site_prediction_id { get; set; }
        public int scenario { get; set; }

        public int tbl_project_id { get; set; }
        public int tbl_upload_id { get; set; }

        public int? site { get; set; }
        public int? site_name { get; set; }
        public string? sector { get; set; }
        public string? cell_id { get; set; }
        public int? sec_id { get; set; }
        public double? longitude { get; set; }
        public double? latitude { get; set; }
        public int? tac { get; set; }
        public int? pci { get; set; }
        public int? azimuth { get; set; }
        public int? height { get; set; }
        public int? bw { get; set; }
        public int? m_tilt { get; set; }
        public int? e_tilt { get; set; }
        [Column("tx_power")]
        public double? maximum_transmission_power_of_resource { get; set; }
        public double? real_transmit_power_of_resource { get; set; }
        public double? reference_signal_power { get; set; }
        public string? cellsize { get; set; }
        public string? frequency { get; set; }
        public int? band { get; set; }
        public string? uplink_center_frequency { get; set; }
        public string? downlink_frequency { get; set; }
        public int? earfcn { get; set; }

        public string? Timestamp { get; set; }
        public string? SourceIP { get; set; }
        public string? DestinationIP { get; set; }
        public string? SourcePort { get; set; }
        public string? DestinationPort { get; set; }
        public string? Protocol { get; set; }
        public string? PacketSize { get; set; }
        public string? Flags { get; set; }
        public string? TimeToLive { get; set; }
        public string? Length { get; set; }
        public string? Info { get; set; }

        public string? Battery { get; set; }
        public string? Network { get; set; }
        public string? dls { get; set; }
        public string? uls { get; set; }
        public string? total_rx_kb { get; set; }
        public string? total_tx_kb { get; set; }
        public string? HotSpot { get; set; }
        public string? Apps { get; set; }
        public string? MOS { get; set; }

        public string? RSRP { get; set; }
        public string? RSRQ { get; set; }
        public string? SINR { get; set; }

        public string? cluster { get; set; }
        public string? technology { get; set; }

        public bool is_updated { get; set; } = true;
        public int version { get; set; } = 1;
        public string? status { get; set; } = "updated";
        public DateTime? created_at { get; set; }
        public DateTime? updated_at { get; set; }
        public string? updated_by { get; set; }
    }

    // ========================= Projects & Regions =========================
    public class tbl_project
    {
        internal object created_by;

        public int id { get; set; }
        public string? Download_path { get; set; }

        public int? company_id { get; set; }
        public string? project_name { get; set; }
        public string? ref_session_id { get; set; }
        public string? from_date { get; set; }
        public string? to_date { get; set; }
        public string? provider { get; set; }
        public string? tech { get; set; }
        public string? band { get; set; }
        public string? earfcn { get; set; }
        public string? apps { get; set; }
        public DateTime? created_on { get; set; }
        public DateTime? ended_on { get; set; }
        public int? created_by_user_id { get; set; }
        public string? created_by_user_name { get; set; }
        public int status { get; set; }
        public string? log_grid { get; set; }
    }

    public class tbl_indoor_planning_floor
    {
        public int id { get; set; }
        public string project_name { get; set; } = string.Empty;
        public string floor_name { get; set; } = "Level 1";
        public string? plan_json { get; set; }
        public int? created_by_user_id { get; set; }
        public string? created_by_user_name { get; set; }
        public DateTime created_at { get; set; } = DateTime.UtcNow;
        public DateTime updated_at { get; set; } = DateTime.UtcNow;
    }

    public class tbl_savepolygon
    {
        public int id { get; set; }
        public string? name { get; set; }
    }

    public class PolygonLogFilter
    {
        public int PolygonId { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int Limit { get; set; } = 20000;
    }

    public class tbl_upload_history
    {
        // DB schema no longer has session_id in tbl_upload_history.
        // Keep this for backward compatibility in code paths that might still read it,
        // but prevent EF from mapping it to SQL columns.
        [NotMapped]
        public int? session_id { get; set; }

        public int id { get; set; }
        public DateTime uploaded_on { get; set; }
        public int file_type { get; set; }
        [Column(TypeName = "longtext")]
        public string file_name { get; set; } = string.Empty;
        public int uploaded_by { get; set; }
        public string? remarks { get; set; }
        [Column(TypeName = "longtext")]
        public string? errors { get; set; }
        public short status { get; set; }
        [Column(TypeName = "longtext")]
        public string polygon_file { get; set; } = string.Empty;
    }

    public class PolygonMatch { public int id { get; set; } }
    public class PolygonDto
    {
        public int id { get; set; }
        public string name { get; set; } = string.Empty;
        public string? wkt { get; set; }
    }

    public class thresholds
    {

        public string? num_cells { get; set; }
      
         public string? dominance  { get; set; }
         public string? coverage_violation { get; set; }
        public int? id { get; set; }
        public int? user_id { get; set; }
        
    
public string? level { get; set; }
    public string? coveragehole_json { get; set; }

    public string? tac{ get; set; }

    public string? jitter { get; set; }

    public string? packet_loss { get; set; }

    public string? latency { get; set; }

        public string? rsrp_json { get; set; }
        public string? rsrq_json { get; set; }
        public string? sinr_json { get; set; }
        public string? dl_thpt_json { get; set; }
        public string? ul_thpt_json { get; set; }
        public string? volte_call { get; set; }
        public string? lte_bler_json { get; set; }
        public string? mos_json { get; set; }

        public int? is_default { get; set; }

      
        public double? coveragehole_value { get; set; }
        public string? delta_json { get; set; }
    }

[Table("tbl_company")]
public class Company
{
    [Key]
    public int id { get; set; }

    public string company_name { get; set; }
    public string contact_person { get; set; }
    public string mobile { get; set; }
    public string email { get; set; }
    public string password { get; set; }
    public string address { get; set; }
    public string pincode { get; set; }
    public string gst_id { get; set; }
    public string company_code { get; set; }

    public int license_validity_in_months { get; set; }
    public int total_granted_licenses { get; set; }
    public int total_used_licenses { get; set; }

    public string otp_phone_number { get; set; }
    public bool ask_for_otp { get; set; }

    public string blacklisted_phone_number { get; set; }
    public string remarks { get; set; }

    public DateTime created_on { get; set; }
    public bool status { get; set; }

    public DateTime? last_login { get; set; }
    public string token { get; set; }
    public string uid { get; set; }
}
public class TempPlainDto
    {
        public int tbl_project_id { get; set; }
        public double? lat { get; set; }
        public double? lon { get; set; }
        public double? rsrp { get; set; }
        public double? rsrq { get; set; }
        public double? sinr { get; set; }
        public string? band { get; set; }
        public string? earfcn { get; set; }
    }
    public class PolygonMatchDto { public int id { get; set; } }
public class CreateProjectRequest
{
    public string ProjectName { get; set; } = string.Empty;

    // multiple session IDs can be sent, we'll store as comma string in ref_session_id
    public List<int>? SessionIds { get; set; }

    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    // send polygon as WKT string:
    // "POLYGON((lon lat, lon lat, lon lat, lon lat))"
    public string? PolygonWkt { get; set; }

    public string? Provider { get; set; }
    public string? Tech { get; set; }
    public string? Band { get; set; }
    public string? Earfcn { get; set; }
    public string? Apps { get; set; }

    // audit fields
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public int? CreatedUserType { get; set; } = 1;

    // optional: if you want to pre-set ended_on or status
    public DateTime? EndedOn { get; set; }
    public int? Status { get; set; } = 1;
}

    public class SavedPolygonListDto
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string? wkt { get; set; }
        public long? project_id { get; set; }
        public decimal? area { get; set; }
    }

    public class SiteNoMlDto
    {
        public long id { get; set; }
        public string? network { get; set; }
        public double? earfcn_or_narfcn { get; set; }
        public int? site_key_inferred { get; set; }
        public double? pci_or_psi { get; set; }
        public int? samples { get; set; }
        public double? lat_pred { get; set; }
        public double? lon_pred { get; set; }
        public int? azimuth_deg_5 { get; set; }
        public int? azimuth_deg_5_soft { get; set; }
        public string? azimuth_deg_label_soft { get; set; }
        public double? azimuth_adjustment_deg { get; set; }
        public double? template_spacing_deg { get; set; }
        public int? beamwidth_deg_est { get; set; }
        public double? median_sample_distance_m { get; set; }
        public int? cell_id_representative { get; set; }
        public double? sector_count { get; set; }
        public double? azimuth_reliability { get; set; }
        public string? spacing_used { get; set; }
    }

    public class SiteMlDto : SiteNoMlDto { }

    public class SitePredictionDto
    {
        public long id { get; set; }
        public int? site { get; set; }
        public int? site_name { get; set; }
        public string? sector { get; set; }
        public int? cell_id { get; set; }
        public int? sec_id { get; set; }
        public double? longitude { get; set; }
        public double? latitude { get; set; }
        public int? tac { get; set; }
        public int? pci { get; set; }
        public int? azimuth { get; set; }
        public int? height { get; set; }
        public int? bw { get; set; }
        public int? m_tilt { get; set; }
        public int? e_tilt { get; set; }
        public double? maximum_transmission_power_of_resource { get; set; }
        public double? real_transmit_power_of_resource { get; set; }
        public double? reference_signal_power { get; set; }
        public string? cellsize { get; set; }
        public string? frequency { get; set; }
        public int? band { get; set; }
        public string? uplink_center_frequency { get; set; }
        public string? downlink_frequency { get; set; }
        public int? earfcn { get; set; }
        public string? cluster { get; set; }
        public int? tbl_project_id { get; set; }
        public int? tbl_upload_id { get; set; }
        public string? Technology { get; set; }
    }

    public class map_regions
    {
        public int id { get; set; }
        public int? tbl_project_id { get; set; }
        public string? name { get; set; }
        public byte[] region { get; set; } = Array.Empty<byte>();
        public float? area { get; set; }
        public int status { get; set; }
        public string? session_id { get; set; }
        [NotMapped]
        public int? created_by_user_id { get; set; }
    }


    public class DateRangeLogItem
{
    public int id { get; set; }
    public int session_id { get; set; }
    public double? lat { get; set; }
    public double? lon { get; set; }
    public double? rsrp { get; set; }
    public double? rsrq { get; set; }
    public double? sinr { get; set; }
    public string? network { get; set; }
    public string? band { get; set; }
    public string? pci { get; set; }
    public DateTime? timestamp { get; set; }
    public string? provider { get; set; }
    public string? dl_tpt { get; set; }
    public string? ul_tpt { get; set; }
    public double? mos { get; set; }
    public int? polygon_id { get; set; }
    public string? image_path { get; set; }
    public string? nodeb_id { get; set; }
    public int neighbour_count { get; set; }
    public string? apps { get; set; }
    public string? app_name { get; set; }
    public string? radio { get; set; }
    public string? mode { get; set; }
    public bool is4g { get; set; }
    public bool is5g { get; set; }
    public bool isNsa { get; set; }
            public float? Speed { get; internal set; }
        }

public class AppSummaryResult
{
    public string appName { get; set; } = "";
    public int SampleCount { get; set; }
    public double avgRsrp { get; set; }
    public double avgRsrq { get; set; }
    public double avgSinr { get; set; }
    public double avgMos { get; set; }
    public double avgDl { get; set; }
    public double avgUl { get; set; }
    public int durationSeconds { get; set; }
    public string durationHHMMSS { get; set; } = "00:00:00";
}

public class OperatorTechTimeFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }

    public string? Operator { get; set; }      // Airtel / Jio / etc
    public string? Technology { get; set; }    // 4G / 5G / NSA / SA
}

public class ProviderNetworkTime
{
    public string Provider { get; set; }
    public string Network { get; set; }
    public double TimeSeconds { get; set; }
}
public class SessionIdsRequest
{
    public List<int> SessionIds { get; set; }
}
public  sealed class AppAgg
{
    public string AppName { get; }
    public int SampleCount { get; set; }

    public double RsrpSum { get; set; }
    public int RsrpCnt { get; set; }

    public double RsrqSum { get; set; }
    public int RsrqCnt { get; set; }

    public double SinrSum { get; set; }
    public int SinrCnt { get; set; }

    public double MosSum { get; set; }
    public int MosCnt { get; set; }

    public double JitterSum { get; set; }
    public int JitterCnt { get; set; }

    public double LatencySum { get; set; }
    public int LatencyCnt { get; set; }

    public double PacketLossSum { get; set; }
    public int PacketLossCnt { get; set; }

    public double DlSum { get; set; }
    public int DlCnt { get; set; }

    public double UlSum { get; set; }
    public int UlCnt { get; set; }

public DateTime? PreviousTimestamp { get; set; }
public int ActiveDurationSeconds   { get; set; }

    public DateTime? FirstTimestamp { get; set; }
    public DateTime? LastTimestamp  { get; set; }
    // remove  grid size 

    public AppAgg(string name) { AppName = name; }
}

       public class MapFilter
{
    public int session_id { get; set; }
    public string? NetworkType { get; set; } // This now refers to Provider
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int page { get; set; } = 1;
    public int limit { get; set; } = 50000;
}

public class LogFilterModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public TimeSpan? StartTime { get; set; }   // Time picker (HH:mm or HH:mm:ss)
    public TimeSpan? EndTime { get; set; } 
    public string? Provider { get; set; }
    public int? PolygonId { get; set; }
    public DateTime? CursorTs { get; set; }
}

   public class SavePolygonModel
        {
            public int? ProjectId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string WKT { get; set; } = string.Empty;
            public List<int>? SessionIds { get; set; } = new();
        }

        

    
}
