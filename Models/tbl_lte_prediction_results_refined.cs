using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SignalTracker.Models
{
    [Table("tbl_lte_prediction_results_refined")]
    public class tbl_lte_prediction_results_refined
    {
        [Key]
        public long id { get; set; }
        
        public long project_id { get; set; }
        
        [StringLength(64)]
        public string? job_id { get; set; }
        
        public double lat { get; set; }
        
        public double lon { get; set; }
        
        [StringLength(50)]
        public string? site_id { get; set; }
        
        public double? pred_rsrp_top2_avg { get; set; }
        
        public double? pred_rsrp_top3_avg { get; set; }
        
        public double? measured_dt_rsrp { get; set; }
        
        public DateTime? created_at { get; set; }
    }
}


