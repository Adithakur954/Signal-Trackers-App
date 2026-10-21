namespace SignalTracker.Services;

public enum LoginSessionState { Current, Missing, Replaced, Unavailable }

public interface ILoginSessionStore
{
    Task<LoginSessionState> ValidateAndRenewAsync(string key, string expectedValue, int ttlSeconds,
        bool allowExpired, CancellationToken cancellationToken = default);
}
