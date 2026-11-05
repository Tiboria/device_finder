using System.Net;
using System.Net.Sockets;

namespace DeviceFinder.App.Services;

/// <summary>
/// Écoute passive des broadcasts émis par l'équipement à son démarrage,
/// sur la carte Ethernet sélectionnée :
/// - SSDP (UDP 239.255.255.250:1900)
/// - WS-Discovery / ONVIF (UDP 239.255.255.250:3702)
/// - mDNS (UDP 224.0.0.251:5353)
/// - NBNS (UDP broadcast, port 137)
/// Chaque paquet reçu donne l'IP source ; la MAC est résolue via SendARP
/// **depuis l'IP de la carte sélectionnée** (la résolution ne peut alors pas
/// dériver vers la passerelle d'une autre carte, ex. WiFi en proxy-ARP).
/// De plus, le datagramme n'est accepté que s'il est arrivé physiquement sur
/// la carte sélectionnée (IP_PKTINFO) : Windows livre les broadcasts aux
/// sockets liés à un port même s'ils arrivent sur une autre carte.
/// </summary>
public sealed class BroadcastListenerService : IDisposable
{
    private static readonly IPAddress SsdpGroup = IPAddress.Parse("239.255.255.250");
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    /// <summary>IP_PKTINFO (option Windows) : fournit l'index de la carte d'arrivée du datagramme.</summary>
    private const SocketOptionName IpPktInfo = (SocketOptionName)19;

    private readonly List<Socket> _sockets = new();
    private CancellationTokenSource? _cts;
    private uint _ifIndex;
    private uint _ipv4Index;
    private IPAddress? _sourceIp;
    private long _acceptedCount;
    private long _rejectedCount;
    private long _resolvedCount;
    private long _unresolvedCount;

    /// <summary>(MAC au format AA:BB:CC:DD:EE:FF, IP source).</summary>
    public event Action<string, string>? DeviceObservation;

    public bool IsRunning { get; private set; }

    /// <summary>Datagrammes acceptés (arrivés sur la carte sélectionnée).</summary>
    public long AcceptedCount => _acceptedCount;

    /// <summary>Datagrammes rejetés : arrivés sur une autre carte (WiFi, VPN…).</summary>
    public long RejectedCount => _rejectedCount;

    /// <summary>Sources dont la MAC a été résolue via SendARP sur la carte sélectionnée.</summary>
    public long ResolvedCount => _resolvedCount;

    /// <summary>Sources sans réponse ARP sur la carte sélectionnée (ignorées).</summary>
    public long UnresolvedCount => _unresolvedCount;

    public void Start(AdapterInfo adapter)
    {
        Stop();
        if (adapter.PrimaryIpv4 is null)
        {
            // Sans IPv4 sur la carte on ne peut pas rejoindre les groupes multicast.
            IsRunning = false;
            return;
        }
        var localIp = adapter.PrimaryIpv4;
        _ifIndex = adapter.InterfaceIndex;
        _ipv4Index = adapter.Ipv4Index;
        _sourceIp = localIp;
        _acceptedCount = 0;
        _rejectedCount = 0;
        _resolvedCount = 0;
        _unresolvedCount = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var listeners = new (string Name, IPAddress? Group, int Port)[]
        {
            ("SSDP", SsdpGroup, 1900),
            ("WS-Discovery", SsdpGroup, 3702),
            ("mDNS", MdnsGroup, 5353),
            ("NBNS", null, 137),
        };

        foreach (var (name, group, port) in listeners)
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(localIp, port));
                if (group is not null)
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, localIp));
                }
                // Nécessaire pour connaître l'index de la carte d'arrivée de chaque datagramme.
                socket.SetSocketOption(SocketOptionLevel.IP, IpPktInfo, true);
                lock (_sockets)
                {
                    _sockets.Add(socket);
                }
                StartReceiveLoop(name, socket, localIp, token);
            }
            catch (Exception ex)
            {
                // Un port peut être déjà occupé (pile Windows pour NBNS, etc.) : on dégrade sans bloquer.
                System.Diagnostics.Debug.WriteLine($"Listen {name} (port {port}) unavailable: {ex.Message}");
            }
        }
        IsRunning = true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        List<Socket> sockets;
        lock (_sockets)
        {
            sockets = _sockets.ToList();
            _sockets.Clear();
        }
        foreach (var socket in sockets)
        {
            try { socket.Close(); } catch { /* déjà fermée */ }
        }
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();

    private void StartReceiveLoop(string listenerName, Socket socket, IPAddress localIp, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    var result = await socket.ReceiveMessageFromAsync(
                        new ArraySegment<byte>(buffer), SocketFlags.None, remote);
                    if (result.ReceivedBytes == 0)
                    {
                        continue;
                    }
                    // Ciblage strict : ne garder que les datagrammes arrivés sur la carte
                    // sélectionnée. On compare aux DEUX index possibles de la carte
                    // (NET_IFINDEX de GetAdapterIndex et index IPv4 historique : ils
                    // peuvent différer sur Windows) ; un index 0 = information absente,
                    // on ne rejette pas dans ce cas (la résolution MAC via SendARP
                    // depuis l'IP de la carte reste le verrou réel).
                    var arrival = (uint)result.PacketInformation.Interface;
                    if (arrival != 0 && arrival != _ifIndex && arrival != _ipv4Index)
                    {
                        System.Threading.Interlocked.Increment(ref _rejectedCount);
                        continue;
                    }
                    if (result.RemoteEndPoint is IPEndPoint remoteIp
                        && remoteIp.Address.AddressFamily == AddressFamily.InterNetwork
                        && !remoteIp.Address.Equals(localIp))
                    {
                        System.Threading.Interlocked.Increment(ref _acceptedCount);
                        HandleSource(listenerName, remoteIp.Address);
                    }
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // Reset (port ICMP unreachable) ou fermeture : petite pause puis on continue.
                    System.Diagnostics.Debug.WriteLine($"Listen {listenerName}: {ex.SocketErrorCode}");
                    if (token.IsCancellationRequested) break;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, token);
    }

    private void HandleSource(string listenerName, IPAddress sourceIp)
    {
        try
        {
            // SendARP **depuis l'IP de la carte sélectionnée** : la requête part sur cette
            // carte uniquement, la réponse ne peut pas venir d'une autre carte (proxy-ARP
            // du routeur WiFi) — l'IP source ne peut pas se voir attribuer une mauvaise MAC.
            var mac = SendArpResolver.TryResolve(sourceIp, _sourceIp);
            if (mac is not null)
            {
                System.Threading.Interlocked.Increment(ref _resolvedCount);
                DeviceObservation?.Invoke(SendArpResolver.FormatMac(mac), sourceIp.ToString());
            }
            else
            {
                System.Threading.Interlocked.Increment(ref _unresolvedCount);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Listen {listenerName}: MAC resolution failed: {ex.Message}");
        }
    }
}
