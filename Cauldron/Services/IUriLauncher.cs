namespace Cauldron.Services;

public interface IUriLauncher {
    /// <summary>Hands <paramref name="uri"/> to the OS; returns false if the OS could not open it.</summary>
    Task<bool> LaunchAsync(string uri);
}
