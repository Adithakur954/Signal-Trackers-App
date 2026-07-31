using Microsoft.EntityFrameworkCore;
using SignalTracker.Services;
using static SignalTracker.Controllers.AdminController;
using static SignalTracker.Controllers.MapViewController;

using Microsoft.Extensions.DependencyInjection;

namespace SignalTracker.Models
{
    public class ApplicationDbContext : DbContext
    {
       private readonly IDbConnectionProvider? _connectionProvider;
        internal object tbl_lte_prediction_results;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }


protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    if (!optionsBuilder.IsConfigured && _connectionProvider != null)
    {
        var connectionString = _connectionProvider.GetConnectionString();
        var serverVersion = new MySqlServerVersion(new Version(8, 0, 29));

        optionsBuilder.UseMySql(connectionString, serverVersion, mysqlOptions =>
        {
            mysqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
        });
    }
}

        // ======= Users & Auth =======
        public DbSet<tbl_user> tbl_user => Set<tbl_user>();
        public DbSet<tbl_user_deletion_otp> tbl_user_deletion_otp => Set<tbl_user_deletion_otp>();
        public DbSet<tbl_user_deletion_token> tbl_user_deletion_token => Set<tbl_user_deletion_token>();
        public DbSet<tbl_user_deletion_audit> tbl_user_deletion_audit => Set<tbl_user_deletion_audit>();
        

       public DbSet<tbl_company> tbl_company { get; set; }
       public DbSet<tbl_lte_prediction_results> Tbl_lte_prediction_results { get; set; }
       public DbSet<tbl_lte_prediction_results_refined> tbl_lte_prediction_results_refined { get; set; }
       public DbSet<site_prediction_base> site_prediction_base { get; set; }
       public DbSet<lte_prediction_optimised_results> lte_prediction_optimised_results { get; set; }



       public DbSet<tbl_company_license_grant_history> tbl_company_license_grant_history { get; set; }

       public DbSet<tbl_company_user_license_issued> tbl_company_user_license_issued { get; set; }


        public DbSet<tbl_user_login_audit_details> tbl_user_login_audit_details => Set<tbl_user_login_audit_details>();
        public DbSet<m_user_type> m_user_type => Set<m_user_type>();
        public DbSet<m_email_setting> m_email_setting => Set<m_email_setting>();
        public DbSet<exception_history> exception_history => Set<exception_history>();

        // ======= Sessions & Logs =======
        public DbSet<tbl_session> tbl_session => Set<tbl_session>();
        public DbSet<tbl_network_log> tbl_network_log => Set<tbl_network_log>();
        public DbSet<tbl_network_log_neighbour> tbl_network_log_neighbour => Set<tbl_network_log_neighbour>();
        public DbSet<tbl_sub_session> tbl_sub_session => Set<tbl_sub_session>();

        // ======= Prediction & Thresholds =======
        public DbSet<tbl_prediction_data> tbl_prediction_data => Set<tbl_prediction_data>();
        public DbSet<site_prediction> site_prediction => Set<site_prediction>();
        public DbSet<site_prediction_optimized> site_prediction_optimized => Set<site_prediction_optimized>();
        public DbSet<thresholds> thresholds => Set<thresholds>(); // Kept this one, removed duplicate 'tbl_threshold'

        // ======= Projects, Regions, Uploads =======
        public DbSet<tbl_project> tbl_project => Set<tbl_project>();
        public DbSet<tbl_indoor_planning_floor> tbl_indoor_planning_floor => Set<tbl_indoor_planning_floor>();
        public DbSet<map_regions> map_regions => Set<map_regions>();
        public DbSet<tbl_upload_history> tbl_upload_history => Set<tbl_upload_history>();
        public DbSet<tbl_dashboard_cache> tbl_dashboard_cache { get; set; }
        public DbSet<grid_analytics_results> grid_analytics_results { get; set; }

        // ======= DTOs (Keyless Views) =======
        public DbSet<N78NeighbourDto> N78NeighbourDto { get; set; }
        public DbSet<TempPlainDto> TempPlainDto => Set<TempPlainDto>();
        public DbSet<KpiDistributionRow> KpiDistributionRows { get; set; }
        public DbSet<BandOpNetDto> BandOpNetDtos { get; set; }
        public DbSet<N78NeighbourSimpleDto> N78NeighbourSimpleDto { get; set; }
        public DbSet<LTE5GNeighbourDto> LTE5GNeighbourDto { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ---- 1. Tables & Keys ----

            modelBuilder.Entity<tbl_user>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_user");
                e.HasIndex(x => x.mobile).HasDatabaseName("ix_tbl_user_mobile");
                e.HasIndex(x => new { x.is_deleted, x.deletion_requested_at }).HasDatabaseName("ix_tbl_user_deletion_due");
            });

            modelBuilder.Entity<tbl_user_deletion_otp>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_user_deletion_otp");
                e.HasIndex(x => new { x.phone_number, x.created_at }).HasDatabaseName("ix_otp_phone_created");
                e.HasIndex(x => new { x.user_id, x.consumed_at, x.expires_at }).HasDatabaseName("ix_otp_user_active");
            });

            modelBuilder.Entity<tbl_user_deletion_token>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_user_deletion_token");
                e.HasIndex(x => x.token_hash).HasDatabaseName("ix_token_hash");
                e.HasIndex(x => new { x.user_id, x.expires_at }).HasDatabaseName("ix_token_user");
            });

            modelBuilder.Entity<tbl_user_deletion_audit>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_user_deletion_audit");
                e.HasIndex(x => new { x.user_id, x.created_at }).HasDatabaseName("ix_audit_user");
                e.HasIndex(x => new { x.phone_number, x.created_at }).HasDatabaseName("ix_audit_phone");
            });

            // ADDED THIS FOR COMPANY API
            modelBuilder.Entity<tbl_company>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_company");
            });




    // Force Entity Framework to map this model to the exact lowercase table name
    modelBuilder.Entity<tbl_lte_prediction_results>().ToTable("tbl_lte_prediction_results");

            modelBuilder.Entity<tbl_user_login_audit_details>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_user_login_audit_details");
            });

            modelBuilder.Entity<m_user_type>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("m_user_type");
            });

            modelBuilder.Entity<m_email_setting>(e =>
            {
                e.HasKey(x => x.ID);
                e.ToTable("m_email_setting");
            });

            modelBuilder.Entity<exception_history>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("exception_history");
            });

            modelBuilder.Entity<tbl_session>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_session");

                // a session can have many network log rows
                e.HasMany(s => s.network_logs)
                 .WithOne(n => n.session)
                 .HasForeignKey(n => n.session_id)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<tbl_network_log>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_network_log");
                e.Ignore(x => x.speed);

                // relationship configured from session side too
                e.HasOne(n => n.session)
                 .WithMany(s => s.network_logs)
                 .HasForeignKey(n => n.session_id);
            });

            modelBuilder.Entity<tbl_network_log_neighbour>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_network_log_neighbour");
                e.Ignore(x => x.speed);
            });

            modelBuilder.Entity<tbl_sub_session>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_sub_session");
            });

            modelBuilder.Entity<tbl_prediction_data>(entity =>
            {
                entity.ToTable("tbl_prediction_data");
                entity.HasKey(e => e.id);
                entity.Property(e => e.id).HasColumnName("id");
                entity.Property(e => e.tbl_project_id).HasColumnName("tbl_project_id");
                entity.Property(e => e.lat).HasColumnName("lat");
                entity.Property(e => e.lon).HasColumnName("lon");
                entity.Property(e => e.band).HasMaxLength(50).HasColumnName("band");
                entity.Property(e => e.earfcn).HasMaxLength(50).HasColumnName("earfcn");
                entity.Property(e => e.network).HasMaxLength(100).HasColumnName("network");
                entity.Property(e => e.rsrp).HasColumnName("rsrp");
                entity.Property(e => e.rsrq).HasColumnName("rsrq");
                entity.Property(e => e.sinr).HasColumnName("sinr");
                entity.Property(e => e.timestamp).HasColumnName("timestamp");
            });

            modelBuilder.Entity<thresholds>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("thresholds");
                e.Ignore("2g");
                e.Ignore("3g");
                e.Ignore("4g");
                e.Ignore("5g");
            });

            modelBuilder.Entity<site_prediction>(e =>
            {
                e.HasKey(x => x.id);
                // Already has [Table] attribute in class definition
            });

            modelBuilder.Entity<site_prediction_optimized>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("site_prediction_optimized");
            });

            modelBuilder.Entity<site_prediction_base>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("lte_prediction_baseline_results");
            });

            modelBuilder.Entity<lte_prediction_optimised_results>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("lte_prediction_optimised_results");
            });

            modelBuilder.Entity<tbl_project>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_project");
            });

            modelBuilder.Entity<tbl_indoor_planning_floor>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_indoor_planning_floor");
                e.HasIndex(x => x.updated_at).HasDatabaseName("ix_indoor_planning_floor_updated_at");
            });

            modelBuilder.Entity<map_regions>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("map_regions");
            });

            modelBuilder.Entity<tbl_upload_history>(e =>
            {
                e.HasKey(x => x.id);
                e.ToTable("tbl_upload_history");
            });

            modelBuilder.Entity<tbl_dashboard_cache>()
                .HasIndex(x => x.cache_key)
                .IsUnique();

            // ---- 2. Keyless DTOs (Views/Raw SQL results) ----

            modelBuilder.Entity<N78NeighbourSimpleDto>().HasNoKey().ToView(null);
            modelBuilder.Entity<N78NeighbourDto>().HasNoKey().ToView(null);
            modelBuilder.Entity<ThroughputDto>().HasNoKey().ToView(null);
            modelBuilder.Entity<HandsetDistResult>().HasNoKey().ToView(null);
            modelBuilder.Entity<KpiDistributionRow>().HasNoKey().ToView(null);
            modelBuilder.Entity<OpNetValueDto>().HasNoKey().ToView(null);
            modelBuilder.Entity<TempPlainDto>().HasNoKey().ToView(null);
            modelBuilder.Entity<BandOpNetDto>(entity => { entity.HasNoKey(); entity.ToView(null); });
        }
    }
}


