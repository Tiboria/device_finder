using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeviceFinder.App.Services;

/// <summary>
/// Exclusion de la machine locale de la liste des devices :
/// le PC hôte émet lui-même des trames ARP (probe de passerelle, DAD) qui
/// remontent dans la capture. Toutes les MAC et toutes les IPv4 du PC,
/// toutes cartes confondues, sont therefore exclues des observations.
/// Le cache est rafraîchi au plus toutes les 2 secondes (les alias posés
/// par l'application deviennent automatiquement des IP locales).
/// </summary>
public static class LocalHostFilter
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly object Gate = new();
    private static HashSet<string> _macs = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _ips = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastRefreshUtc = DateTime.MinValue;

    /// <summary>Vrai si la MAC ou l'IP appartient à la machine locale.</summary>
    public static bool IsLocal(string mac, string? ip)
    {
        EnsureFresh();
        lock (Gate)
        {
            return IsLocalMac(mac) || IsLocalIp(ip);
        }
    }

    public static bool IsLocalIp(string? ip)
    {
        EnsureFresh();
        if (string.IsNullOrEmpty(ip))
        {
            return false;
        }
        lock (Gate)
        {
            return _ips.Contains(ip);
        }
    }

    public static bool IsLocalMac(string? mac)
    {
        EnsureFresh();
        var normalized = NormalizeMac(mac);
        if (normalized is null)
        {
            return false;
        }
        lock (Gate)
        {
            return _macs.Contains(normalized);
        }
    }

    /// <summary>Force un rafraîchissement immédiat (ex. après pose d'alias).</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            RefreshCore();
        }
    }

    private static void EnsureFresh()
    {
        if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
        {
            return;
        }
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
            {
                return;
            }
            RefreshCore();
        }
    }

    private static void RefreshCore()
    {
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var macBytes = nic.GetPhysicalAddress()?.GetAddressBytes();
                if (macBytes is { Length: 6 })
                {
                    macs.Add(BitConverter.ToString(macBytes).Replace('-', ':'));
                }
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ips.Add(unicast.Address.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LocalHostFilter: network enumeration failed: {ex.Message}");
        }
        _macs = macs;
        _ips = ips;
        _lastRefreshUtc = DateTime.UtcNow;
    }

    private static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }
        var hex = mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
        {
            return null;
        }
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
