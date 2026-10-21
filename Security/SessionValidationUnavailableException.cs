namespace SignalTracker.Security;

public sealed class SessionValidationUnavailableException : Exception
{
    public SessionValidationUnavailableException(Exception? inner = null)
        : base("Session validation is temporarily unavailable.", inner) { }
}
