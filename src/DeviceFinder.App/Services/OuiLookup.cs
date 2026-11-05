using System.IO;
using System.Text;

namespace DeviceFinder.App.Services;

/// <summary>
/// Recherche du constructeur à partir de l'adresse MAC, via la base OUI IEEE
/// embarquée dans l'application (fichiers Resources/oui.csv et oui36.csv,
/// copiés à côté de l'exécutable). Aucun téléchargement à l'exécution.
/// </summary>
public sealed class OuiLookup
{
    private readonly Dictionary<string, string> _maL = new(); // préfixe 24 bits  (6 hex)
    private readonly Dictionary<string, string> _maS = new(); // préfixe 36 bits  (9 hex)

    public OuiLookup(string resourcesDirectory)
    {
        Load(Path.Combine(resourcesDirectory, "oui.csv"), _maL, 6);
        Load(Path.Combine(resourcesDirectory, "oui36.csv"), _maS, 9);
    }

    public string LookupVendor(string macColonFormat)
    {
        var hex = macColonFormat.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (hex.Length < 9)
        {
            return Loc.Get("Device.VendorUnknown");
        }
        if (_maS.TryGetValue(hex[..9], out var vendor))
        {
            return vendor;
        }
        if (_maL.TryGetValue(hex[..6], out vendor))
        {
            return vendor;
        }
        return Loc.Get("Device.VendorUnknown");
    }

    private void Load(string path, Dictionary<string, string> target, int keyLength)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Registry,", StringComparison.Ordinal))
                {
                    continue;
                }
                var fields = SplitCsvLine(line);
                if (fields.Length < 3 || fields[0].Length != keyLength)
                {
                    continue;
                }
                var key = fields[0].ToUpperInvariant();
                var name = fields[1].Trim();
                if (name.Length > 0)
                {
                    target.TryAdd(key, name);
                }
            }
        }
        catch (Exception ex)
        {
            // La base constructeur est best-effort : on ne bloque jamais le démarrage.
            System.Diagnostics.Debug.WriteLine($"OUI: loading {path} failed: {ex.Message}");
        }
    }

    /// <summary>Découpage CSV minimal gérant les guillemets.</summary>
    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }
}
