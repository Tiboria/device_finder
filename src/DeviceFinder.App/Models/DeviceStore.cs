using DeviceFinder.App.Services;

namespace DeviceFinder.App.Models;

/// <summary>
/// Registre central des équipements détectés, identifiés par MAC.
/// Thread-safe : les services d'écoute appellent Report() depuis des threads d'arrière-plan.
/// </summary>
public sealed class DeviceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly OuiLookup _oui;

    public DeviceStore(OuiLookup oui)
    {
        _oui = oui;
    }

    /// <summary>Levée quand un nouvel équipement apparaît (thread d'arrière-plan).</summary>
    public event Action<Device>? DeviceAdded;

    /// <summary>Levée quand un équipement connu change d'état (thread d'arrière-plan).</summary>
    public event Action<Device>? DeviceUpdated;

    /// <param name="mac">MAC au format AA:BB:CC:DD:EE:FF ou AABBCCDDEEFF.</param>
    /// <param name="ip">IP source observée, éventuellement nulle ou 0.0.0.0 (probe ARP).</param>
    /// <param name="forcedMode">Mode d'adressage imposé par la source (ex. bail du mini-serveur DHCP).</param>
    public void Report(string mac, string? ip, AddressingMode? forcedMode = null)
    {
        var key = NormalizeMac(mac);
        if (key is null)
        {
            return;
        }

        Device? device;
        bool added;
        bool changed;
        lock (_gate)
        {
            if (!_devices.TryGetValue(key, out device!))
            {
                device = new Device(key, _oui.LookupVendor(key));
                _devices[key] = device;
                added = true;
            }
            else
            {
                added = false;
            }
            changed = device.ApplyObservation(ip, forcedMode);
        }

        if (added)
        {
            DeviceAdded?.Invoke(device);
        }
        else if (changed)
        {
            DeviceUpdated?.Invoke(device);
        }
    }

    public IReadOnlyList<Device> GetAll()
    {
        lock (_gate)
        {
            return _devices.Values.OrderByDescending(d => d.LastSeen).ToArray();
        }
    }

    public Device? Find(string mac)
    {
        var key = NormalizeMac(mac);
        if (key is null)
        {
            return null;
        }
        lock (_gate)
        {
            return _devices.TryGetValue(key, out var device) ? device : null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _devices.Clear();
        }
    }

    /// <summary>Normalise une MAC en AA:BB:CC:DD:EE:FF, ou null si invalide.</summary>
    internal static string? NormalizeMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }
        var hex = mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        if (hex.Length != 6 * 2 || !hex.All(c => Uri.IsHexDigit(c)))
        {
            return null;
        }
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
