using PacketDotNet;
using SharpPcap;

namespace DeviceFinder.App.Services;

/// <summary>
/// Capture des trames ARP sur la carte sélectionnée (Npcap via SharpPcap, mode promiscuous).
/// Toute trame ARP émise par l'équipement — probe de passerelle, ARP gratuit, réponse à un
/// scan — révèle sa MAC et son IP source dans l'en-tête ARP, quel que soit son sous-réseau.
/// C'est le mécanisme de référence pour les équipements en IP fixe.
/// </summary>
public sealed class ArpCaptureService : IDisposable
{
    private ICaptureDevice? _device;
    private string? _selfMac;
    private long _framesSeen;
    private long _framesSkippedLocal;
    private long _framesReported;

    /// <summary>(MAC au format AA:BB:CC:DD:EE:FF, IP source du champ sender ARP).</summary>
    public event Action<string, string>? DeviceObservation;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Nom du handle Npcap réellement ouvert (traçabilité, barre de statut).</summary>
    public string? OpenedDeviceName { get; private set; }

    /// <summary>Nom de handle attendu pour la dernière carte sélectionnée.</summary>
    public string? ExpectedDeviceName { get; private set; }

    /// <summary>Trames ARP vues au total sur la carte.</summary>
    public long FramesSeen => _framesSeen;

    /// <summary>Trames écartées (émetteur = machine locale ou champs invalides).</summary>
    public long FramesSkippedLocal => _framesSkippedLocal;

    /// <summary>Trames rapportées au registre des devices.</summary>
    public long FramesReported => _framesReported;

    public bool Start(AdapterInfo adapter)
    {
        Stop();
        _selfMac = adapter.Mac;
        _framesSeen = 0;
        _framesSkippedLocal = 0;
        _framesReported = 0;
        OpenedDeviceName = null;
        // Le nom de device Npcap est du type \\Device\\NPF_{GUID}. NetworkInterface.Id
        // peut etre fourni avec ou sans accolades selon la source : on compare sur le
        // GUID nu, insensible a la casse. Un GUID etant unique, aucun risque de
        // s'attacher a une autre carte (WiFi, virtuelle...).
        var guid = adapter.Id.Trim().Trim('{', '}');
        ExpectedDeviceName = "\\Device\\NPF_{" + guid + "}";
        try
        {
            var available = new List<string>();
            foreach (var candidate in CaptureDeviceList.Instance)
            {
                if (candidate.Name is null)
                {
                    continue;
                }
                available.Add(candidate.Name);
                if (candidate.Name.Contains(guid, StringComparison.OrdinalIgnoreCase))
                {
                    _device = candidate;
                    break;
                }
            }
            if (_device is null)
            {
                LastError = available.Count == 0
                    ? Loc.Fmt("ArpCapture.NoHandle", ExpectedDeviceName, adapter.Name)
                    : Loc.Fmt("ArpCapture.HandleNotFound", adapter.Name, ExpectedDeviceName,
                        string.Join(", ", available));
                IsRunning = false;
                return false;
            }

            OpenedDeviceName = _device.Name;
            _device.Open(DeviceModes.Promiscuous);
            _device.Filter = "arp";
            _device.OnPacketArrival += OnPacketArrival;
            _device.StartCapture();
            IsRunning = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = Loc.Fmt("ArpCapture.OpenFailed", ExpectedDeviceName, ex.Message);
            IsRunning = false;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        var device = _device;
        _device = null;
        if (device is null)
        {
            IsRunning = false;
            return;
        }
        try
        {
            if (IsRunning)
            {
                device.StopCapture();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ARP: StopCapture : {ex.Message}");
        }
        try
        {
            device.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ARP: Close : {ex.Message}");
        }
        IsRunning = false;
    }

    public void Dispose() => Stop();

    private void OnPacketArrival(object? sender, PacketCapture args)
    {
        try
        {
            var raw = args.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var arp = packet.Extract<ArpPacket>();
            System.Threading.Interlocked.Increment(ref _framesSeen);
            if (arp?.SenderHardwareAddress is null)
            {
                System.Threading.Interlocked.Increment(ref _framesSkippedLocal);
                return;
            }
            var macBytes = arp.SenderHardwareAddress.GetAddressBytes();
            if (macBytes.Length != 6 || macBytes.All(b => b == 0x00))
            {
                System.Threading.Interlocked.Increment(ref _framesSkippedLocal);
                return;
            }
            var mac = SendArpResolver.FormatMac(arp.SenderHardwareAddress);
            if (_selfMac is not null && string.Equals(mac, _selfMac, StringComparison.OrdinalIgnoreCase))
            {
                System.Threading.Interlocked.Increment(ref _framesSkippedLocal);
                return;
            }
            var ip = arp.SenderProtocolAddress?.ToString();
            if (string.IsNullOrEmpty(ip))
            {
                System.Threading.Interlocked.Increment(ref _framesSkippedLocal);
                return;
            }
            // Règle de parsing (test réel du 04/11/2026) :
            // - requête « who-has X tell Y » : on n'enregistre QUE (MAC émetteur, IP Y).
            //   L'IP cible X n'est JAMAIS enregistrée — elle ne prouve pas que l'émetteur
            //   la détient (c'était la source d'entrées fantômes).
            // - réponse « is-at » : on enregistre (MAC du répondant, IP du répondant),
            //   c'est-à-dire les champs « sender » de l'en-tête ARP dans les deux cas.
            //   NB : l'IP Y peut être 0.0.0.0 (probe ARP/APIPA) : la MAC est alors
            //   enregistrée sans IP confirmée.
            System.Threading.Interlocked.Increment(ref _framesReported);
            DeviceObservation?.Invoke(mac, ip);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ARP: packet skipped: {ex.Message}");
        }
    }
}
