namespace SignalTracker.Configuration;

public static class HostingConfiguration
{
    public static string ResolveDataProtectionKeysPath(WebApplicationBuilder builder)
    {
        var configuredPath = builder.Configuration["DataProtection:KeysPath"]
                             ?? Environment.GetEnvironmentVariable("DATAPROTECTION_KEYS_PATH");

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Environment.ExpandEnvironmentVariables(configuredPath);
        }
        else if (builder.Environment.IsDevelopment())
        {
            configuredPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
        }
        else
        {
            configuredPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SignalTracker",
                "DataProtectionKeys");
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredPath));
    }

    public static int? GetHttpsRedirectionPort(IConfiguration configuration)
    {
        var configuredPort = configuration.GetValue<int?>("HttpsRedirection:HttpsPort");
        if (configuredPort.HasValue) return configuredPort;

        var envPort = Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT")
                      ?? Environment.GetEnvironmentVariable("HTTPS_PORT");

        return int.TryParse(envPort, out var parsedPort) ? parsedPort : null;
    }
}


