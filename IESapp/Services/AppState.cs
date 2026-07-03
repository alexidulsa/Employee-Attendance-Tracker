using System.Net.NetworkInformation;
using System.Reflection;

namespace IESapp.Services;

public class AppState
{
    public string CurrentUserEmail { get; private set; } = string.Empty;
    public bool IsLoggedIn => !string.IsNullOrEmpty(CurrentUserEmail);

    /// <summary>True when logged in via the admin credentials sheet (not Google OAuth).</summary>
    public bool IsAdmin { get; private set; } = false;

    /// <summary>Unique session identifier: IESapp-v1.0_MAC_d-M-yyyy</summary>
    public string SessionId { get; } = GenerateSessionId();

    public event Action? OnChange;

    public void Login(string email, bool isAdmin = false)
    {
        CurrentUserEmail = email;
        IsAdmin = isAdmin;
        NotifyStateChanged();
    }

    public void Logout()
    {
        CurrentUserEmail = string.Empty;
        IsAdmin = false;
        NotifyStateChanged();
    }

    public event Action? OnTimeLogsUpdated;
    public void NotifyTimeLogsUpdated() => OnTimeLogsUpdated?.Invoke();

    public event Action? OnEmployeesUpdated;
    public void NotifyEmployeesUpdated() => OnEmployeesUpdated?.Invoke();

    private void NotifyStateChanged() => OnChange?.Invoke();

    private static string GenerateSessionId()
    {
        var mac = "00:00:00:00:00:00";
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ?? "unknown";
        
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            if (nic != null)
            {
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
        }
        catch { }

        var date = DateTime.Now.ToString("MM-dd-yyyy");
        return $"IESapp-v{version}_{mac}_{date}";
    }
}
