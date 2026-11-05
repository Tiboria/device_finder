using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceFinder.App.Services;

/// <summary>IP alias posée sur une carte par l'application (pour tracer et nettoyer).</summary>
public sealed class AliasRecord
{
    public string? AdapterId { get; set; }
    public string? AdapterName { get; set; }
    public string? Ip { get; set; }
    public DateTime? AddedUtc { get; set; }
    public string? DeviceMac { get; set; }
}

/// <summary>
/// Réglages web (protocole, port) d'un device, persistés par adresse MAC.
/// </summary>
public sealed class DeviceWebConfig
{
    /// <summary>« http » ou « https ».</summary>
    public string Protocol { get; set; } = "http";

    /// <summary>Port de la page d'administration (80 par défaut).</summary>
    public int Port { get; set; } = 80;
}

/// <summary>
/// Paramètres d'application persistés dans %APPDATA%\DeviceFinder\settings.json.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string? SelectedAdapterId { get; set; }

    /// <summary>
    /// Langue de l'interface : « auto » (défaut = langue de l'OS), « fr-FR » ou
    /// « en-US ». Persisté dès que l'utilisateur choisit explicitement.
    /// </summary>
    public string Language { get; set; } = Loc.Auto;

    /// <summary>
    /// Vrai = ouvrir les pages dans le navigateur externe au lieu du navigateur
    /// intégré WebView2. Faux par défaut (navigateur intégré).
    /// </summary>
    public bool UseExternalBrowser { get; set; }

    /// <summary>Réglages web par device, clé = MAC normalisée.</summary>
    public Dictionary<string, DeviceWebConfig> DeviceWeb { get; set; } = new();

    /// <summary>IP alias posées par l'application (identifiables, nettoyables).</summary>
    public List<AliasRecord> Aliases { get; set; } = new();

    /// <summary>Première adresse du pool du mini-serveur DHCP (désactivé par défaut).</summary>
    public string DhcpPoolStart { get; set; } = "192.168.99.10";

    /// <summary>Dernière adresse du pool du mini-serveur DHCP.</summary>
    public string DhcpPoolEnd { get; set; } = "192.168.99.20";

    /// <summary>IP du PC sur le /24 du pool DHCP : source, passerelle et « server identifier ».</summary>
    public string DhcpServerIp { get; set; } = "192.168.99.1";

    /// <summary>
    /// Durée du bail DHCP attribué (option 51), en secondes. Défaut 10 minutes,
    /// plancher 5 minutes. Un bail survit à la fermeture du serveur : l'équipement
    /// garde son IP jusqu'à expiration.
    /// </summary>
    public int DhcpLeaseSeconds { get; set; } = 600;

    /// <summary>
    /// Vrai = barre de statut DHCP dépliée (compteurs détaillés), faux = version
    /// compacte. Choix persisté entre lancements, compacte par défaut.
    /// </summary>
    public bool DhcpDetailedCounters { get; set; }

    [JsonIgnore] public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeviceFinder");

    [JsonIgnore] public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings LoadOrDefault()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings: load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings: save failed: {ex.Message}");
        }
    }
}
