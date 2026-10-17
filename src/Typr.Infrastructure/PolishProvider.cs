namespace Typr.Infrastructure;

public enum PolishProvider { ChatGpt, Claude }
public enum ProviderConnectionState { Connected, SignInRequired, CliMissing, UnsupportedSubscription, Error }
public sealed record ProviderConnection(PolishProvider Provider, ProviderConnectionState State, string Message, string? AccountLabel = null);
public sealed record PolishModel(string Id, string DisplayName, IReadOnlyList<string> ReasoningEfforts, string DefaultReasoningEffort);
public sealed record ProviderSignIn(Uri? BrowserUri, string Instructions, string? LoginId = null);
public sealed record CliUpdateInfo(string? InstalledVersion, string LatestVersion, bool UpdateAvailable);

public sealed class PolishProviderException(ProviderConnectionState state, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ProviderConnectionState State { get; } = state;
}
