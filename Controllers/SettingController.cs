using System;
using System.Data;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SignalTracker.Helper;
using SignalTracker.Models;
using Microsoft.AspNetCore.Authorization;

namespace SignalTracker.Controllers
{
    
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SettingController : ControllerBase
    {
        private readonly ApplicationDbContext db;
        private readonly CommonFunction cf;

        public SettingController(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor)
        {
            db = context;
            cf = new CommonFunction(context, httpContextAccessor);
        }

        /// <summary>
        /// Check if session is valid (API replacement for SettingIndex view).
        /// </summary>
        // [HttpGet("CheckSession")]
         
        // public IActionResult CheckSession()
        // {
        //     if (!cf.SessionCheck())
        //     {
        //         return Unauthorized(new { Status = 0, Message = "Unauthorized" });
        //     }

        //     return Ok(new { Status = 1, Message = "Session valid" });
        // }

        /// <summary>
        /// Get threshold settings for logged-in user (or default).
        /// </summary>
private void EnsureMacDetailThresholdColumns()
{
    EnsureColumn("thresholds", "mac_dl_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_dl_delivered_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_ul_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_ul_delivered_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_bler_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_bler_init_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_mcs_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_retx_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_rb_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_grants_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_tx_power_json", "LONGTEXT NULL");
    EnsureColumn("thresholds", "mac_modulation_pct_json", "LONGTEXT NULL");
}

private void EnsureColumn(string tableName, string columnName, string columnDefinition)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != ConnectionState.Open;
    if (shouldClose)
        conn.Open();

    try
    {
        using var exists = conn.CreateCommand();
        exists.CommandText = @"
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = DATABASE()
              AND table_name = @tableName
              AND column_name = @columnName;";
        var tableParam = exists.CreateParameter();
        tableParam.ParameterName = "@tableName";
        tableParam.Value = tableName;
        exists.Parameters.Add(tableParam);
        var columnParam = exists.CreateParameter();
        columnParam.ParameterName = "@columnName";
        columnParam.Value = columnName;
        exists.Parameters.Add(columnParam);

        var count = Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE `{tableName.Replace("`", "``")}` ADD COLUMN `{columnName.Replace("`", "``")}` {columnDefinition};";
        alter.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
            conn.Close();
    }
}

[HttpGet("GetThresholdSettings")]
public IActionResult GetThresholdSettings()
{
    var response = new ReturnAPIResponse();

    try
    {
        cf.SessionCheck();
        int uid = cf.UserId;
        EnsureMacDetailThresholdColumns();

        // 1ï¸âƒ£ User-specific threshold (highest priority)
        var userSetting = db.thresholds
            .Where(x => x.user_id == uid && x.is_default == 0)
            .OrderByDescending(x => x.id)
            .FirstOrDefault();

        if (userSetting != null)
        {
            response.Status = 1;
            response.Data = userSetting;
            response.Message = "User threshold fetched.";
            return Ok(response);
        }

        // 2ï¸âƒ£ Global default threshold (user_id NULL or 0)
        var defaultSetting = db.thresholds
            .Where(x => x.is_default == 1 && (x.user_id == null || x.user_id == 0))
            .OrderByDescending(x => x.id)
            .FirstOrDefault();

        if (defaultSetting != null)
        {
            response.Status = 1;
            response.Data = defaultSetting;
            response.Message = "Default threshold fetched.";
            return Ok(response);
        }

        // 3ï¸âƒ£ Absolute fallback (first row)
        var fallback = db.thresholds
            .OrderBy(x => x.id)
            .FirstOrDefault();

        if (fallback != null)
        {
            response.Status = 1;
            response.Data = fallback;
            response.Message = "Fallback threshold returned.";
            return Ok(response);
        }

        response.Status = 0;
        response.Message = "No threshold configuration found.";
    }
    catch (Exception ex)
    {
        response.Status = 0;
        response.Message = "Error: " + SafeException.Get(ex);
    }

    return Ok(response);
}

[HttpPost("SaveThreshold")]
public IActionResult SaveThreshold([FromBody] thresholds model)
{
    var response = new ReturnAPIResponse();

    try
    {
        cf.SessionCheck();
        int uid = cf.UserId;
        EnsureMacDetailThresholdColumns();

        thresholds? existing = null;

        // Prefer explicit row id when client sends it (prevents updating stale/older rows).
        if (model?.id.HasValue == true && model.id.Value > 0)
        {
            existing = db.thresholds.FirstOrDefault(x =>
                x.id == model.id &&
                x.user_id == uid &&
                x.is_default == 0);
        }

        // Fallback to latest user-specific custom threshold row.
        if (existing == null)
        {
            existing = db.thresholds
                .Where(x => x.user_id == uid && x.is_default == 0)
                .OrderByDescending(x => x.id)
                .FirstOrDefault();
        }

        if (existing != null)
        {
            // UPDATE
            existing.rsrp_json = model.rsrp_json;
            existing.rsrq_json = model.rsrq_json;
            existing.sinr_json = model.sinr_json;
            existing.dl_thpt_json = model.dl_thpt_json;
            existing.ul_thpt_json = model.ul_thpt_json;
            existing.num_cells = model.num_cells;
            existing.level = model.level;
            existing.volte_call = model.volte_call;
            existing.lte_bler_json = model.lte_bler_json;
            existing.mos_json = model.mos_json;
            existing.coveragehole_json = model.coveragehole_json;
            existing.coveragehole_value = model.coveragehole_value;
            existing.tac= model.tac;
            existing.packet_loss = model.packet_loss;
            existing.jitter = model.jitter;
            existing.latency = model.latency;
              existing.dominance = model.dominance;
                existing.coverage_violation = model.coverage_violation;
                existing.delta_json = model.delta_json;
                existing.mac_dl_json = model.mac_dl_json;
                existing.mac_dl_delivered_json = model.mac_dl_delivered_json;
                existing.mac_ul_json = model.mac_ul_json;
                existing.mac_ul_delivered_json = model.mac_ul_delivered_json;
                existing.mac_bler_json = model.mac_bler_json;
                existing.mac_bler_init_json = model.mac_bler_init_json;
                existing.mac_mcs_json = model.mac_mcs_json;
                existing.mac_retx_json = model.mac_retx_json;
                existing.mac_rb_json = model.mac_rb_json;
                existing.mac_grants_json = model.mac_grants_json;
                existing.mac_tx_power_json = model.mac_tx_power_json;
                existing.mac_modulation_pct_json = model.mac_modulation_pct_json;
            db.thresholds.Update(existing);
        }
        else
        {
            // INSERT
            model.id = 0;
            model.user_id = uid;
            model.is_default = 0;

            db.thresholds.Add(model);
        }

        db.SaveChanges();

        var updated = db.thresholds
            .Where(x => x.user_id == uid && x.is_default == 0)
            .OrderByDescending(x => x.id)
            .FirstOrDefault();

        response.Status = 1;
        response.Data = updated;
        response.Message = "Threshold saved successfully.";
    }
    catch (Exception ex)
    {
        response.Status = 0;
        response.Message = "Error: " + SafeException.Get(ex);
    }

    return Ok(response);
}
   }}


