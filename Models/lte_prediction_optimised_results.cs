using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SignalTracker.Models
{
    [Table("lte_prediction_optimised_results")]
    public class lte_prediction_optimised_results
    {
        [Key]
        public int id { get; set; }

        [Column("project_id")]
        public int project_id { get; set; }

        [Column("job_id")]
        public int? job_id { get; set; }

        [Column("lat")]
        public double lat { get; set; }

        [Column("lon")]
        public double lon { get; set; }

        [Column("pred_rsrp")]
        public double? pred_rsrp { get; set; }

        [Column("pred_rsrq")]
        public int? pred_rsrq { get; set; }

        [Column("pred_sinr")]
        public double? pred_sinr { get; set; }

        [Column("node_b_id")]
        [StringLength(255)]
        public string? node_b_id { get; set; }

        [Column("cell_id")]
        [StringLength(255)]
        public string? cell_id { get; set; }

        [Column("operator")]
        [StringLength(100)]
        public string? Operator { get; set; }

        [Column("created_at")]
        public DateTime? created_at { get; set; }

        [Column("site_id")]
        [StringLength(255)]
        public string? site_id { get; set; }

        [Column("nodeb_id_cell_id")]
        [StringLength(255)]
        public string? nodeb_id_cell_id { get; set; }
    }
}


