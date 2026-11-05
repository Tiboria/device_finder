using System.ComponentModel;
using System.Net;
using System.Text.Json.Serialization;
using DeviceFinder.App.Services;

namespace DeviceFinder.App.Models;

public enum AddressingMode
{
    Unknown,
    Fixed,
    Apipa,
    Dhcp,
}

/// <summary>
/// Un équipement détecté, identifié par son adresse MAC.
/// Les propriétés liées à l'UI notifient leurs changements ; les mises à jour
/// peuvent venir de threads d'arrière-plan (WPF marshale les notifications scalaires).
/// </summary>
public sealed class Device : INotifyPropertyChanged
{
    private const int MaxIpHistory = 50;

    private string? _currentIp;
    private AddressingMode _mode = AddressingMode.Unknown;
    private DateTime _lastSeen;
    private bool _reachable;
    private bool _reachabilityKnown;
    /// <summary>Vrai dès qu'une adresse a été attribuée par le mini-serveur DHCP intégré.</summary>
    private bool _modeFromDhcp;

    public Device(string mac, string vendor)
    {
        Mac = mac;
        Vendor = string.IsNullOrWhiteSpace(vendor) ? Loc.Get("Device.VendorUnknown") : vendor;
        FirstSeen = DateTime.Now;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>MAC normalisée au format AA:BB:CC:DD:EE:FF (clé d'identité).</summary>
    public string Mac { get; }

    public string Vendor { get; }

    public DateTime FirstSeen { get; }

    /// <summary>Dernière IP non nulle observée, toutes sources confondues.</summary>
    public string? CurrentIp
    {
        get => _currentIp;
        private set
        {
            if (SetField(ref _currentIp, value))
            {
                OnPropertyChanged(nameof(IpDisplay));
            }
        }
    }

    /// <summary>Historique des IP vues (IP actuelle exclue), la plus récente en dernier.</summary>
    public IReadOnlyList<string> IpHistory => _ipHistory;

    private readonly List<string> _ipHistory = new();

    public AddressingMode Mode
    {
        get => _mode;
        private set
        {
            if (SetField(ref _mode, value))
            {
                OnPropertyChanged(nameof(ModeDisplay));
            }
        }
    }

    public DateTime LastSeen
    {
        get => _lastSeen;
        private set
        {
            if (SetField(ref _lastSeen, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public bool Reachable
    {
        get => _reachable;
        private set => SetField(ref _reachable, value);
    }

    /// <summary>Faux tant qu'aucun ping n'a encore été effectué.</summary>
    public bool ReachabilityKnown
    {
        get => _reachabilityKnown;
        private set => SetField(ref _reachabilityKnown, value);
    }

    [JsonIgnore] public string IpDisplay => CurrentIp ?? "—";
    [JsonIgnore] public string ModeDisplay => Mode switch
    {
        AddressingMode.Fixed => Loc.Get("Device.ModeFixed"),
        AddressingMode.Apipa => Loc.Get("Device.ModeApipa"),
        AddressingMode.Dhcp => Loc.Get("Device.ModeDhcp"),
        _ => Loc.Get("Device.ModeUnknown"),
    };
    [JsonIgnore] public string LastSeenText => LastSeen.ToString("HH:mm:ss");
    [JsonIgnore] public string StatusText => !ReachabilityKnown ? "—"
        : Reachable ? Loc.Get("Device.Reachable") : Loc.Get("Device.Offline");
    [JsonIgnore] public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            if (!ReachabilityKnown) return System.Windows.Media.Brushes.Gray;
            return Reachable ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
        }
    }

    /// <summary>
    /// Applique une observation (IP source d'un broadcast, champ sender d'une trame ARP…).
    /// Retourne vrai si l'état a changé de manière visible.
    /// <paramref name="forcedMode"/> : mode imposé par la source d'observation (ex. DHCP
    /// pour un bail attribué par le mini-serveur intégré) ; le mode DHCP reste ensuite
    /// sticky : les observations suivantes ne le requalifient pas en « Fixe ».
    /// </summary>
    public bool ApplyObservation(string? ip, AddressingMode? forcedMode = null)
    {
        LastSeen = DateTime.Now;
        bool changed = false;

        if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0")
        {
            if (!string.Equals(CurrentIp, ip, StringComparison.Ordinal))
            {
                if (CurrentIp is not null && !_ipHistory.Contains(CurrentIp))
                {
                    _ipHistory.Add(CurrentIp);
                    if (_ipHistory.Count > MaxIpHistory)
                    {
                        _ipHistory.RemoveAt(0);
                    }
                }
                CurrentIp = ip;
                changed = true;
            }
            var deduced = DeduceMode(ip);
            if (forcedMode == AddressingMode.Dhcp)
            {
                _modeFromDhcp = true;
                deduced = AddressingMode.Dhcp;
            }
            else if (deduced == AddressingMode.Fixed && _modeFromDhcp)
            {
                // Adresse attribuée par notre serveur DHCP : le mode reste « DHCP ».
                deduced = AddressingMode.Dhcp;
            }
            if (Mode != deduced)
            {
                Mode = deduced;
                changed = true;
            }
        }
        return changed;
    }

    public void SetReachability(bool reachable)
    {
        Reachable = reachable;
        ReachabilityKnown = true;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    /// <summary>
    /// Déduction du mode d'adressage : APIPA pour 169.254.x.x, Inconnu pour 0.0.0.0,
    /// Fixe sinon (sans serveur DHCP local on ne peut pas distinguer Fixe/DHCP).
    /// </summary>
    private static AddressingMode DeduceMode(string ip)
    {
        if (ip == "0.0.0.0")
        {
            return AddressingMode.Unknown;
        }
        if (IPAddress.TryParse(ip, out var address)
            && address.GetAddressBytes() is [169, 254, _, _])
        {
            return AddressingMode.Apipa;
        }
        return AddressingMode.Fixed;
    }

    private bool SetField<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(null);
        return true;
    }

    /// <summary>Force une notification de l'UI (ex. rafraîchissement périodique).</summary>
    public void OnPropertyChangedExternal(string propertyName) => OnPropertyChanged(propertyName);

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
