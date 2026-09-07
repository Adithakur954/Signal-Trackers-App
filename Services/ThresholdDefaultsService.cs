using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class ThresholdDefaultsService
{
    private readonly ApplicationDbContext _db;

    public ThresholdDefaultsService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task EnsureForUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));

        var alreadyExists = await _db.thresholds
            .AsNoTracking()
            .AnyAsync(x => x.user_id == userId && x.is_default == 0, cancellationToken);
        if (alreadyExists)
            return;

        _db.thresholds.Add(CreateDefaults(userId));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static thresholds CreateDefaults(int userId)
    {
        return new thresholds
        {
            user_id = userId,
            is_default = 0,
            rsrp_json = Ranges(
                (-105, -95, "#c88546", "default"), (-95, -85, "#e7ff70", "default"),
                (-85, -75, "#00ff00", "default"), (-140, -105, "#b12f2f", "default"),
                (-137, -117, "#ff0000", "5g"), (-117, -97, "#e1ff00", "5g"),
                (-97, -77, "#304630", "5g"), (-77, 3, "#00ff00", "5g"),
                (-140, -105, "#ff0000", "4g"), (-105, -95, "#ffa200", "4g"),
                (-95, -85, "#cee713", "4g"), (-85, 3, "#00ff00", "4g")),
            rsrq_json = Ranges(
                (-14, -11, "#e56161", "default"), (-11, -8, "#FFFF00", "default"),
                (-8, -4, "#90EE90", "default"), (-4, 4, "#006400", "default")),
            sinr_json = Ranges(
                (1, 4, "#dfdb62", "default"), (4, 8, "#d38c3c", "default"),
                (8, 16, "#75ea99", "default"), (16, 31, "#429444", "default")),
            c_i_json = Ranges(
                (18, 99, "#00c853", "Good"), (12, 18, "#ffd600", "Fair"),
                (9, 12, "#ff9100", "Poor"), (-99, 9, "#d50000", "Bad")),
            dl_thpt_json = Ranges(
                (7.65, 11.05, "#7a87e6", "default"), (11.05, 15.3, "#e0ee77", "default"),
                (15.3, 23.8, "#84d78c", "default"), (23.8, 40.8, "#3a6942", "default")),
            ul_thpt_json = Ranges(
                (1.7, 2.55, "#FF0000", "default"), (2.55, 3.4, "#0000FF", "default"),
                (3.4, 4.25, "#FFFF00", "default"), (4.25, 10.2, "#00ff00", "default")),
            delta_json = Ranges(
                (-2.5, -1.5, "#ff3300", "default"), (-1.5, -0.5, "#ff006f", "default"),
                (-0.5, 0.5, "#528e61", "default"), (0.5, 1.5, "#00ff00", "default")),
            lte_bler_json = Ranges(
                (0, 2.4, "#006400", "Excellent"), (2.4, 6, "#90EE90", "Good"),
                (6, 12, "#FFD700", "Fair"), (12, 20, "#FF7A00", "Poor")),
            mos_json = Ranges(
                (0, 2.85, "#FF0000", "default"), (2.85, 3.8, "#FFFF00", "default"),
                (3.8, 4.75, "#0000FF", "default")),
            mac_dl_json = Ranges((0, 10, "#00ff00", "default"), (10, 50, "#00ff00", "default")),
            mac_dl_delivered_json = Ranges((0, 5, "#ff2600", "default"), (5, 10, "#ffa200", "default"), (10, 20, "#f1ff33", "default"), (20, 40, "#00ff00", "default")),
            mac_ul_json = Ranges((0, 1, "#FF0000", "Very Low"), (1, 3, "#FF7A00", "Low"), (3, 5, "#FFD700", "Fair"), (5, 10, "#90EE90", "Good")),
            mac_ul_delivered_json = Ranges((0, 5, "#FF0000", "Very Low"), (5, 10, "#FF7A00", "Low"), (10, 20, "#FFD700", "Fair"), (20, 40, "#90EE90", "Good")),
            mac_bler_json = Ranges((0, 2.4, "#006400", "Excellent"), (2.4, 6, "#90EE90", "Good"), (6, 12, "#FFD700", "Fair"), (12, 20, "#FF7A00", "Poor")),
            mac_bler_init_json = Ranges((0, 2.4, "#006400", "Excellent"), (2.4, 6, "#90EE90", "Good"), (6, 12, "#FFD700", "Fair"), (12, 20, "#FF7A00", "Poor")),
            mac_mcs_json = Ranges((0, 5, "#FF0000", "Very Low"), (5, 10, "#FF7A00", "Low"), (10, 15, "#FFD700", "Fair"), (15, 22, "#90EE90", "Good")),
            mac_retx_json = Ranges((0, 5, "#006400", "Excellent"), (5, 10, "#90EE90", "Good"), (10, 20, "#FFD700", "Fair"), (20, 30, "#FF7A00", "Poor")),
            mac_rb_json = Ranges((0, 40, "#006400", "Low Load"), (40, 60, "#90EE90", "Normal"), (60, 75, "#FFD700", "Moderate"), (75, 90, "#FF7A00", "High")),
            mac_grants_json = Ranges((0, 10, "#FF0000", "Very Low"), (10, 50, "#FF7A00", "Low"), (50, 100, "#FFD700", "Moderate"), (100, 500, "#90EE90", "High")),
            mac_tx_power_json = Ranges((-50, -10, "#006400", "Excellent"), (-10, 0, "#90EE90", "Good"), (0, 10, "#FFD700", "Fair"), (10, 20, "#FF7A00", "High")),
            mac_modulation_pct_json = Ranges((0, 25, "#FF0000", "Poor"), (25, 50, "#FF7A00", "Fair"), (50, 75, "#FFD700", "Good"), (75, 90, "#90EE90", "Very Good")),
            volte_call = Ranges((0, 0, "#FF0000", "Failed"), (1, 1, "#006400", "Connected")),
            coveragehole_json = "-105",
            coveragehole_value = -105,
            jitter = Ranges((-6, 6, "#00ff00", "default"), (6, 18, "#fff700", "default"), (18, 54, "#ff1900", "default")),
            latency = Ranges((-9.6, -3.6, "#034903", "default"), (-3.6, 2.4, "#c3f782", "default"), (2.4, 14.4, "#2d16da", "default"), (14.4, 26.4, "#eedd6d", "default")),
            packet_loss = Ranges((0, 1, "#006400", "Excellent"), (1, 2.5, "#90EE90", "Good"), (2.5, 5, "#FFD700", "Fair"), (5, 10, "#FF7A00", "Poor")),
            tac = Ranges((0, 16777215, "#006400", "Valid TAC")),
            dominance = Ranges((0, 0.9, "#00ff00", "default"), (0.9, 1.8, "#00ff2a", "default"), (1.8, 2.7, "#00ff55", "default"), (2.7, 3.6, "#ffd500", "default")),
            coverage_violation = Ranges((-15, -10, "#00ff00", "default"), (-10, -5, "#ffd500", "default"), (-5, -2, "#ff2600", "default"), (-2, -1, "#4766c2", "default"))
        };
    }

    private static string Ranges(params (double Min, double Max, string Color, string Range)[] values)
    {
        return JsonSerializer.Serialize(values.Select(x => new
        {
            range = x.Range,
            min = x.Min,
            max = x.Max,
            color = x.Color
        }));
    }
}
