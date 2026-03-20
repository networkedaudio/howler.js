using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RTPTransmitter.Services;

/// <summary>
/// Singleton service that enumerates available network interfaces and
/// holds the user's runtime NIC selection. Services that need the local
/// address for multicast binding should read <see cref="SelectedAddress"/>
/// instead of the static config value.
/// </summary>
public sealed class NetworkInterfaceService
{
    private readonly ILogger<NetworkInterfaceService> _logger;
    private string _selectedAddress = "0.0.0.0";

    /// <summary>
    /// Fired when the user selects a different network interface.
    /// </summary>
    public event Action? OnSelectionChanged;

    /// <summary>
    /// The currently selected local IP address for multicast binding.
    /// </summary>
    public string SelectedAddress
    {
        get => _selectedAddress;
        private set
        {
            if (_selectedAddress != value)
            {
                _selectedAddress = value;
                _logger.LogInformation("Network interface selection changed to {Address}", value);
                OnSelectionChanged?.Invoke();
            }
        }
    }

    public NetworkInterfaceService(ILogger<NetworkInterfaceService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Set the selected network interface by IP address.
    /// </summary>
    public void Select(string ipAddress)
    {
        SelectedAddress = ipAddress;
    }

    /// <summary>
    /// Returns all available IPv4 unicast addresses grouped by NIC.
    /// </summary>
    public IReadOnlyList<NicInfo> GetAvailableInterfaces()
    {
        var results = new List<NicInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel)
                continue;

            var ipProps = nic.GetIPProperties();
            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                results.Add(new NicInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    IpAddress = addr.Address.ToString(),
                    InterfaceType = nic.NetworkInterfaceType.ToString(),
                    Speed = nic.Speed,
                    SupportsMulticast = nic.SupportsMulticast
                });
            }
        }

        return results;
    }
}

/// <summary>
/// Describes a single IPv4 address on a network interface.
/// </summary>
public sealed class NicInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string IpAddress { get; init; }
    public required string InterfaceType { get; init; }
    public long Speed { get; init; }
    public bool SupportsMulticast { get; init; }

    /// <summary>
    /// Friendly display string for the dropdown.
    /// </summary>
    public string DisplayName => $"{Name} — {IpAddress} ({Description})";
}
