# Device Finder — RJ45 device detection

> English documentation. Version française : `README.fr.md`.

A Windows desktop application (C# / .NET 8, WPF) for technicians who set up a
brand-new or factory-reset network device (IP camera, router, switch, access
point, NVR…) plugged **directly into the PC's Ethernet port**: it detects the
device by its **MAC address** whatever its IP state (fixed IP outside the local
subnet, DHCP with no server, APIPA) and opens its web administration page in
one click.

## Hybrid development environment

- The code is written and compiled from Linux: `EnableWindowsTargeting=true`
  in the project file lets `dotnet build` produce WPF binaries.
- Execution and testing happen on Windows only (VM or physical PC).

## Internationalization (v1.0)

The UI ships in **French and English**. The language selector at the top of
the window switches **on the fly** (no restart); the choice is persisted in
`%APPDATA%\DeviceFinder\settings.json`. Until you pick explicitly, the
setting stays `auto` and follows the **OS language** at every launch. The
installer is bilingual too, pre-selecting the OS language.

## Build and publishing

```bash
# Build (control from Linux)
dotnet build DeviceFinder.sln

# Test deliverable: self-contained publish, no .NET to preinstall on the target
# (-p:DebugType=none: no .pdb emitted, so no build-machine paths in the
#  deliverable)
dotnet publish src/DeviceFinder.App/DeviceFinder.App.csproj -c Release -r win-x64 \
    --self-contained true -p:DebugType=none -o publish/DeviceFinder-win-x64
```

The folder to copy to the Windows PC is `publish/DeviceFinder-win-x64/`
(`DeviceFinder.exe` + the embedded .NET 8 runtime). The application requires
UAC elevation (`requireAdministrator` manifest) because it changes the network
configuration.

### Generating the two release deliverables (on the bench Windows PC)

```powershell
# Prerequisites: .NET 8 SDK and Inno Setup 6 — no internet connection required
powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
```

The script publishes self-contained without PDBs, reads the version from the
published assembly, then produces in `publish\`:

- **`DeviceFinder-Setup.exe`** — all-in-one Inno Setup installer: installs the
  app into Program Files (or a folder of your choice), creates Start menu and
  desktop shortcuts, offers an **"Install Npcap" checkbox checked by default**
  (Npcap wizard launched at the end of installation), and a clean uninstaller.
  Nothing to download: the .NET 8 runtime and the Npcap installer are embedded
  in the Setup. Npcap is **not** removed when uninstalling the app (it has its
  own uninstaller and may serve other tools).
- **`DeviceFinder-portable-win-x64.zip`** — portable version: a folder to
  unzip on a USB stick and run directly (`DeviceFinder.exe`), no installation,
  no prerequisites (current bench mode; the Npcap installer stays embedded in
  the exe, extractable on first need via the dedicated button).

## If Windows blocks the application

`DeviceFinder.exe` and `DeviceFinder-Setup.exe` are not digitally signed (a
signing certificate is expensive and nothing here requires one). Without a
signature or "reputation", Windows may show a blue screen **"Windows protected
your PC"** (SmartScreen) or, on a recent Windows 11, a Smart App Control
block. This is a precaution filter based on file notoriety, not a malware
detection or a problem in the application: the binary comes from your own
build chain.

- **Verify** the file is yours: right-click the exe → Properties → Details tab
  (product "Device Finder", publisher "Tiboria", expected version), and
  compare its size between the stick and `publish\`.
- **Allow** (SmartScreen): on the blue screen, click "More info" then
  "Run anyway".
- **Allow** (Smart App Control): Settings → Privacy & security → Windows
  Security → App & browser control → Smart App Control settings → turn it
  off (it is locked for a while after Windows setup; if the toggle is greyed
  out, it unlocks after a delay or through Protection history).

Once launched a first time, the application will not trigger this block again.

## Npcap (ARP capture)

ARP frame capture requires Npcap. Without it, the application runs in
degraded mode (passive broadcast listening, APIPA scan, manual IP mode) and
shows an "ARP capture unavailable" banner.

The `npcap-1.88.exe` installer is **embedded as a resource in the
executable**: a single folder to copy on a USB stick. If Npcap is missing, the
"Install Npcap" button extracts the installer to `%TEMP%\DeviceFinder` and
launches the GUI wizard; the capture then starts without restarting the
application. In silent mode, the "install for Nmap-compatible mode" option is
not passed (therefore disabled). Npcap remains an extended-detection option,
never a prerequisite.

## How it works (summary)

- **Detection by MAC address** — four mechanisms running continuously on the
  selected Ethernet adapter: passive listening of SSDP / WS-Discovery /
  mDNS / NBNS broadcasts, ARP frame capture via Npcap (the reference
  mechanism for silent fixed-IP devices), periodic APIPA scan of
  169.254.0.0/16 via SendARP, and an optional **built-in DHCP server**
  (disabled by default; pool 192.168.99.10–192.168.99.20/24 by default,
  configurable, lease duration configurable via option 51).
- **Safety guards**: all network changes are limited to the selected adapter
  — never other adapters, default routes or DNS. Alias IPs are identified,
  listed in the config file, and all removed at application shutdown; a
  startup banner detects leftovers from a crashed session; the DHCP server
  shows a permanent warning banner and is stopped at shutdown; a "Restore
  network configuration" button cleans everything in one click.
- **Web access in one click**: double-click a device → the app checks
  reachability (ICMP ping every 5 s per device IDs the state), adds an alias
  IP in the device's /24 without asking (first free address from .100,
  conflict-checked), then opens the page in the embedded WebView2 browser or
  the external one (per-device protocol/port settings, persisted by MAC).
  Only a subnet conflict with another active adapter (Wi-Fi, VPN) asks for
  confirmation.
- **Observability**: a dedicated status line exposes adapter, ARP capture
  state, frame counters and broadcast activity; the DHCP line is collapsible
  and shows all 13 transaction counters — no silent path between reception
  and OFFER.

The detailed test-bench documentation lives in `README-banc.md` (French).

## Progress

All the features above are implemented and bench-validated through the
staged delivery (steps 1–4: detection + external browser, APIPA scan and
cleanup, WebView2 + per-device settings, built-in DHCP server). Step 5
packaging (offline installer + portable zip) is implemented, bench
acceptance pending. Step 5.5 (internationalization fr-FR/en-US, hot
language switch, bilingual installer) is implemented, bench acceptance
pending. The full step-by-step history, with every root-cause analysis of
the bench fixes, is in `README.fr.md`.

Known remaining issue: the window icon at runtime (documented as such, no fix
at this stage).

## Architecture

```
DeviceFinder.sln
├── packaging/
│   ├── DeviceFinder.iss        # Inno Setup installer script (FR/EN, Npcap embedded)
│   ├── build-release.ps1       # generates both deliverables into publish\
│   └── check-strings.sh        # i18n audit: no French literals left in code
└── src/DeviceFinder.App/
    ├── App.xaml(.cs), MainWindow.xaml(.cs), app.manifest, AssemblyInfo.cs
    ├── LeaseHelpWindow.cs        # DHCP lease help window (ⓘ button)
    ├── Localization/             # Strings.fr-FR.xaml / Strings.en-US.xaml
    ├── Models/
    │   ├── Device.cs          # device identified by MAC, WPF INPC for the UI
    │   ├── DeviceStore.cs     # thread-safe registry of detected devices
    │   └── ManualProbeItem.cs # manual IP mode result row
    └── Services/
        ├── Loc.cs                 # localization: hot-swapped dictionaries + Get/Fmt
        ├── AppSettings.cs         # settings + alias IP trace (%APPDATA%/DeviceFinder)
        ├── OuiLookup.cs           # embedded IEEE OUI database (Resources/oui*.csv)
        ├── SendArpResolver.cs     # SendARP (iphlpapi) P/Invoke
        ├── NetworkAdapterService.cs # adapter enumeration, subnet conflicts
        ├── BroadcastListenerService.cs # SSDP, WS-Discovery, mDNS, NBNS
        ├── ArpCaptureService.cs    # Npcap/SharpPcap ARP capture (promiscuous)
        ├── NpcapService.cs        # Npcap detection + embedded installer
        ├── AliasIpService.cs      # alias IPs via netsh (store=active), robust/idempotent
        ├── ApipaScanService.cs     # periodic ARP scan of 169.254.0.0/16
        ├── LocalHostFilter.cs      # local machine exclusion (MAC/IP, all adapters)
        ├── ManualProbeService.cs   # manual IP mode: range parsing + ping/SendARP probe
        ├── DhcpServerService.cs    # mini DHCP server: OFFER/ACK/NAK, leases by MAC
        ├── ReachabilityMonitor.cs  # ICMP ping every 5 s per device
        └── DeviceOpenerService.cs  # browser opening + alias logic
```
