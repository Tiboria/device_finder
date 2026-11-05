using System.Globalization;
using System.Windows;

namespace DeviceFinder.App.Services;

/// <summary>
/// Localisation de l'interface (fr-FR, en-US). Mécanisme zéro dépendance qui
/// permet la bascule À CHAUD :
/// - les textes de l'UI (XAML) portent des {DynamicResource} pointant dans un
///   dictionnaire de ressources échangé dans Application.Resources.MergedDictionaries :
///   le swap met à jour tous les textes affichés, sans recharger la fenêtre ;
/// - les chaînes construites en code (barre de statut, messages de services,
///   boîtes de dialogue) passent par <see cref="Get"/>/<see cref="Fmt"/>, servis
///   par la table extraite du MÊME dictionnaire — une seule source de vérité par
///   langue et par clé.
/// Langue par défaut : celle de l'OS (premier lancement) ; le choix est ensuite
/// persisté dans les paramètres (« auto » tant que l'utilisateur n'a pas choisi).
/// La table est immuable après chargement : les threads de fond (capture ARP,
/// serveur DHCP) la lisent sans verrou.
/// </summary>
public static class Loc
{
    public const string French = "fr-FR";
    public const string English = "en-US";

    /// <summary>Clé de réglage « suit la langue de l'OS ».</summary>
    public const string Auto = "auto";

    private const string DictionaryUriPrefix = "pack://application:,,,/Localization/Strings.";

    /// <summary>Table de la langue active (remplacée atomiquement au changement).</summary>
    private static volatile IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    /// <summary>Langue d'interface affichée (« fr-FR » ou « en-US »).</summary>
    public static string Current { get; private set; } = French;

    /// <summary>Levé sur le thread UI après un changement effectif de langue.</summary>
    public static event Action? LanguageChanged;

    public static bool IsLanguageCode(string? code) => code is French or English;

    /// <summary>Langue d'interface de l'OS : français → fr-FR, toute autre → en-US.</summary>
    public static string DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? French : English;

    /// <summary>
    /// Résout le réglage persisté vers un code de langue : « auto » (défaut) suit
    /// la langue de l'OS, sinon le choix explicite (toute valeur inconnue retombe
    /// sur la langue de l'OS — jamais de langue inexistante).
    /// </summary>
    public static string Resolve(string? setting) =>
        IsLanguageCode(setting) ? setting! : DetectSystemLanguage();

    /// <summary>
    /// Applique la langue : remplace le dictionnaire de chaînes dans les ressources
    /// de l'application (tous les {DynamicResource} se réévaluent à chaud) et
    /// recharge la table du code. Appelable avant la création de la fenêtre
    /// principale (démarrage) comme après (bascule du sélecteur).
    /// </summary>
    public static void SetLanguage(string code)
    {
        code = code == French ? French : English;
        var resources = Application.Current.Resources;
        var fresh = new ResourceDictionary
        {
            Source = new Uri(DictionaryUriPrefix + code + ".xaml"),
        };

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in fresh.Keys)
        {
            if (key is string name && fresh[name] is string value)
            {
                table[name] = value;
            }
        }
        _table = table;

        // Retire les dictionnaires de langue déjà présents (démarrage ou bascule
        // précédente) puis ajoute le nouveau — le swap notifie tous les
        // DynamicResource de l'application.
        for (int i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (resources.MergedDictionaries[i].Source?.OriginalString.StartsWith(
                    DictionaryUriPrefix, StringComparison.Ordinal) == true)
            {
                resources.MergedDictionaries.RemoveAt(i);
            }
        }
        resources.MergedDictionaries.Add(fresh);

        bool changed = Current != code;
        Current = code;
        if (changed)
        {
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>
    /// Texte de la langue active pour cette clé. Repli sur la clé elle-même : une
    /// clé manquante reste visible en UI (repérable à la relecture) au lieu
    /// d'afficher une chaîne vide.
    /// </summary>
    public static string Get(string key)
    {
        var table = _table;
        return table.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : key;
    }

    /// <summary>Texte mis en forme : {0}, {1}… remplacés par les arguments fournis.</summary>
    public static string Fmt(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
