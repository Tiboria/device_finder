using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace DeviceFinder.App.Services;

/// <summary>
/// Détection et installation guidée de Npcap. L'installeur (npcap-1.88.exe) est
/// embarqué en ressource dans l'exécutable : l'application l'extrait vers le
/// dossier temporaire et le lance si Npcap est absent — un seul fichier à copier
/// sur clé USB. Si Npcap est absent, la capture ARP est indisponible mais les
/// autres mécanismes de détection restent fonctionnels (dégradation propre,
/// jamais de blocage) ; le mode « IP manuelle » rend l'outil pleinement
/// utilisable sans Npcap.
/// </summary>
public static class NpcapService
{
    public const string EmbeddedResourceName = "DeviceFinder.Resources.npcap-1.88.exe";

    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap");
            if (key is not null)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Npcap: registry access failed: {ex.Message}");
        }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
            return File.Exists(Path.Combine(dir, "wpcap.dll"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Extrait l'installeur Npcap embarqué vers %TEMP%\DeviceFinder et renvoie son chemin.</summary>
    public static string? ExtractEmbeddedInstaller()
    {
        try
        {
            var assembly = typeof(NpcapService).Assembly;
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                Debug.WriteLine($"Npcap: embedded resource not found: {EmbeddedResourceName}");
                return null;
            }
            var dir = Path.Combine(Path.GetTempPath(), "DeviceFinder");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "npcap-1.88.exe");
            using var file = File.Create(path);
            stream.CopyTo(file);
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Npcap: extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Cherche l'installeur Npcap embarqué puis, à défaut, livré à côté de l'exécutable.</summary>
    public static string? FindInstaller()
    {
        var embedded = ExtractEmbeddedInstaller();
        if (embedded is not null)
        {
            return embedded;
        }
        var baseDir = AppContext.BaseDirectory;
        var directories = new[] { baseDir, Path.Combine(baseDir, "redist"), Path.Combine(baseDir, "Npcap") };
        foreach (var dir in directories)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                var exe = Directory.EnumerateFiles(dir, "npcap*.exe")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (exe is not null)
                {
                    return exe;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Npcap: installer lookup failed in {dir}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// Installation de Npcap. En mode silencieux (/S), l'option « install for
    /// Nmap-compatible mode » n'est pas passée, donc désactivée. En mode GUI,
    /// l'utilisateur parcourt l'assistant normal. L'application est déjà élevée
    /// (requireAdministrator), aucun prompt UAC supplémentaire.
    /// </summary>
    public static (bool Success, string Message) Install(string installerPath, bool silent, int timeoutSeconds = 900)
    {
        if (!File.Exists(installerPath))
        {
            return (false, Loc.Fmt("Npcap.InstallerNotFound", installerPath));
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = silent ? "/S" : "",
                UseShellExecute = true,
                CreateNoWindow = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, Loc.Get("Npcap.CannotLaunch"));
            }
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                return (false, Loc.Fmt("Npcap.Timeout", timeoutSeconds));
            }
            if (process.ExitCode != 0)
            {
                return (false, Loc.Fmt("Npcap.ExitCode", process.ExitCode));
            }
            return IsInstalled()
                ? (true, Loc.Get("Npcap.Installed"))
                : (false, Loc.Get("Npcap.NotInstalledAfter"));
        }
        catch (Exception ex)
        {
            return (false, Loc.Fmt("Npcap.InstallFailed", ex.Message));
        }
    }
}
