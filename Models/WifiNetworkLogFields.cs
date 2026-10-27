using System.Text.Json.Serialization;

namespace SignalTracker.Models;

// Inherited by the network-log response so Wi-Fi fields stay at the row's root.
// Unknown/absent fields are omitted, including on cellular rows; false and zero remain valid values.
public class WifiNetworkLogFields
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_ssid { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_bssid { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_capabilities { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? wifi_rssi_dbm { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? wifi_frequency_mhz { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public long? wifi_timestamp { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_distance { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_distance_sd { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? wifi_passpoint { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? wifi_channel_bandwidth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? wifi_center_freq0_mhz { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? wifi_center_freq1_mhz { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_standard { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? wifi_80211mc_responder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? wifi_80211az_ntb_responder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? wifi_twt_responder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public List<WifiRadioChainInfo>? wifi_radio_chain_infos { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? wifi_interface_name { get; set; }
}

public sealed class WifiRadioChainInfo
{
    public int id { get; set; }
    public int level { get; set; }
}
