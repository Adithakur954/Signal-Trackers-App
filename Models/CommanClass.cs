using CsvHelper.Configuration.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace SignalTracker.Models
{
    public class LoggedInUser
    {
        public int UserID { get; set; }
        public string UserAgent { get; set; }
        public string IP { get; set; }
    }
    public class LoginData
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public string? country_code { get; set; }
        public bool? ForceLogin { get; set; }
        public string Captcha { get; set; }
        public string IP { get; set; }
    }
    public class ResetPasswordModel
    {
        public string Token { get; set; } = String.Empty;
        public string Email { get; set; } = String.Empty;
        public string NewPassword { get; set; } = String.Empty;
        public string Captcha { get; set; } = String.Empty;
    }
    public class ReturnMessage
    {
        public int Status { get; set; }
        public string Message { get; set; }
    }
    public class ReturnAPIResponse : ReturnMessage
    {
        public object Data { get; set; }
        public object token { get; set; }
        public int UserType { get; set; }
    }
    public class NetworkLogModel
    {

        [Name("Timestamp")]
        public string? Timestamp { get; set; }
        [Name("Latitude")]
        public string? Latitude { get; set; }
        [Name("Longitude")]
        public string? Longitude { get; set; }
        [Name("Battery Level")]
        public string? Battery { get; set; }        
        
        [Name("Network Type")]
        public string? Network { get; set; }
        [Name("Download Speed (KB/s)")]
        public string? dls { get; set; }
        [Name("Upload Speed (KB/s)")]
        public string? uls { get; set; }
        [Name("Call State")]
        public string? call_state { get; set; }
        public string? HotSpot { get; set; }
        [Name("Running Apps")]
        public string? Apps { get; set; }
        [Name("No of Cells")]
        public string? num_cells { get; set; }
        [Name("Alpha Long")]
        public string? m_alpha_long { get; set; }
        [Name("Alpha Short")]
        public string? m_alpha_short { get; set; }

        public string? CI { get; set; }
        public string? PCI { get; set; }
        public string? EARFCN { get; set; }
        public string? RSRP { get; set; }
        public string? RSRQ { get; set; }
        public string? SINR { get; set; }
        [Name("Total Rx (KB)")]
        public string? total_rx_kb { get; set; }
        [Name("Total Tx (KB)")]
        public string? total_tx_kb { get; set; }
        public string? MOS { get; set; }
        public string? Jitter { get; set; }
        public string? Latency { get; set; }
        [Name("Packet Loss")]
        public string? packet_loss { get; set; }
        [Name("DL THPT")]
        public string? dl_tpt { get; set; }
        [Name("UL THPT")]
        public string? ul_tpt { get; set; }
        [Name("VOLTE CALL")]
        public string? volte_call { get; set; }
        public string? BAND { get; set; }
        public string? CQI { get; set; }
        public string? BLER { get; set; }
        [Name("CellInfo_1")]
        public string? primary_cell_info_1 { get; set; }
        [Name("CellInfo_2")]
        public string? primary_cell_info_2 { get; set; }
        
    }
    public class PredictionDtatModel
    {
       
        public string? latitude { get; set; }
        public string? longitude { get; set; }
        public string? RSRP { get; set; }
        public string? RSRQ { get; set; }
        public string? SINR { get; set; }
        public string? ServingCell { get; set; }
        public string? azimuth { get; set; }
        public string? tx_power { get; set; }
        public string? height { get; set; }
        public string? band { get; set; }
        public string? earfcn { get; set; }
        public string? reference_signal_power { get; set; }
        public string? PCI { get; set; }
        public string? Mtilt { get; set; }
        public string? Etilt { get; set; }


    }
    public class SettingReangeColor
    {
        public string range { get; set; } = "None";
        public int min { get; set; }
        public int max { get; set; }
        public string color { get; set; } = "Red";
    }
    public class GraphStruct
    {
        public List<string> Category { get; set; }
        public List<GrapSeries> series { get; set; }
        public GraphStruct()
        {
            Category = new List<string>();
            series = new List<GrapSeries>();
        }
    }
    public class GrapSeries
    {
        public string name { get; set; } = "series1";
        public List<object> data { get; set; }
        public GrapSeries()
        {
            data = new List<object>();
        }
    }
    public class GeoJson
    {
        public string Type { get; set; }
        public List<Feature> Features { get; set; }
    }

    public class Feature
    {
        public Geometry Geometry { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public string Type { get; set; }
    }

    public class Geometry
    {
        public string Type { get; set; }
        public List<List<List<double>>> Coordinates { get; set; }  // For Polygon
    }

}


