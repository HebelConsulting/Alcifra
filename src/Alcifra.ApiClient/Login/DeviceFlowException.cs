namespace Alcifra.ApiClient.Login;

/// <summary>
/// A device authorization that could not be completed, carrying the sentence to show the person.
/// </summary>
/// <remarks>
/// The shared flow is UI-free and knows nothing of any host's own exception type (alcifra's <c>CliException</c>,
/// apass's), so it throws this and each tool translates it. The message is the contract: it is what the user reads.
/// </remarks>
public sealed class DeviceFlowException(string message) : Exception(message);
