using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DeviceFinder.App.Models;
using DeviceFinder.App.Services;
using Microsoft.Web.WebView2.Core;

namespace DeviceFinder.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly OuiLookup _oui;
    private readonly DeviceStore _store;
    private readonly BroadcastListenerService _passiveListener = new();
    private readonly ArpCaptureService _arpCapture = new();
    private readonly ApipaScanService _apipaScan = new();
    private readonly DhcpServerService _dhcpServer = new();
    private readonly ReachabilityMonitor _monitor;
    private readonly AliasIpService _aliasService;
    private readonly DeviceOpenerService _opener;
    private readonly ObservableCollection<Device> _devices = new();
    private readonly ObservableCollection<ManualProbeItem> _manualItems = new();
    private readonly DispatcherTimer _statusClock;

    private AdapterInfo? _selectedAdapter;
    private bool _suppressAdapterEvents;
    private bool _suppressLanguageEvents;
    private bool _suppressDetailEvents;
    private bool _suppressDhcpEvents;
    private bool _dhcpBusy;
    private bool _openingDevice;
    private bool _probingManual;
    private bool _closing;
    private bool _browserInitialized;
    private bool _browserUnavailable;
    private bool _suppressExternalCheckEvents;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.LoadOrDefault();
        _oui = new OuiLookup(Path.Combine(AppContext.BaseDirectory, "Resources"));
        _store = new DeviceStore(_oui);
        _monitor = new ReachabilityMonitor(_store);
        _aliasService = new AliasIpService(_settings);
        _opener = new DeviceOpenerService(_aliasService);

        DeviceListView.ItemsSource = _devices;
        ManualResultsView.ItemsSource = _manualItems;

        _store.DeviceAdded += d => Dispatcher.BeginInvoke(() =>
        {
            if (!_devices.Contains(d))
            {
                _devices.Add(d);
                StatusBar.Text = Loc.Fmt("Status.DeviceDetected", d.Mac, d.Vendor);
            }
        });
        _store.DeviceUpdated += d => Dispatcher.BeginInvoke(() =>
        {
            // Le panneau de détail suit le device sélectionné (IP, historique, état).
            if (ReferenceEquals(DeviceListView.SelectedItem, d))
            {
                UpdateDetailPanel(d);
            }
        });

        _passiveListener.DeviceObservation += (mac, ip) => OnObservation(mac, ip);
        _arpCapture.DeviceObservation += (mac, ip) => OnObservation(mac, ip);
        _apipaScan.DeviceObservation += (mac, ip) => OnObservation(mac, ip);
        // La transaction DHCP révèle la MAC et l'IP attribuée : mode « DHCP » imposé.
        _dhcpServer.DeviceObservation += (mac, ip) => OnObservation(mac, ip, AddressingMode.Dhcp);
        _dhcpServer.LeasesChanged += () => Dispatcher.BeginInvoke(UpdateDhcpLeases);
        _dhcpServer.StatusMessage += msg => Dispatcher.BeginInvoke(() => StatusBar.Text = msg);
        _aliasService.StatusMessage += msg => Dispatcher.BeginInvoke(() => StatusBar.Text = msg);

        // Rafraîchit l'affichage « Dernière vue » chaque seconde + progression du scan APIPA
        // + état permanent de la détection (carte, capture, compteurs) : un incident muet
        // ne doit plus être possible sur un outil de terrain.
        _statusClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusClock.Tick += (_, _) =>
        {
            foreach (var device in _devices)
            {
                device.OnPropertyChangedExternal(nameof(Device.LastSeenText));
            }
            CaptureStatusText.Text = BuildDetectionStatus();
            DhcpCompactStatus.Text = BuildDhcpCompactStatus();
            DhcpDetailedStatus.Text = BuildDhcpDetailedStatus();
            ApipaStatusText.Text = _apipaScan.IsRunning
                ? Loc.Fmt("Status.ApipaProgress", _apipaScan.ProgressPercent, FormatDuration(_apipaScan.Elapsed))
                : "";
            if (DeviceListView.SelectedItem is Device selected)
            {
                DetailLastSeen.Text = selected.LastSeen.ToString("HH:mm:ss");
            }
        };
        _statusClock.Start();

        // Bascule de langue à chaud : les textes XAML suivent seuls (DynamicResource),
        // tout ce qui est construit en code est rafraîchi ici.
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Changement de langue effective : rafraîchit tous les textes construits en
    /// code (bandeaux, baux, panneau de détail, listes, barre de statut) — les
    /// textes XAML suivent via DynamicResource sans intervention.
    /// </summary>
    private void OnLanguageChanged()
    {
        LoadAdapters();
        UpdateNpcapBanner();
        UpdateResidualBanner();
        UpdateDhcpBanner();
        UpdateDhcpLeases();
        UpdateDetailPanel(SelectedDevice);
        foreach (var device in _devices)
        {
            device.OnPropertyChangedExternal(nameof(Device.ModeDisplay));
            device.OnPropertyChangedExternal(nameof(Device.StatusText));
        }
        foreach (var item in _manualItems)
        {
            item.RefreshStatusText();
        }
        StatusBar.Text = Loc.Fmt("Status.LanguageChanged", SelectedLanguageEndonym());
    }

    /// <summary>Endonyme affiché dans le sélecteur pour le code de langue courant.</summary>
    private static string SelectedLanguageEndonym() =>
        Loc.Current == Loc.French ? "Français" : "English";

    /// <summary>
    /// État permanent de la détection, affiché en continu : carte sélectionnée,
    /// état de la capture ARP (ouverte / échec avec message, handle attendu vs ouvert),
    /// compteurs de trames vues/rejetées, activité des broadcasts.
    /// </summary>
    private string BuildDetectionStatus()
    {
        var adapter = _selectedAdapter is null
            ? Loc.Get("Status.NoAdapter")
            : Loc.Fmt("Status.Adapter", _selectedAdapter.Name);

        string capture;
        if (!NpcapService.IsInstalled())
        {
            capture = Loc.Get("Status.CaptureAbsent");
        }
        else if (_arpCapture.IsRunning)
        {
            capture = Loc.Fmt("Status.CaptureOpen",
                _arpCapture.OpenedDeviceName,
                _arpCapture.FramesSeen, _arpCapture.FramesReported, _arpCapture.FramesSkippedLocal);
        }
        else
        {
            capture = Loc.Fmt("Status.CaptureFailed",
                _arpCapture.LastError ?? Loc.Get("Status.CaptureNoStart"));
        }

        string broadcasts = _passiveListener.IsRunning
            ? Loc.Fmt("Status.BroadcastsActive",
                _passiveListener.AcceptedCount, _passiveListener.RejectedCount,
                _passiveListener.ResolvedCount, _passiveListener.UnresolvedCount)
            : Loc.Get("Status.BroadcastsIdle");

        return $"{adapter} · {capture} · {broadcasts}";
    }

    /// <summary>
    /// Ligne DHCP compacte de la barre de statut (par défaut) : état, nombre de baux,
    /// dernière activité. Les compteurs détaillés vivent dans
    /// <see cref="BuildDhcpDetailedStatus"/>, affichés uniquement si la ligne est dépliée.
    /// </summary>
    private string BuildDhcpCompactStatus()
    {
        if (!_dhcpServer.IsRunning)
        {
            int stopped = _dhcpServer.GetLeaseViews().Count;
            return stopped > 0
                ? Loc.Fmt("Status.DhcpStoppedWithLeases", stopped)
                : Loc.Get("Status.DhcpInactive");
        }
        string activity = _dhcpServer.LastActivityUtc is { } last
            ? Loc.Fmt("Status.DhcpLastActivity",
                Math.Max(0, (int)(DateTime.UtcNow - last).TotalSeconds))
            : Loc.Get("Status.DhcpNoActivity");
        return Loc.Fmt("Status.DhcpActiveCompact", _dhcpServer.BoundLeases, activity);
    }

    /// <summary>
    /// Ligne DHCP détaillée (version dépliée) : chaque compteur de la chaîne
    /// réception → parse → tri → OFFER doit être visible (exigence : aucun
    /// chemin muet) — aucun compteur supprimé par la version compacte.
    /// </summary>
    private string BuildDhcpDetailedStatus()
    {
        if (!_dhcpServer.IsRunning)
        {
            return Loc.Get("Status.DhcpDetailedInactive");
        }
        return Loc.Fmt("Status.DhcpDetailedActive",
            FormatLeaseSeconds(_dhcpServer.LeaseSeconds),
            _dhcpServer.DatagramsSeen,
            _dhcpServer.DiscoversSeen, _dhcpServer.OffersSent,
            _dhcpServer.UnconfirmedOffers,
            _dhcpServer.RequestsSeen, _dhcpServer.AcksSent, _dhcpServer.NaksSent,
            _dhcpServer.IgnoredOtherServer,
            _dhcpServer.NonDhcp, _dhcpServer.UnknownMessageType,
            _dhcpServer.RejectedLocalMac, _dhcpServer.RejectedRelay,
            _dhcpServer.ReleasedOrDeclined,
            _dhcpServer.BoundLeases, _dhcpServer.ReceiveErrors)
            + (_dhcpServer.ReceiveErrors > 0 ? $" ({_dhcpServer.LastReceiveError})" : "");
    }

    /// <summary>Durée de bail lisible : « 10 min », « 1 h »…</summary>
    private static string FormatLeaseSeconds(int seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0)
        {
            return Loc.Fmt("Common.Hours", seconds / 3600);
        }
        if (seconds >= 60 && seconds % 60 == 0)
        {
            return Loc.Fmt("Common.Minutes", seconds / 60);
        }
        return Loc.Fmt("Common.Seconds", seconds);
    }

    private void OnObservation(string mac, string ip, AddressingMode? forcedMode = null)
    {
        // Appelé depuis les threads d'écoute : le store est thread-safe,
        // les Device notifient l'UI via INotifyPropertyChanged.
        // Toute exception est tracée : elle ne doit pas tuer silencieusement
        // la boucle d'écoute qui nous a appelés.
        try
        {
            // Exclusion de la machine locale (ses trames ARP remontent dans la capture).
            if (LocalHostFilter.IsLocal(mac, ip))
            {
                return;
            }
            // Fusion par MAC : une seule entrée par MAC normalisée, l'IP confirmée la
            // plus récente remplace l'ancienne (qui part dans l'historique).
            _store.Report(mac, ip, forcedMode);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Observation ({mac}, {ip}) skipped: {ex.Message}");
        }
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
        {
            return Loc.Fmt("Common.MinutesSeconds", (int)elapsed.TotalMinutes, elapsed.Seconds);
        }
        return Loc.Fmt("Common.Seconds", (int)elapsed.TotalSeconds);
    }

    // ---------------------------------------------------------------- UI

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LocalHostFilter.Refresh();
        _monitor.Start();
        LoadAdapters();
        InitLanguageCombo();
        UpdateNpcapBanner();
        UpdateResidualBanner();

        var wanted = _settings.SelectedAdapterId;
        var item = string.IsNullOrEmpty(wanted)
            ? AdapterCombo.Items.OfType<AdapterInfo>().FirstOrDefault()
            : AdapterCombo.Items.OfType<AdapterInfo>().FirstOrDefault(a => a.Id == wanted)
              ?? AdapterCombo.Items.OfType<AdapterInfo>().FirstOrDefault();
        if (item is not null)
        {
            AdapterCombo.SelectedItem = item;
        }
        else
        {
            StatusBar.Text = Loc.Get("Common.NoAdapterDetected");
        }

        // Démarrage déterministe de la détection : LoadAdapters sélectionne la carte
        // pendant la suppression des événements, et re-sélectionner la même carte ne
        // déclenche PAS SelectionChanged — RestartDetection doit donc être appelé
        // explicitement (sinon aucun service ne démarre et la liste reste vide).
        if (AdapterCombo.SelectedItem is not null)
        {
            RestartDetection();
        }

        _suppressExternalCheckEvents = true;
        UseExternalBrowserCheck.IsChecked = _settings.UseExternalBrowser;
        _suppressExternalCheckEvents = false;

        LoadDhcpConfig();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        _statusClock.Stop();
        _passiveListener.Dispose();
        _arpCapture.Dispose();
        _apipaScan.Dispose();
        _monitor.Dispose();
        try
        {
            // Fermer la fenêtre SANS avoir désactivé le DHCP doit tout ranger :
            // même routine que le bouton « Restaurer la config réseau » — serveur
            // DHCP coupé, socket fermée (port 67 re-bindable immédiatement),
            // tous les alias retirés. Les baux restent en table, marqués
            // « serveur arrêté » (filet d'information) ; la détection de résidus
            // au prochain lancement reste le filet pour le cas du crash.
            RestoreNetworkCore();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.Fmt("Status.CleanupFailed", ex.Message),
                "Device Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _dhcpServer.Dispose();
        _settings.Save();
    }

    // ---------------------------------------------------------------- Langue

    /// <summary>
    /// Sélectionne l'entrée du sélecteur correspondant à la langue active, SANS
    /// persister : tant que l'utilisateur n'a pas choisi explicitement, le réglage
    /// reste « auto » (suit la langue de l'OS à chaque lancement).
    /// </summary>
    private void InitLanguageCombo()
    {
        _suppressLanguageEvents = true;
        try
        {
            foreach (var item in LanguageCombo.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag as string) == Loc.Current)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _suppressLanguageEvents = false;
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvents || IsLoaded is false)
        {
            return;
        }
        var code = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(code))
        {
            return;
        }
        // Choix explicite : persisté puis appliqué à chaud (fenêtre, listes, statut).
        _settings.Language = code;
        _settings.Save();
        Loc.SetLanguage(code);
    }

    // ---------------------------------------------------------------- Cartes

    private void LoadAdapters()
    {
        _suppressAdapterEvents = true;
        try
        {
            var previous = (AdapterCombo.SelectedItem as AdapterInfo)?.Id;
            var adapters = NetworkAdapterService.GetEthernetAdapters();
            AdapterCombo.ItemsSource = adapters;
            var toSelect = adapters.FirstOrDefault(a => a.Id == previous)
                           ?? adapters.FirstOrDefault();
            AdapterCombo.SelectedItem = toSelect;
        }
        finally
        {
            _suppressAdapterEvents = false;
        }
    }

    private void RefreshAdaptersButton_Click(object sender, RoutedEventArgs e)
    {
        LoadAdapters();
        UpdateNpcapBanner();
        if (AdapterCombo.SelectedItem is not null)
        {
            RestartDetection();
        }
        StatusBar.Text = Loc.Get("Status.AdaptersRefreshed");
    }

    private void AdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAdapterEvents)
        {
            return;
        }
        RestartDetection();
    }

    private void RestartDetection()
    {
        _selectedAdapter = AdapterCombo.SelectedItem as AdapterInfo;
        if (_selectedAdapter is null)
        {
            StatusBar.Text = Loc.Get("Common.SelectAdapterToWatch");
            return;
        }
        _settings.SelectedAdapterId = _selectedAdapter.Id;

        // Le serveur DHCP écoute sur la carte précédente : il doit être coupé et
        // relancé explicitement sur la nouvelle (l'utilisateur le réactive d'un clic).
        if (_dhcpServer.IsRunning)
        {
            _dhcpServer.Stop();
            UncheckDhcpQuiet();
            _ = Task.Run(() => _aliasService.RemoveAliasesWithMac(AliasIpService.DhcpMarkerMac));
            UpdateDhcpBanner();
            UpdateDhcpLeases();
        }

        _passiveListener.Start(_selectedAdapter);
        _apipaScan.Start(_selectedAdapter);

        if (NpcapService.IsInstalled())
        {
            if (_arpCapture.Start(_selectedAdapter))
            {
                StatusBar.Text = Loc.Fmt("Status.DetectionActive", _selectedAdapter.Name);
            }
            else
            {
                StatusBar.Text = Loc.Fmt("Status.DetectionPartial", _arpCapture.LastError);
            }
        }
        else
        {
            StatusBar.Text = Loc.Fmt("Status.DetectionNoNpcap", _selectedAdapter.Name);
        }
    }

    // ---------------------------------------------------------------- Npcap

    private void UpdateNpcapBanner()
    {
        if (NpcapService.IsInstalled())
        {
            NpcapStatusBorder.Visibility = Visibility.Collapsed;
            return;
        }
        NpcapStatusBorder.Visibility = Visibility.Visible;
        InstallNpcapButton.IsEnabled = true;
        InstallNpcapButton.Content = Loc.Fmt("Ui.InstallNpcapWithFile", "npcap-1.88.exe");
        NpcapStatusText.Text = Loc.Get("Npcap.BannerHowTo");
    }

    private async void InstallNpcapButton_Click(object sender, RoutedEventArgs e)
    {
        var installer = await Task.Run(() => NpcapService.FindInstaller());
        if (installer is null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("Npcap.OpenDialog.Title"),
                Filter = Loc.Get("Npcap.OpenDialog.Filter"),
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            installer = dialog.FileName;
        }

        InstallNpcapButton.IsEnabled = false;
        InstallNpcapButton.Content = Loc.Get("Npcap.Installing");
        StatusBar.Text = Loc.Fmt("Npcap.StatusInstalling", Path.GetFileName(installer));

        var result = await Task.Run(() => NpcapService.Install(installer, silent: false));
        UpdateNpcapBanner();
        if (result.Success)
        {
            StatusBar.Text = Loc.Fmt("Npcap.RestartCapture", result.Message);
            RestartDetection();
        }
        else
        {
            MessageBox.Show(this, result.Message, Loc.Get("Npcap.DialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusBar.Text = result.Message;
        }
    }

    // ---------------------------------------------------------------- Alias résiduels et restauration

    /// <summary>Bandeau « alias résiduels d'une session précédente » (crash, kill).</summary>
    private void UpdateResidualBanner()
    {
        var aliases = _aliasService.GetAliases();
        if (aliases.Count == 0)
        {
            ResidualAliasesBorder.Visibility = Visibility.Collapsed;
            return;
        }
        ResidualAliasesBorder.Visibility = Visibility.Visible;
        ResidualAliasesText.Text = Loc.Fmt("Banner.Residual", aliases.Count,
            string.Join(", ", aliases.Select(a => Loc.Fmt("Alias.IpOnAdapter", a.Ip, a.AdapterName))));
    }

    private void CleanResidualButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _aliasService.RemoveAllAliases();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Fmt("Status.ResidualRemoveFailed", ex.Message),
                "Device Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateResidualBanner();
        StatusBar.Text = Loc.Get("Status.ResidualRemoved");
    }

    /// <summary>
    /// Routine unique de remise en état réseau : coupe le serveur DHCP (arrêt des
    /// réponses, fermeture socket — le port 67 doit être re-bindable immédiatement),
    /// puis retire tous les alias posés par l'application, celui du serveur DHCP
    /// compris. Appelée par le bouton « Restaurer la config réseau » ET par la
    /// fermeture de la fenêtre : jamais deux chemins de code pour le même nettoyage.
    /// </summary>
    private void RestoreNetworkCore()
    {
        // Coupe d'abord le serveur DHCP (arrêt des réponses), puis suppression de
        // tous les alias tracés — l'adresse du serveur en fait partie.
        _dhcpServer.Stop();
        UncheckDhcpQuiet();
        _aliasService.RemoveAllAliases();
        UpdateDhcpBanner();
        UpdateDhcpLeases();
        UpdateResidualBanner();
    }

    private void RestoreNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        var aliases = _aliasService.GetAliases();
        string aliasDetail = aliases.Count == 0
            ? Loc.Get("Restore.NoAlias")
            : string.Join("\n", aliases.Select(a => Loc.Fmt("Alias.BulletIpOnAdapter", a.Ip, a.AdapterName)));
        string dhcpDetail = _dhcpServer.IsRunning
            ? Loc.Fmt("Restore.DhcpActiveDetail", _dhcpServer.AdapterName,
                _dhcpServer.PoolStartText, _dhcpServer.PoolEndText)
            : Loc.Get("Restore.DhcpInactiveDetail");
        var answer = MessageBox.Show(this,
            Loc.Fmt("Restore.Confirm", Loc.Get("Restore.WhatHeader"), aliasDetail, dhcpDetail),
            Loc.Get("Restore.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            RestoreNetworkCore();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Fmt("Restore.Failed", ex.Message),
                "Device Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StatusBar.Text = Loc.Get("Restore.Done");
    }

    // ---------------------------------------------------------------- Serveur DHCP (option)

    /// <summary>Remplit les champs de configuration DHCP depuis les paramètres.</summary>
    private void LoadDhcpConfig()
    {
        _suppressDhcpEvents = true;
        try
        {
            DhcpPoolStartBox.Text = _settings.DhcpPoolStart;
            DhcpPoolEndBox.Text = _settings.DhcpPoolEnd;
            DhcpServerIpBox.Text = _settings.DhcpServerIp;
            SelectLeaseComboItem(_settings.DhcpLeaseSeconds);
        }
        finally
        {
            _suppressDhcpEvents = false;
        }
        ApplyDhcpCountersLayout();
        UpdateDhcpBanner();
        UpdateDhcpLeases();
    }

    /// <summary> Sélectionne la durée de bail la plus proche de la valeur persistée.</summary>
    private void SelectLeaseComboItem(int seconds)
    {
        var item = DhcpLeaseCombo.Items.OfType<ComboBoxItem>()
            .OrderBy(i => Math.Abs(GetLeaseSecondsFromItem(i) - seconds))
            .FirstOrDefault();
        if (item is not null)
        {
            DhcpLeaseCombo.SelectedItem = item;
        }
    }

    private static int GetLeaseSecondsFromItem(ComboBoxItem item) =>
        int.TryParse(item.Tag as string, out var seconds)
            ? Math.Max(DhcpServerService.MinLeaseSeconds, seconds)
            : DhcpServerService.MinLeaseSeconds;

    private void DhcpLeaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDhcpEvents || DhcpLeaseCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        int seconds = GetLeaseSecondsFromItem(item);
        bool changed = _settings.DhcpLeaseSeconds != seconds;
        _settings.DhcpLeaseSeconds = seconds;
        _settings.Save();
        if (changed && _dhcpServer.IsRunning && _dhcpServer.LeaseSeconds != seconds)
        {
            StatusBar.Text = Loc.Fmt("Dhcp.LeaseComboHint", item.Content);
        }
    }

    /// <summary>
    /// « ⓘ » : ouvre la fenêtre d'aide dédiée à la persistance du bail (concept
    /// de sécurité — l'équipement garde son IP après coupure du serveur). Le clic
    /// est le mécanisme principal ; le survol ne garde qu'un raccourci d'une ligne.
    /// </summary>
    private void LeaseHelpButton_Click(object sender, RoutedEventArgs e)
    {
        var help = new LeaseHelpWindow { Owner = this };
        help.ShowDialog();
    }

    /// <summary>
    /// Applique la disposition de la ligne DHCP de la barre de statut : compacte
    /// (défaut) ou détaillée (compteurs). Le choix est persisté entre lancements.
    /// </summary>
    private void ApplyDhcpCountersLayout()
    {
        bool expanded = _settings.DhcpDetailedCounters;
        DhcpDetailedStatus.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        DhcpCountersToggleButton.Content = expanded ? "▴" : "▾";
    }

    private void DhcpCountersToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DhcpDetailedCounters = !_settings.DhcpDetailedCounters;
        _settings.Save();
        ApplyDhcpCountersLayout();
    }

    private void DhcpConfigBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveDhcpConfig();
        }
    }

    private void DhcpConfigBox_LostFocus(object sender, RoutedEventArgs e) => SaveDhcpConfig();

    /// <summary>Persiste la configuration du pool (elle est revalidée à l'activation).</summary>
    private void SaveDhcpConfig()
    {
        if (_suppressDhcpEvents)
        {
            return;
        }
        _settings.DhcpPoolStart = (DhcpPoolStartBox.Text ?? "").Trim();
        _settings.DhcpPoolEnd = (DhcpPoolEndBox.Text ?? "").Trim();
        _settings.DhcpServerIp = (DhcpServerIpBox.Text ?? "").Trim();
        _settings.Save();
    }

    /// <summary>
    /// Valide la configuration saisie. Le pool et l'IP du PC doivent être dans un même
    /// /24 (le masque attribué est 255.255.255.0) ; la validation complète (adresses
    /// réservées, taille, exclusion des IP locales) est déléguée au service.
    /// </summary>
    private bool TryGetDhcpConfig(out IPAddress serverIp, out IPAddress poolStart, out IPAddress poolEnd, out string? error)
    {
        serverIp = IPAddress.Any;
        poolStart = IPAddress.Any;
        poolEnd = IPAddress.Any;
        error = null;
        if (!IPAddress.TryParse((DhcpPoolStartBox.Text ?? "").Trim(), out var parsedStart))
        {
            error = Loc.Get("Dhcp.BadPoolStart");
            return false;
        }
        if (!IPAddress.TryParse((DhcpPoolEndBox.Text ?? "").Trim(), out var parsedEnd))
        {
            error = Loc.Get("Dhcp.BadPoolEnd");
            return false;
        }
        if (!IPAddress.TryParse((DhcpServerIpBox.Text ?? "").Trim(), out var parsedServer))
        {
            error = Loc.Get("Dhcp.BadServerIp");
            return false;
        }
        if (!DhcpServerService.IsValidPool(parsedServer, parsedStart, parsedEnd, out var poolError))
        {
            error = poolError;
            return false;
        }
        serverIp = parsedServer;
        poolStart = parsedStart;
        poolEnd = parsedEnd;
        return true;
    }

    private async void DhcpEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDhcpEvents)
        {
            return;
        }
        if (DhcpEnabledCheck.IsChecked == true)
        {
            await EnableDhcpAsync();
        }
        else
        {
            await DisableDhcpAsync();
        }
    }

    /// <summary>« Appliquer » : enregistre la configuration et (re)démarre le serveur si actif.</summary>
    private async void DhcpApplyButton_Click(object sender, RoutedEventArgs e)
    {
        SaveDhcpConfig();
        if (!_dhcpServer.IsRunning)
        {
            StatusBar.Text = Loc.Get("Dhcp.ConfigSavedInactive");
            return;
        }
        // Relance avec la nouvelle configuration.
        UncheckDhcpQuiet();
        await DisableDhcpAsync();
        UncheckDhcpQuiet();
        _suppressDhcpEvents = true;
        DhcpEnabledCheck.IsChecked = true;
        _suppressDhcpEvents = false;
        await EnableDhcpAsync();
    }

    private async void DhcpDisableButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_dhcpServer.IsRunning)
        {
            return;
        }
        UncheckDhcpQuiet();
        await DisableDhcpAsync();
    }

    private async Task EnableDhcpAsync()
    {
        if (_dhcpBusy || _closing)
        {
            return;
        }
        if (_selectedAdapter is null)
        {
            StatusBar.Text = Loc.Get("Common.SelectAdapterFirst");
            UncheckDhcpQuiet();
            return;
        }
        if (!TryGetDhcpConfig(out var serverIp, out var poolStart, out var poolEnd, out var error))
        {
            StatusBar.Text = error ?? Loc.Get("Dhcp.InvalidConfig");
            UncheckDhcpQuiet();
            return;
        }
        if (_dhcpServer.IsRunning)
        {
            return;
        }

        _dhcpBusy = true;
        DhcpEnabledCheck.IsEnabled = false;
        try
        {
            var adapter = _selectedAdapter;
            StatusBar.Text = Loc.Fmt("Dhcp.EnsuringAddr", serverIp, adapter.Name);
            var ensure = await Task.Run(() => EnsureDhcpServerAddress(adapter, serverIp));
            switch (ensure)
            {
                case DhcpEnsureResult.Cancelled:
                    StatusBar.Text = Loc.Get("Dhcp.CancelledByConflict");
                    UncheckDhcpQuiet();
                    return;
                case DhcpEnsureResult.Failed:
                    StatusBar.Text = Loc.Fmt("Dhcp.EnsureFailed", serverIp, adapter.Name);
                    UncheckDhcpQuiet();
                    return;
            }

            // Pare-feu : sans règle « UDP 67 entrant », le pare-feu Windows jette les
            // DHCPDISCOVER silencieusement (bind réussi, aucune trame reçue). La règle
            // est vérifiée/recréée pour le chemin courant de l'exécutable — plus
            // d'échec muet, l'état est explicitement guidé dans la barre de statut.
            var firewall = await Task.Run(() =>
            {
                try
                {
                    return DhcpServerService.EnsureInboundUdp67Rule();
                }
                catch (Exception ex)
                {
                    return new DhcpServerService.FirewallRuleResult(false,
                        Loc.Fmt("Dhcp.FirewallCheckFailed", ex.Message));
                }
            });

            if (_dhcpServer.Start(adapter, serverIp, poolStart, poolEnd, _settings.DhcpLeaseSeconds))
            {
                string fwNote = firewall.Ok
                    ? firewall.Message
                    : Loc.Fmt("Dhcp.FirewallWarning", firewall.Message);
                StatusBar.Text = Loc.Fmt("Dhcp.ActiveStatus",
                    adapter.Name, poolStart, poolEnd, serverIp,
                    FormatLeaseSeconds(_dhcpServer.LeaseSeconds), fwNote);
            }
            else
            {
                StatusBar.Text = Loc.Fmt("Dhcp.StartFailed", _dhcpServer.LastError);
                UncheckDhcpQuiet();
            }
        }
        finally
        {
            _dhcpBusy = false;
            DhcpEnabledCheck.IsEnabled = true;
            UpdateDhcpBanner();
            UpdateDhcpLeases();
        }
    }

    /// <summary>Coupe le serveur et retire son adresse (alias marqué) de la carte.</summary>
    private async Task DisableDhcpAsync()
    {
        if (_dhcpBusy || _closing)
        {
            return;
        }
        _dhcpBusy = true;
        DhcpEnabledCheck.IsEnabled = false;
        try
        {
            bool wasRunning = _dhcpServer.IsRunning;
            _dhcpServer.Stop();
            await Task.Run(() => _aliasService.RemoveAliasesWithMac(AliasIpService.DhcpMarkerMac));
            if (wasRunning)
            {
                StatusBar.Text = Loc.Get("Dhcp.Disabled");
            }
        }
        finally
        {
            _dhcpBusy = false;
            DhcpEnabledCheck.IsEnabled = true;
            UpdateDhcpBanner();
            UpdateDhcpLeases();
        }
    }

    private void UncheckDhcpQuiet()
    {
        _suppressDhcpEvents = true;
        try
        {
            DhcpEnabledCheck.IsChecked = false;
        }
        finally
        {
            _suppressDhcpEvents = false;
        }
    }

    private enum DhcpEnsureResult
    {
        Ok,
        Cancelled,
        Failed,
    }

    /// <summary>
    /// Garantit que l'IP du PC existe sur la carte sélectionnée (nécessaire pour écouter
    /// et servir de passerelle) : réutilise l'IP si elle y figure déjà, sinon la pose en
    /// alias marqué — avec confirmation si le /24 chevauche une autre carte active.
    /// </summary>
    private DhcpEnsureResult EnsureDhcpServerAddress(AdapterInfo adapter, IPAddress serverIp)
    {
        var text = serverIp.ToString();
        if (adapter.Ipv4Addresses.Any(ip => ip.Equals(serverIp)))
        {
            return DhcpEnsureResult.Ok;
        }
        if (_aliasService.GetAliases().Any(a =>
                a.Ip == text && string.Equals(a.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return DhcpEnsureResult.Ok;
        }

        if (NetworkAdapterService.HasSubnetConflictOnOtherAdapter(adapter.Id, serverIp, out var conflict))
        {
            bool go = MessageBox.Show(this,
                Loc.Fmt("Conflict.DhcpConfirm", conflict, serverIp, adapter.Name),
                Loc.Get("Conflict.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!go)
            {
                return DhcpEnsureResult.Cancelled;
            }
        }

        return _aliasService.AddAlias(adapter, text, AliasIpService.DhcpMarkerMac, verify: true)
            ? DhcpEnsureResult.Ok
            : DhcpEnsureResult.Failed;
    }

    /// <summary>Bandeau d'avertissement permanent tant que le serveur DHCP tourne.</summary>
    private void UpdateDhcpBanner()
    {
        if (!_dhcpServer.IsRunning)
        {
            DhcpActiveBorder.Visibility = Visibility.Collapsed;
            return;
        }
        DhcpActiveBorder.Visibility = Visibility.Visible;
        DhcpActiveText.Text = Loc.Fmt("Dhcp.BannerActive",
            _dhcpServer.AdapterName, _dhcpServer.PoolStartText, _dhcpServer.PoolEndText,
            _dhcpServer.ServerIpText);
    }

    private void UpdateDhcpLeases() => DhcpLeasesView.ItemsSource = _dhcpServer.GetLeaseViews();

    // ---------------------------------------------------------------- Liste et panneau de détail

    private void DeviceListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = DeviceListView.SelectedItem is Device;
        OpenWebButton.IsEnabled = selected;
        OpenExternalButton.IsEnabled = selected;
        CopyMacButton.IsEnabled = selected;
        CopyIpButton.IsEnabled = selected;
        UpdateDetailPanel(DeviceListView.SelectedItem as Device);
    }

    private void DeviceListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedDevice();
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            LoadAdapters();
        }
    }

    private void DeviceListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<ListViewItem>(source) is not null)
        {
            OpenSelectedDevice();
        }
    }

    /// <summary>Remplit le panneau de détail (MAC, constructeur, IP, mode, historique, réglages web).</summary>
    private void UpdateDetailPanel(Device? device)
    {
        _suppressDetailEvents = true;
        try
        {
            if (device is null)
            {
                DetailMac.Text = "—";
                DetailVendor.Text = Loc.Get("Detail.SelectDevice");
                DetailIp.Text = "—";
                DetailMode.Text = "—";
                DetailStatus.Text = "—";
                DetailFirstSeen.Text = "—";
                DetailLastSeen.Text = "—";
                DetailAlias.Text = "—";
                DetailHistoryView.ItemsSource = null;
                DetailHistoryEmpty.Visibility = Visibility.Visible;
                DetailUrlPreview.Text = "http://—/";
                return;
            }

            DetailMac.Text = device.Mac;
            DetailVendor.Text = device.Vendor;
            DetailIp.Text = device.IpDisplay;
            DetailMode.Text = device.ModeDisplay;
            DetailStatus.Text = device.StatusText;
            DetailFirstSeen.Text = device.FirstSeen.ToString("dd/MM/yyyy HH:mm:ss");
            DetailLastSeen.Text = device.LastSeen.ToString("HH:mm:ss");

            var alias = _aliasService.GetAliases()
                .FirstOrDefault(a => string.Equals(a.DeviceMac, device.Mac, StringComparison.OrdinalIgnoreCase))
                ?? _aliasService.GetAliases().FirstOrDefault(a =>
                    a.DeviceMac != AliasIpService.DhcpMarkerMac
                    && a.Ip is not null && device.CurrentIp is not null
                    && a.Ip.Split('.').Take(3).SequenceEqual(device.CurrentIp.Split('.').Take(3)));
            DetailAlias.Text = alias?.Ip ?? Loc.Get("Detail.NoAlias");

            var history = device.IpHistory.Reverse().ToList();
            DetailHistoryView.ItemsSource = history;
            DetailHistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var config = GetWebConfig(device);
            ProtocolCombo.SelectedIndex = config.Protocol == "https" ? 1 : 0;
            PortCombo.Text = config.Port > 0 ? config.Port.ToString() : "80";
            UpdateUrlPreview(device);
        }
        finally
        {
            _suppressDetailEvents = false;
        }
    }

    private Device? SelectedDevice => DeviceListView.SelectedItem as Device;

    /// <summary>Réglages web (protocole, port) persistés pour un device.</summary>
    private DeviceWebConfig GetWebConfig(Device device)
    {
        if (_settings.DeviceWeb.TryGetValue(device.Mac, out var config))
        {
            return config;
        }
        return new DeviceWebConfig();
    }

    private void UpdateUrlPreview(Device? device)
    {
        if (device?.CurrentIp is null)
        {
            DetailUrlPreview.Text = "http://—/";
            return;
        }
        DetailUrlPreview.Text = DeviceOpenerService.BuildUrl(
            device.CurrentIp, GetProtocol(), GetPort());
    }

    private string GetProtocol() =>
        (ProtocolCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "https" ? "https" : "http";

    private int GetPort()
    {
        var text = PortCombo.Text?.Trim() ?? "";
        return int.TryParse(text, out var port) && port > 0 && port <= 65535 ? port : 80;
    }

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Peut être levé pendant InitializeComponent (ComboBoxItem IsSelected=True)
        // avant que les autres contrôles du panneau ne soient créés.
        if (_suppressDetailEvents || PortCombo is null || !IsLoaded)
        {
            return;
        }
        // Confort : basculer http↔https échange le port si c'est celui par défaut de l'autre protocole.
        int port = GetPort();
        if (GetProtocol() == "https" && port == 80)
        {
            _suppressDetailEvents = true;
            PortCombo.Text = "443";
            _suppressDetailEvents = false;
        }
        else if (GetProtocol() == "http" && port == 443)
        {
            _suppressDetailEvents = true;
            PortCombo.Text = "80";
            _suppressDetailEvents = false;
        }
        SaveWebConfig();
    }

    private void PortCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveWebConfig();
        }
    }

    private void PortCombo_LostFocus(object sender, RoutedEventArgs e) => SaveWebConfig();

    /// <summary>Persiste les réglages web du device sélectionné (clé = MAC).</summary>
    private void SaveWebConfig()
    {
        if (_suppressDetailEvents || SelectedDevice is not Device device)
        {
            return;
        }
        _settings.DeviceWeb[device.Mac] = new DeviceWebConfig
        {
            Protocol = GetProtocol(),
            Port = GetPort(),
        };
        _settings.Save();
        UpdateUrlPreview(device);
    }

    // ---------------------------------------------------------------- Ouverture des pages web

    private void OpenWebButton_Click(object sender, RoutedEventArgs e) => OpenSelectedDevice();

    private void CopyMacButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceListView.SelectedItem is Device device)
        {
            Clipboard.SetText(device.Mac);
            StatusBar.Text = Loc.Fmt("Status.MacCopied", device.Mac);
        }
    }

    private void CopyIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceListView.SelectedItem is Device device)
        {
            Clipboard.SetText(device.CurrentIp ?? "");
            StatusBar.Text = Loc.Fmt("Status.IpCopied", device.CurrentIp ?? "—");
        }
    }

    private async void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not Device device || device.CurrentIp is null)
        {
            return;
        }
        var config = GetWebConfig(device);
        await OpenUrlAsync(DeviceOpenerService.BuildUrl(device.CurrentIp, config.Protocol, config.Port));
        StatusBar.Text = Loc.Get("Status.PageOpenedExternal");
    }

    private async void OpenSelectedDevice()
    {
        if (_openingDevice || _closing)
        {
            return;
        }
        if (DeviceListView.SelectedItem is not Device device)
        {
            return;
        }
        if (_selectedAdapter is null)
        {
            MessageBox.Show(this, Loc.Get("Common.SelectAdapterFirst"), "Device Finder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (device.CurrentIp is null)
        {
            StatusBar.Text = Loc.Get("Open.NoIpYet");
            return;
        }

        _openingDevice = true;
        OpenWebButton.IsEnabled = false;
        try
        {
            var config = GetWebConfig(device);
            var result = await _opener.OpenDeviceAsync(
                _selectedAdapter,
                device,
                config.Protocol,
                config.Port,
                message => MessageBox.Show(this, message, Loc.Get("Conflict.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
                message => Dispatcher.BeginInvoke(() => StatusBar.Text = message));
            switch (result.Outcome)
            {
                case OpenOutcome.OpenedDirect:
                    await OpenUrlAsync(result.Url!);
                    StatusBar.Text = Loc.Fmt("Status.PageOpened", result.Url);
                    break;
                case OpenOutcome.OpenedAfterAlias:
                    await OpenUrlAsync(result.Url!);
                    StatusBar.Text = result.Message ?? Loc.Fmt("Status.PageOpened", result.Url);
                    break;
                case OpenOutcome.Cancelled:
                    StatusBar.Text = Loc.Get("Open.CancelledByConflict");
                    break;
                default:
                    StatusBar.Text = result.Message ?? Loc.Get("Open.Failed");
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Fmt("Open.Error", ex.Message), "Device Finder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _openingDevice = false;
            OpenWebButton.IsEnabled = DeviceListView.SelectedItem is Device;
        }
    }

    // ---------------------------------------------------------------- IP manuelle

    private async void ManualProbeButton_Click(object sender, RoutedEventArgs e) => await ProbeManualAsync();

    private async void ManualIpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ProbeManualAsync();
        }
    }

    private async Task ProbeManualAsync()
    {
        if (_probingManual || _closing)
        {
            return;
        }
        if (_selectedAdapter is null)
        {
            MessageBox.Show(this, Loc.Get("Common.SelectAdapterFirst"), "Device Finder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ips = ManualProbeService.ParseRange(ManualIpBox.Text, out var error);
        if (ips is null)
        {
            StatusBar.Text = error ?? Loc.Get("Manual.InvalidAddress");
            return;
        }

        _probingManual = true;
        ManualProbeButton.IsEnabled = false;
        try
        {
            var adapter = _selectedAdapter;
            StatusBar.Text = Loc.Fmt("Manual.Ensuring", ips[0]);
            var ensure = await Task.Run(() => _aliasService.EnsureAliasForSubnet(
                adapter, ips[0], null,
                message => MessageBox.Show(this, message, Loc.Get("Conflict.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
                message => Dispatcher.BeginInvoke(() => StatusBar.Text = message)));
            if (ensure.Cancelled)
            {
                StatusBar.Text = Loc.Get("Manual.CancelledByConflict");
                return;
            }
            if (!ensure.Ok)
            {
                StatusBar.Text = ensure.Message ?? Loc.Get("Alias.EnsureFailed");
                return;
            }

            StatusBar.Text = Loc.Fmt("Manual.Probing", ips.Count);
            var sourceIp = ensure.AliasIp is not null
                ? System.Net.IPAddress.Parse(ensure.AliasIp)
                : adapter.PrimaryIpv4;
            var results = await ManualProbeService.ProbeAsync(ips, sourceIp);
            _manualItems.Clear();
            foreach (var r in results)
            {
                _manualItems.Add(new ManualProbeItem
                {
                    Ip = r.Ip,
                    Mac = r.Mac ?? "—",
                    Vendor = r.Mac is not null ? _oui.LookupVendor(r.Mac) : "—",
                    StatusKind = r.PingOk
                        ? ManualProbeStatus.Reachable
                        : r.Mac is not null ? ManualProbeStatus.ArpOnly : ManualProbeStatus.NoResponse,
                });
                if (r.Mac is not null)
                {
                    // Nourrit la liste principale des devices.
                    OnObservation(r.Mac, r.Ip);
                }
            }

            int alive = results.Count(r => r.PingOk || r.Mac is not null);
            StatusBar.Text = ensure.AliasIp is not null
                ? Loc.Fmt("Manual.DoneWithAlias", alive, results.Count, ensure.AliasIp)
                : Loc.Fmt("Manual.Done", alive, results.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Fmt("Manual.Error", ex.Message), "Device Finder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _probingManual = false;
            ManualProbeButton.IsEnabled = true;
        }
    }

    private async void ManualResultsView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<ListViewItem>(source) is null)
        {
            return;
        }
        if (ManualResultsView.SelectedItem is not ManualProbeItem item)
        {
            return;
        }
        await OpenUrlAsync($"http://{item.Ip}/");
        StatusBar.Text = Loc.Fmt("Status.PageOpened", $"http://{item.Ip}/");
    }

    // ---------------------------------------------------------------- Navigateur intégré (WebView2)

    /// <summary>
    /// Vrai si le runtime WebView2 est présent sur la machine. S'il manque, les pages
    /// basculent automatiquement vers le navigateur externe avec un message explicite —
    /// aucun téléchargement n'est tenté (application 100 % hors-ligne).
    /// </summary>
    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureBrowserAsync()
    {
        if (_browserInitialized)
        {
            return true;
        }
        if (_browserUnavailable || !IsWebView2RuntimeAvailable())
        {
            _browserUnavailable = true;
            BrowserUnavailablePanel.Visibility = Visibility.Visible;
            return false;
        }
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.SourceChanged += (_, _) =>
            {
                AddressBox.Text = Browser.Source?.ToString() ?? "";
                UpdateNavigationButtons();
            };
            Browser.CoreWebView2.NavigationCompleted += (_, _) => UpdateNavigationButtons();
            _browserInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _browserUnavailable = true;
            BrowserUnavailablePanel.Visibility = Visibility.Visible;
            BrowserUnavailableText.Text = Loc.Fmt("Browser.RuntimeFailed", ex.Message);
            return false;
        }
    }

    private void UpdateNavigationButtons()
    {
        BrowserBackButton.IsEnabled = Browser.CoreWebView2?.CanGoBack ?? false;
        BrowserForwardButton.IsEnabled = Browser.CoreWebView2?.CanGoForward ?? false;
    }

    /// <summary>
    /// Ouvre une URL dans le navigateur intégré (onglet « Navigateur »), ou dans le
    /// navigateur externe si demandé par le réglage ou si le runtime WebView2 manque.
    /// </summary>
    private async Task OpenUrlAsync(string url)
    {
        bool wantExternal = _settings.UseExternalBrowser || !await EnsureBrowserAsync();
        if (!wantExternal)
        {
            MainTabs.SelectedIndex = 1;
            Browser.CoreWebView2!.Navigate(url);
            AddressBox.Text = url;
            return;
        }
        if (!_settings.UseExternalBrowser)
        {
            // Runtime WebView2 absent : bascule explicite, jamais de téléchargement tenté.
            StatusBar.Text = Loc.Get("Browser.MissingRuntimeStatus");
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Fmt("Browser.OpenFailed", ex.Message), "Device Finder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddressGoButton_Click(object sender, RoutedEventArgs e) => await NavigateAddressBoxAsync();

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateAddressBoxAsync();
        }
    }

    private async Task NavigateAddressBoxAsync()
    {
        var text = (AddressBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return;
        }
        var url = text.Contains("://", StringComparison.Ordinal) ? text : $"http://{text}";
        if (!await EnsureBrowserAsync())
        {
            StatusBar.Text = Loc.Get("Browser.MissingRuntimeInput");
            return;
        }
        Browser.CoreWebView2!.Navigate(url);
    }

    private void BrowserBackButton_Click(object sender, RoutedEventArgs e) =>
        Browser.CoreWebView2?.GoBack();

    private void BrowserForwardButton_Click(object sender, RoutedEventArgs e) =>
        Browser.CoreWebView2?.GoForward();

    private void BrowserReloadButton_Click(object sender, RoutedEventArgs e) =>
        Browser.CoreWebView2?.Reload();

    private void OpenInExternalButton_Click(object sender, RoutedEventArgs e)
    {
        var url = Browser.Source?.ToString();
        if (string.IsNullOrEmpty(url))
        {
            url = (AddressBox.Text ?? "").Trim();
            if (url.Length == 0)
            {
                return;
            }
            url = url.Contains("://", StringComparison.Ordinal) ? url : $"http://{url}";
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Fmt("Browser.OpenFailed", ex.Message), "Device Finder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UseExternalBrowserCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressExternalCheckEvents)
        {
            return;
        }
        _settings.UseExternalBrowser = UseExternalBrowserCheck.IsChecked == true;
        _settings.Save();
    }

    // ---------------------------------------------------------------- Utilitaires

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
