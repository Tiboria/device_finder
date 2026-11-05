using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DeviceFinder.App.Services;

/// <summary>Carte réseau énumérée, utilisable comme élément du ComboBox.</summary>
public sealed record AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Première adresse IPv4 de la carte, null si aucune.</summary>
    public IPAddress? PrimaryIpv4 { get; init; }
    /// <summary>Toutes les adresses IPv4 de la carte.</summary>
    public IReadOnlyList<IPAddress> Ipv4Addresses { get; init; } = Array.Empty<IPAddress>();
    /// <summary>MAC de la carte au format AA:BB:CC:DD:EE:FF.</summary>
    public string? Mac { get; init; }
    /// <summary>
    /// Index de la carte (NET_IFINDEX, celui de IP_PKTINFO/iphlpapi moderne).
    /// Obtenu via GetAdapterIndex ; 0 si la résolution a échoué.
    /// </summary>
    public uint InterfaceIndex { get; init; }
    /// <summary>
    /// Index IPv4 historique (GetIPv4Properties().Index) : peut différer du NET_IFINDEX.
    /// Conservé pour la comparaison des paquets entrants.
    /// </summary>
    public uint Ipv4Index { get; init; }
    /// <summary>Vrai pour une carte virtuelle (VMware, VirtualBox, Hyper-V, TAP/VPN…).</summary>
    public bool IsVirtual { get; init; }
    public bool IsUp { get; init; }
    public OperationalStatus Status { get; init; }

    public override string ToString()
    {
        var ip = PrimaryIpv4?.ToString() ?? Loc.Get("Adapter.NoIpv4");
        var state = IsUp ? "" : Loc.Get("Adapter.Disconnected");
        var virtualMark = IsVirtual ? Loc.Get("Adapter.Virtual") : "";
        return $"{Name} — {ip}{state}{virtualMark}";
    }
}

/// <summary>
/// Énumération des cartes Ethernet du PC et utilitaires de sous-réseau.
/// </summary>
public static class NetworkAdapterService
{
    /// <summary>
    /// Mots-clés de cartes virtuelles (hyperviseurs, VPN/TAP, loopback, capteur Npcap…).
    /// Un PC de technicien n'a normalement qu'une carte Ethernet physique à surveiller.
    /// </summary>
    private static readonly string[] VirtualKeywords =
    {
        "vmware", "virtualbox", "hyper-v", "vethernet", "virtual switch",
        "km-test", "loopback", "wan miniport", "tap-windows", "tap-adapter",
        "openvpn", "npcap", "winpcap", "bluetooth", "wi-fi direct",
        "debug adapter", "rawip", "qos packet", "parallels", "virtual network",
        "anyconnect", "globalprotect", "zerotier", "wireguard",
    };

    /// <summary>Vrai si la description correspond à une carte virtuelle connue.</summary>
    public static bool LooksVirtual(string description)
    {
        return VirtualKeywords.Any(k => description.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Index de carte (NET_IFINDEX) à partir de l'Id/GUID NetworkInterface,
    /// via GetAdapterIndex (iphlpapi). C'est l'index qu'IP_PKTINFO remonte pour
    /// les datagrammes entrants — PAS l'index IPv4 historique, qui peut différer.
    /// 0 si la résolution échoue.
    /// </summary>
    public static uint GetIfIndex(string adapterId)
    {
        try
        {
            var name = (adapterId ?? "").Trim().Trim('{', '}');
            if (name.Length > 0 && GetAdapterIndex(name, out var index) == 0)
            {
                return index;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetIfIndex({adapterId}) failed: {ex.Message}");
        }
        return 0;
    }

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAdapterIndex(string adapterName, out uint ifIndex);

    /// <summary>
    /// Cartes Ethernet physiques uniquement (WiFi, boucles et tunnels exclus ;
    /// les cartes virtuelles VMware/VirtualBox/Hyper-V/TAP/Npcap sont filtrées).
    /// S'il ne reste aucune carte physique, toutes les cartes Ethernet sont
    /// retournées avec le marqueur « virtuelle » plutôt qu'une liste vide.
    /// </summary>
    public static List<AdapterInfo> GetEthernetAdapters()
    {
        var all = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
            {
                continue;
            }
            var props = nic.GetIPProperties();
            var v4s = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .ToList();
            var macBytes = nic.GetPhysicalAddress()?.GetAddressBytes();
            var mac = macBytes is { Length: 6 }
                ? BitConverter.ToString(macBytes).Replace('-', ':')
                : null;
            uint ipv4Index = 0;
            try
            {
                ipv4Index = (uint)(props.GetIPv4Properties()?.Index ?? 0);
            }
            catch
            {
                // Carte sans stack IPv4 : index inconnu, restera 0.
            }
            var ifIndex = GetIfIndex(nic.Id);
            if (ifIndex == 0)
            {
                ifIndex = ipv4Index;
            }
            all.Add(new AdapterInfo
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                PrimaryIpv4 = v4s.FirstOrDefault(),
                Ipv4Addresses = v4s,
                Mac = mac,
                InterfaceIndex = ifIndex,
                Ipv4Index = ipv4Index,
                IsVirtual = LooksVirtual(nic.Description),
                IsUp = nic.OperationalStatus == OperationalStatus.Up,
                Status = nic.OperationalStatus,
            });
        }
        var physical = all.Where(a => !a.IsVirtual).ToList();
        return physical.Count > 0 ? physical : all;
    }

    /// <summary>Convertit une IPv4 en entier (ordre réseau, big-endian).</summary>
    public static uint ToUint(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    public static uint MaskFromPrefix(int prefixLength)
    {
        if (prefixLength <= 0)
        {
            return 0;
        }
        return uint.MaxValue << (32 - Math.Min(32, prefixLength));
    }

    /// <summary>Vrai si le réseau /24 de deviceIp chevauche un réseau actif d'une autre carte.</summary>
    public static bool HasSubnetConflictOnOtherAdapter(string selectedAdapterId, IPAddress deviceIp, out string conflictDescription)
    {
        uint devNet = ToUint(deviceIp) & MaskFromPrefix(24);
        uint devBroadcast = devNet | ~MaskFromPrefix(24);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Id == selectedAdapterId || nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                int prefix = unicast.PrefixLength is > 0 and <= 32 ? unicast.PrefixLength : 32;
                uint mask = MaskFromPrefix(prefix);
                uint net = ToUint(unicast.Address) & mask;
                uint broadcast = net | ~mask;
                if (devNet <= broadcast && net <= devBroadcast)
                {
                    conflictDescription = Loc.Fmt("Conflict.Description",
                        nic.Name, nic.Description, $"{unicast.Address}/{prefix}",
                        $"{new IPAddress(devNet)}/24");
                    return true;
                }
            }
        }
        conflictDescription = "";
        return false;
    }
}
