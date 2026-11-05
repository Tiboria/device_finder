using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DeviceFinder.App.Services;

/// <summary>
/// Résolution MAC d'une adresse IP via SendARP (iphlpapi.dll, P/Invoke).
/// </summary>
internal static class SendArpResolver
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int SendARP(uint destinationIp, uint sourceIp, byte[] macAddress, ref int physicalAddressLength);

    /// <summary>Résout la MAC d'une IP ; null si aucune réponse ARP.</summary>
    public static PhysicalAddress? TryResolve(IPAddress ipAddress, IPAddress? sourceIp = null)
    {
        if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        var destBytes = ipAddress.GetAddressBytes();
        uint dest = (uint)(destBytes[0] | (destBytes[1] << 8) | (destBytes[2] << 16) | (destBytes[3] << 24));

        uint src = 0;
        if (sourceIp is not null && sourceIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var srcBytes = sourceIp.GetAddressBytes();
            src = (uint)(srcBytes[0] | (srcBytes[1] << 8) | (srcBytes[2] << 16) | (srcBytes[3] << 24));
        }

        var mac = new byte[6];
        int len = mac.Length;
        int result = SendARP(dest, src, mac, ref len);
        if (result == 0 && len == 6 && mac.Any(b => b != 0))
        {
            return new PhysicalAddress(mac);
        }
        return null;
    }

    public static string FormatMac(PhysicalAddress address)
    {
        return BitConverter.ToString(address.GetAddressBytes()).Replace('-', ':');
    }
}
