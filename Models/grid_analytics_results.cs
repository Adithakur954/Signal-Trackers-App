using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SignalTracker.Models
{
    [Table("grid_analytics_results")]
    public class grid_analytics_results
    {
        [Key]
        public int id { get; set; }

        [Column("project_id")]
        public int project_id { get; set; }

        [Column("region_id")]
        public int? region_id { get; set; }

        [Column("scenario_id")]
        public int? scenario_id { get; set; }

        [Column("grid_size_meters")]
        public double grid_size_meters { get; set; }

        [Column("grid_id")]
        [StringLength(50)]
        public string grid_id { get; set; } = "";

        [Column("center_lat")]
        public double center_lat { get; set; }

        [Column("center_lon")]
        public double center_lon { get; set; }

        [Column("min_lat")]
        public double min_lat { get; set; }

        [Column("max_lat")]
        public double max_lat { get; set; }

        [Column("min_lon")]
        public double min_lon { get; set; }

        [Column("max_lon")]
        public double max_lon { get; set; }

        [Column("baseline_point_count")]
        public int baseline_point_count { get; set; }

        [Column("optimized_point_count")]
        public int optimized_point_count { get; set; }

        // Baseline metrics
        [Column("baseline_avg_rsrp")]   public double? baseline_avg_rsrp { get; set; }
        [Column("baseline_avg_rsrq")]   public double? baseline_avg_rsrq { get; set; }
        [Column("baseline_avg_sinr")]   public double? baseline_avg_sinr { get; set; }
        [Column("baseline_median_rsrp")]public double? baseline_median_rsrp { get; set; }
        [Column("baseline_median_rsrq")]public double? baseline_median_rsrq { get; set; }
        [Column("baseline_median_sinr")]public double? baseline_median_sinr { get; set; }
        [Column("baseline_min_rsrp")]   public double? baseline_min_rsrp { get; set; }
        [Column("baseline_min_rsrq")]   public double? baseline_min_rsrq { get; set; }
        [Column("baseline_min_sinr")]   public double? baseline_min_sinr { get; set; }
        [Column("baseline_max_rsrp")]   public double? baseline_max_rsrp { get; set; }
        [Column("baseline_max_rsrq")]   public double? baseline_max_rsrq { get; set; }
        [Column("baseline_max_sinr")]   public double? baseline_max_sinr { get; set; }
        [Column("baseline_mode_rsrp")]  public double? baseline_mode_rsrp { get; set; }
        [Column("baseline_mode_rsrq")]  public double? baseline_mode_rsrq { get; set; }
        [Column("baseline_mode_sinr")]  public double? baseline_mode_sinr { get; set; }
        [Column("baseline_best_operator_avg")] public string? baseline_best_operator_avg { get; set; }
        [Column("baseline_best_operator_min")] public string? baseline_best_operator_min { get; set; }
        [Column("baseline_best_operator_max")] public string? baseline_best_operator_max { get; set; }

        // Optimized metrics
        [Column("optimized_avg_rsrp")]   public double? optimized_avg_rsrp { get; set; }
        [Column("optimized_avg_rsrq")]   public double? optimized_avg_rsrq { get; set; }
        [Column("optimized_avg_sinr")]   public double? optimized_avg_sinr { get; set; }
        [Column("optimized_median_rsrp")]public double? optimized_median_rsrp { get; set; }
        [Column("optimized_median_rsrq")]public double? optimized_median_rsrq { get; set; }
        [Column("optimized_median_sinr")]public double? optimized_median_sinr { get; set; }
        [Column("optimized_min_rsrp")]   public double? optimized_min_rsrp { get; set; }
        [Column("optimized_min_rsrq")]   public double? optimized_min_rsrq { get; set; }
        [Column("optimized_min_sinr")]   public double? optimized_min_sinr { get; set; }
        [Column("optimized_max_rsrp")]   public double? optimized_max_rsrp { get; set; }
        [Column("optimized_max_rsrq")]   public double? optimized_max_rsrq { get; set; }
        [Column("optimized_max_sinr")]   public double? optimized_max_sinr { get; set; }
        [Column("optimized_mode_rsrp")]  public double? optimized_mode_rsrp { get; set; }
        [Column("optimized_mode_rsrq")]  public double? optimized_mode_rsrq { get; set; }
        [Column("optimized_mode_sinr")]  public double? optimized_mode_sinr { get; set; }
        [Column("optimized_best_operator_avg")] public string? optimized_best_operator_avg { get; set; }
        [Column("optimized_best_operator_min")] public string? optimized_best_operator_min { get; set; }
        [Column("optimized_best_operator_max")] public string? optimized_best_operator_max { get; set; }

        // Difference metrics (optimized - baseline)
        [Column("diff_avg_rsrp")]   public double? diff_avg_rsrp { get; set; }
        [Column("diff_avg_rsrq")]   public double? diff_avg_rsrq { get; set; }
        [Column("diff_avg_sinr")]   public double? diff_avg_sinr { get; set; }
        [Column("diff_median_rsrp")]public double? diff_median_rsrp { get; set; }
        [Column("diff_median_rsrq")]public double? diff_median_rsrq { get; set; }
        [Column("diff_median_sinr")]public double? diff_median_sinr { get; set; }
        [Column("diff_min_rsrp")]   public double? diff_min_rsrp { get; set; }
        [Column("diff_min_rsrq")]   public double? diff_min_rsrq { get; set; }
        [Column("diff_min_sinr")]   public double? diff_min_sinr { get; set; }
        [Column("diff_max_rsrp")]   public double? diff_max_rsrp { get; set; }
        [Column("diff_max_rsrq")]   public double? diff_max_rsrq { get; set; }
        [Column("diff_max_sinr")]   public double? diff_max_sinr { get; set; }
        [Column("diff_mode_rsrp")]  public double? diff_mode_rsrp { get; set; }
        [Column("diff_mode_rsrq")]  public double? diff_mode_rsrq { get; set; }
        [Column("diff_mode_sinr")]  public double? diff_mode_sinr { get; set; }

        [Column("created_at")]
        public DateTime? created_at { get; set; }
    }
}
