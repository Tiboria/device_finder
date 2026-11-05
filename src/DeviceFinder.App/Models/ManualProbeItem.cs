using System.ComponentModel;
using DeviceFinder.App.Services;

namespace DeviceFinder.App.Models;

/// <summary>État d'une adresse sondée — traduit à l'affichage, pas avant.</summary>
public enum ManualProbeStatus
{
    Reachable,
    ArpOnly,
    NoResponse,
}

/// <summary>
/// Ligne de résultat du mode « IP manuelle ». L'état est conservé structuré
/// (<see cref="StatusKind"/>) : le texte affiché est traduit au moment du rendu,
/// ce qui permet la bascule de langue à chaud sans re-sonder les adresses.
/// </summary>
public sealed class ManualProbeItem : INotifyPropertyChanged
{
    public required string Ip { get; init; }
    public string Mac { get; init; } = "—";
    public string Vendor { get; init; } = "—";
    public required ManualProbeStatus StatusKind { get; init; }

    public string Status => StatusKind switch
    {
        ManualProbeStatus.Reachable => Loc.Get("Manual.StatusReachable"),
        ManualProbeStatus.ArpOnly => Loc.Get("Manual.StatusArpOnly"),
        _ => Loc.Get("Manual.StatusNoResponse"),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Notifie l'UI après un changement de langue (texte re-traduit).</summary>
    public void RefreshStatusText() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
}
