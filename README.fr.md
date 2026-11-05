# Device Finder — détection des équipements RJ45

> Version française de la documentation. English documentation: `README.md`.

Application de bureau Windows (C# / .NET 8, WPF) destinée aux techniciens qui
configurent un équipement réseau neuf ou remis à zéro branché **directement sur la
carte Ethernet du PC** : elle détecte l'équipement par son **adresse MAC** quel que
soit son état IP (fixe hors sous-réseau, DHCP sans serveur, APIPA) et ouvre sa page
web d'administration en un clic.

## Environnement de développement hybride

- Le code est écrit et compilé depuis Linux : `EnableWindowsTargeting=true` dans le
  fichier projet permet à `dotnet build` de produire les binaires WPF.
- L'exécution et les tests se font sur Windows (VM ou PC physique) uniquement.

## Build et publication

```bash
# Compilation (contrôle sous Linux)
dotnet build DeviceFinder.sln

# Livrable de test : publication self-contained, aucun .NET à préinstaller sur la cible
# (-p:DebugType=none : aucun .pdb émis, donc aucun chemin de la machine de build
#  dans le livrable)
dotnet publish src/DeviceFinder.App/DeviceFinder.App.csproj -c Release -r win-x64 \
    --self-contained true -p:DebugType=none -o publish/DeviceFinder-win-x64
```

La sortie à copier sur le PC Windows est `publish/DeviceFinder-win-x64/`
(`DeviceFinder.exe` + le runtime .NET 8 embarqué). L'application exige l'élévation
UAC (manifest `requireAdministrator`) car elle modifie la configuration réseau.

### Génération des deux livrables de release (sur le PC Windows du banc)

```powershell
# Prérequis : SDK .NET 8 et Inno Setup 6 — aucune connexion internet requise
powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
```

Le script publie en self-contained sans PDB, lit la version dans l'assembly
publié, puis produit dans `publish\` :

- **`DeviceFinder-Setup.exe`** — installeur Inno Setup tout-en-un : installe
  l'application dans Programmes (ou le dossier de ton choix), crée les raccourcis
  menu Démarrer et bureau, propose **une case « Installer Npcap » cochée par
  défaut** (assistant Npcap lancé à la fin de l'installation), et un
  désinstalleur propre. Rien à télécharger : le runtime .NET 8 et l'installeur
  Npcap sont embarqués dans le Setup. Npcap n'est **pas** retiré à la
  désinstallation de l'app (il a son propre désinstalleur, et peut servir à
  d'autres outils).
- **`DeviceFinder-portable-win-x64.zip`** — version portable : un dossier à
  décompresser sur une clé USB et à lancer directement (`DeviceFinder.exe`),
  sans installation ni prérequis (mode banc actuel ; l'installeur Npcap reste
  embarqué dans l'exe, extractible au premier besoin via le bouton dédié).

## Si Windows bloque l'application

`DeviceFinder.exe` et `DeviceFinder-Setup.exe` ne sont pas signés numériquement
(un certificat de signature coûte cher et rien ici ne l'impose). Sans
signature ni « réputation », Windows peut afficher un écran bleu
**« Windows a protégé votre ordinateur »** (SmartScreen) ou, sur un Windows 11
récent, un blocage du **Contrôle intelligent des applications**. C'est un filtre
de précaution lié à la notoriété du fichier, pas une détection de menace ni un
problème dans l'application : le binaire vient de ta propre chaîne de build.

- **Vérifier** que le fichier vient bien de toi : clic droit sur l'exe →
  Propriétés → onglet Détails (produit « Device Finder », éditeur « Tiboria »,
  version attendue), et comparer sa taille entre la clé et `publish\`.
- **Autoriser** (SmartScreen) : dans l'écran bleu, cliquer
  « Informations complémentaires » puis « Exécuter quand même ».
- **Autoriser** (Contrôle intelligent des applications) : Paramètres →
  Confidentialité et sécurité → Sécurité Windows → Contrôle des applications et
  du navigateur → paramètres du Contrôle intelligent des applications → le
  désactiver (il est verrouillé pendant un temps après l'installation de
  Windows ; si le sélecteur est grisé, il s'ouvre après un délai ou via
  l'Historique des protections).

Une fois lancée une première fois, l'application ne redéclenchera plus ce
blocage.

## Npcap (capture ARP)

La capture des trames ARP nécessite Npcap. Sans Npcap, l'application fonctionne en
mode dégradé (écoute passive des broadcasts, scan APIPA, mode IP manuelle) et
affiche un bandeau « Capture ARP indisponible ».

L'installeur `npcap-1.88.exe` est **embarqué en ressource dans l'exécutable** :
un seul dossier à copier sur clé USB. Si Npcap est absent, le bouton
« Installer Npcap » extrait l'installeur vers `%TEMP%\DeviceFinder` et lance
l'assistant en mode GUI ; la capture démarre ensuite sans redémarrer
l'application. En mode silencieux, l'option « install for Nmap-compatible mode »
n'est pas passée (donc désactivée). Npcap reste une option de détection étendue,
jamais un prérequis.

## État d'avancement

- **Étape 1 (MVP) — validée sur PC physique** : sélection de carte Ethernet,
  écoute passive des broadcasts (SSDP, WS-Discovery/ONVIF, mDNS, NBNS), capture
  ARP via Npcap, liste des devices (MAC, constructeur via base OUI IEEE
  embarquée, IP, mode déduit, pastille joignable, dernière vue), copie MAC/IP,
  ouverture de la page web dans le navigateur externe avec ajout automatique
  d'IP alias et avertissement en cas de conflit de sous-réseau.
- **Étape 2 — validée**, durcie après le test réel du 04/11/2026 :
  - exclusion de la machine locale de la liste (toutes ses MAC et IPv4, toutes
    cartes confondues, rafraîchies périodiquement) ;
  - nettoyage des alias à la fermeture + détection au démarrage des résidus
    d'une session précédente (bandeau rouge « Supprimer ces alias ») ;
  - robustesse de l'alias : SendARP avant pose (IP locales exclues),
    double-clic idempotent (réutilisation de l'alias déjà posé dans le /24),
    vérification DAD post-pose (poll jusqu'à 4 s) — si Windows refuse l'adresse
    ou la signale en doublon, retrait et candidat suivant ;
  - mode « IP manuelle » : saisie d'une IP ou d'une petite plage
    (`10.0.0.50`, `192.168.1.50-60`, `172.16.0.10-172.16.0.20`), pose d'alias,
    sonde ping/SendARP, résultats avec MAC/constructeur, ouverture de la page
    web en double-clic — pleinement utilisable sans Npcap ;
  - scan APIPA périodique du 169.254.0.0/16 via SendARP (parallélisé à 128,
    lié à la carte sélectionnée, durée du balayage affichée) ;
  - installeur Npcap embarqué dans l'exe (extraction + assistant GUI) ;
  - bouton « Restaurer la config réseau ».
- **Correctifs issus du test réel du 04/11/2026** (PC hôte en WiFi sur le même
  LAN que le device + câble direct) :
  - parsing ARP strict : dans « who-has X tell Y » seul le couple (MAC émetteur,
    IP Y) est enregistré, jamais l'IP cible X ; dans les réponses « is-at »,
    (MAC du répondant, IP du répondant) — l'IP cherchée ne peut plus se
    retrouver attribuée à une mauvaise MAC ;
  - ciblage strict : l'écoute des broadcasts n'accepte que les datagrammes
    arrivés sur la carte sélectionnée (IP_PKTINFO) et la résolution MAC via
    SendARP part de l'IP de cette carte (plus de dérive via la WiFi/proxy-ARP) ;
    la capture ARP s'attache au handle Npcap exact de la carte ;
  - sélecteur de cartes : les cartes virtuelles (VMware, VirtualBox, Hyper-V,
    TAP/VPN, loopback, Npcap) sont filtrées, seules les cartes physiques sont
    proposées (marquées « virtuelle » en secours si aucune carte physique) ;
  - fusion par MAC : une seule entrée par MAC, l'IP confirmée la plus récente
    remplace l'ancienne (qui part dans l'historique).
- **Étape 3 — validée sur banc réel le 04/11 (commit 2aca39d)** :
  - navigateur intégré WebView2 (onglet « Navigateur » : retour/avant/actualiser,
    barre d'adresse) ; bascule en un clic vers le navigateur externe, case
    « Toujours utiliser le navigateur externe » persistée ; si le runtime
    WebView2 manque, bascule automatique vers le navigateur externe avec un
    message explicite — aucun téléchargement tenté ;
  - protocole (http/https) et port par device (liste libre : 80, 443, 8000,
    8080, 8081, 8443), persistés par MAC, aperçu de l'URL dans le panneau ;
  - panneau de détail : MAC, constructeur, IP actuelle, mode, état, première/
    dernière vue, IP alias posée pour ce device, historique des IP vues.
- **Correctif régression étape 3 (détection muette sur banc à une seule carte)** :
  - cause : la carte était sélectionnée pendant la suppression des événements,
    et re-sélectionner la même carte ne déclenche pas `SelectionChanged` —
    `RestartDetection` n'était jamais appelé au démarrage, aucun service
    d'écoute ne tournait. Il est désormais appelé explicitement au chargement
    et au rafraîchissement des cartes ;
  - match du handle Npcap sur le GUID nu (avec ou sans accolades, casse
    ignorée), avec liste des handles disponibles dans le message d'erreur ;
  - filtre d'arrivée des broadcasts : comparaison aux DEUX index de la carte
    (NET_IFINDEX via `GetAdapterIndex` P/Invoke et index IPv4 historique, qui
    peuvent différer) — plus de rejet systémique des datagrammes ;
  - observabilité permanente (ligne dédiée dans la barre de statut) : carte
    sélectionnée, état de la capture ARP (handle ouvert vs attendu, échec avec
    message, Npcap absent), compteur de trames vues/rejetées, activité des
    broadcasts (reçus/rejetés/résolus). Un incident muet n'est plus possible.
- Icône applicative : `app.ico` embarquée (icône de l'exécutable via
  `<ApplicationIcon>` et icône de la fenêtre principale).
- **Étape 4 — validée sur banc réel le 04/11 au soir : mini-serveur DHCP optionnel (désactivé par défaut)** :
  - panneau dédié dans l'onglet Détection : case d'activation, pool configurable
    (par défaut 192.168.99.10 – 192.168.99.20 /24, sous-réseau volontairement peu
    utilisé) et IP du PC sur ce /24 (192.168.99.1 par défaut), persistés dans les
    paramètres ;
  - à l'activation : pose automatique de l'IP du PC sur la carte sélectionnée en
    alias marqué (même robustesse que les alias de device : SendARP, vérification
    DAD, confirmation en cas de conflit de sous-réseau avec une autre carte active) ;
  - le serveur (UDP 67) ne répond **qu'aux DHCPDISCOVER de la transaction du pool
    (la carte sélectionnée)** : le socket écoute sur 0.0.0.0:67 et le ciblage
    de carte repose sur le scoping des réponses — l'OFFER part en broadcast
    dirigé du /24 du pool (sous-réseau qui n'existe que sur la carte
    sélectionnée, l'alias), donc un DISCOVER venu d'une autre carte (WiFi/VPN)
    ne voit jamais l'OFFER et ne peut pas compléter sa transaction ; les
    réponses (OFFER/ACK/NAK)
    partent en broadcast dirigé vers le /24 du pool : elles sortent forcément par
    la carte qui porte le sous-réseau, jamais par la route par défaut (WiFi/VPN) ;
  - transaction DHCP complète : OFFER (honore l'IP demandée si elle est libre),
    REQUEST/ACK (bail 1 h, T1/T2), NAK, DECLINE/RELEASE ; adresse d'offre choisie
    comme la première libre du pool (pas déjà allouée, pas une IP locale, aucun
    répondant SendARP) ; bail par MAC, OFFER non confirmée libérée après 60 s ;
  - la transaction révèle la MAC et l'IP attribuée : l'équipement apparaît dans la
    liste avec le mode « DHCP » (sticky : les observations ARP suivantes ne le
    requalifient pas en « Fixe ») ; la passerelle attribuée est l'IP du PC ;
  - bandeau d'avertissement permanent en haut de la fenêtre tant que le serveur
    tourne (« Serveur DHCP actif sur « carte »… ») avec bouton de désactivation ;
  - coupe automatique à la fermeture et au changement de carte ; l'adresse du
    serveur est un alias tracé : le nettoyage à la fermeture, la détection de
    résidus au démarrage et le bouton « Restaurer la config réseau » (qui affiche
    aussi l'état DHCP on/off) la couvrent ;
  - liste des baux en temps réel (MAC, IP, Offerte/Attribuée, expiration) et
    compteurs permanents dans la barre de statut ;
  - **correctif « serveur sourd » (banc, device Linux nmcli en DHCP)** :
    - cause prouvée : le socket était lié à l'IP unicast de la carte — sur
      Windows, un bind unicast ne reçoit pas les broadcasts limités
      (0.0.0.0:68 > 255.255.255.255:67), donc aucun DISCOVER n'atteignait le
      filtre IP_PKTINFO (« broadcast : 0 reçu, 0 rejeté ») ;
    - le socket écoute désormais sur 0.0.0.0:67, bind **exclusif** (sans
      SO_REUSEADDR) : si un autre serveur DHCP accapare le port 67 (service DHCP
      Windows, partage de connexion/ICS), l'échec est explicite dans la barre de
      statut au lieu d'une concurrence silencieuse ;
    - pare-feu : à chaque activation, la règle inbound « UDP 67 » de
      l'application est recréée (delete puis add, chemin d'exécutable courant —
      déterminant pour l'usage portable — tous profils dont « Réseau public ») et
      l'état est guidé dans la barre de statut si la création échoue ;
    - bug intercepté à la relecture : le balayage du pool utilisait
      `new IPAddress(uint)`, constructeur qui inverse les octets par rapport à
      l'ordre réseau (0xC0A8A80A → 10.168.168.192) — corrigé par itération
      sur la partie hôte en octets ;
    - compteurs DHCP permanents dans la barre de statut : DISCOVER reçus,
      OFFER émis, REQUEST reçus, ACK/NAK, datagrammes rejetés (autre carte,
      doit bouger hors banc), baux attribués ;
  - **correctif « boucle muette » (bis, banc nmcli + pktmon)** :
    - preuves : pktmon voit les DISCOVER arriver à la carte, un listener
      PowerShell bindé sur 0.0.0.0:67 les reçoit, l'app reste à 0 reçu —
      l'écart portait sur la primitive de réception : le serveur utilisait
      `ReceiveMessageFromAsync` (WSARecvMsg + IP_PKTINFO), resté muet face aux
      broadcasts limités ;
    - la boucle reçoit désormais par `ReceiveFromAsync` (recvfrom), la
      primitive exacte du listener de preuve ;
    - comptage brut « datagrammes reçus » incrémenté avant tout traitement ;
    - blindage : aucune exception avalée — compteur « erreurs de réception »
      + message systématique en barre de statut, le traitement d'un
      datagramme défaillant ne tue jamais la boucle ;
    - le ciblage de carte (IP_PKTINFO d'arrivée, muet sur le banc) est remplacé
      par le scoping des réponses : l'OFFER en broadcast dirigé du /24 du pool
      n'existe que sur la carte sélectionnée, une transaction venue d'une
      autre carte ne peut pas aboutir ;
    - audit : bind et réception partagent le même objet socket (instance
      unique tracée dans le code) ;
  - **correctif « DISCOVER non reconnus » (ter, banc nmcli : réceptions 16+,
    DISCOVER 0, OFFER 0, zéro erreur)** :
    - audit complet du chemin réception → parse → tri → OFFER : quatre sorties
      muettes identifiées (parse rejeté, type non traité, MAC locale, relais) —
      toutes désormais comptées et affichées en barre de statut ;
    - compteur « DISCOVER reçus » déplacé AVANT tout filtre : il reflète la
      réception (séquence d'acceptation : datagrammes → DISCOVER → OFFER →
      REQUEST → ACK sans trou) ;
    - parse BOOTP/DHCP durci : il ne lève jamais (aucune exception possible
      pour un datagramme malformé — indices bornés par la vérification de
      longueur), et tout rejet est classé par motif (trop court / pas BOOTP /
      cookie absent / chaddr nulle) avec diagnostic hex en barre de statut :
      en-tête + zone cookie/options à l'octet 236 ;
    - nouvelles sorties comptées : OFFER non confirmées (expiration — signature
      d'un client hors carte), RELEASE/DECLINE sans bail, type non traité
      (avec le motif, option 53 introuvable ou type non géré) ;
    - segment DHCP complet en barre de statut, jamais tronqué (deuxième ligne
      dédiée, 13 compteurs) ;
    - le pattern C# `is not ((byte)1 or 3 or 4 or 7)` a été testé
      empiriquement : sain (DISCOVER passe, INFORM sort) ;
    - filtre d'arrivée par IP locale : confirmé ABSENT du chemin DHCP (aucun
      rejet « autre carte » possible au niveau applicatif) — le ciblage reste
      le scoping des réponses + la signature « OFFER non confirmées » pour un
      client venu d'une autre carte (jamais confirmé, jamais configuré, bail
      libéré après 60 s, aucune interférence avec le DHCP du réseau d'origine).
  - **validation de bout en bout (04/11 au soir, après le correctif « ter »)** : DORA
    complet vu vert jusqu'au bout au tcpdump (DISCOVER → OFFER → REQUEST → ACK),
    IP du pool attribuée, badge DHCP, pastille verte, page web ouverte ;
  - **finitions 4.5/4.6 validées sur banc** : bail configurable, arrêt complet
    partagé à la fermeture, barre compacte, bouton ⓘ d'aide sur le bail ;
  - seul problème connu restant : l'icône runtime de la fenêtre (documentée
    comme telle, sans correctif à ce stade).
- **Étape 5 — réalisée, en test sur banc : packaging final (livraison 100 % hors-ligne)** :
  - `packaging/DeviceFinder.iss` : installeur Inno Setup 6 en français uniquement,
    icônes menu Démarrer + bureau, dossier Programmes par défaut (au choix),
    désinstalleur propre, case « Installer Npcap » cochée par défaut (assistant
    GUI lancé en fin d'installation, installeur Npcap embarqué dans le Setup.exe) ;
    chemins relatifs au dossier `packaging/`, aucun chemin de machine de build ;
  - `packaging/build-release.ps1` (à exécuter sur le PC Windows du banc) :
    publication self-contained **sans PDB** (`-p:DebugType=none` — les `.pdb`
    embarquent les chemins sources de la machine de build, ils sont écartés),
    version lue dans l'assembly publié (source unique : le csproj), compilation
    Inno Setup, puis zip portable — les deux livrables finissent dans `publish\` ;
  - neutralité de l'identité : métadonnées d'assembly (éditeur, auteur,
    copyright) → « Tiboria » ; audit final de neutralité (grep insensible à la
    casse sur le nom de la machine de build, dans les sources, les scripts et
    le dossier publié + recherche de chemins absolus dans les scripts) :
    sans résultat ;
  - composants embarqués vérifiés : runtime .NET 8 self-contained, installeur
    Npcap en ressource (extractible dans l'app et en case lors de l'installation),
    base OUI constructeurs, aucun téléchargement à l'installation ni à
    l'exécution, pas de télémétrie ni de mise à jour en ligne ;
  - tests d'acceptation : installation via le Setup → app fonctionnelle et
    désinstallation propre ; zip portable sur clé USB → fonctionnel depuis la
    clé ; ballet DHCP complet rejoué depuis la version installée ; aucune
    connexion requise à aucun moment (validation sur banc : à faire).
- **Étape 5.5 — réalisée, en test sur banc : internationalisation (fr-FR / en-US)** :
  - textes XAML en `{DynamicResource}` : un dictionnaire de chaînes par langue
    (`Localization/Strings.fr-FR.xaml`, `Strings.en-US.xaml`, 233 clés chacun,
    clés strictement identiques — vérifié par diff) est échangé dans
    `Application.Resources.MergedDictionaries` à la volée : tous les textes
    affichés se réévaluent sans recharger la fenêtre ;
  - chaînes construites en code (barre de statut, messages DHCP/alias/opener/
    Npcap, boîtes de dialogue) via `Loc.Get`/`Loc.Fmt` servi par la même table
    (une seule source de vérité par langue) — la table est immuable, lue sans
    verrou depuis les threads d'écoute ;
  - bascule à chaud par le sélecteur « Langue » en haut de fenêtre : les zones
    construites en code sont rafraîchies (bandeaux, baux, panneau de détail,
    listes de devices et de sondes — états conservés structurés, pas
    re-traduits depuis des chaînes figées) ;
  - réglage persisté (`settings.json`) : « auto » (défaut) suit la langue de
    l'OS à chaque lancement, sinon fr-FR/en-US explicites après un choix
    utilisateur ; résolution AVANT la création de la fenêtre principale ;
  - feuille d'aide sur le bail, sélecteur de cartes et compteurs : traduits ;
  - la règle pare-feu UDP 67 porte un nom neutre identique dans les deux
    langues (sinon la bascule laisserait des règles orphelines) ; l'ancien nom
    français est supprimé à chaque activation (migration) ;
  - installeur Inno Setup multilingue FR/EN : page de langue présélectionnée
    sur l'OS, messages d'installation traduits ;
  - `README.md` en anglais, `README.fr.md` (ce fichier) conservé ;
  - audit : aucun littéral français restant dans le code source hors
    dictionnaires et commentaires (`packaging/check-strings.sh`) ;
  - tests d'acceptation sur banc : bascule fr→en→fr à chaud, redémarrage
    garde le choix, ballet DHCP rejoué, aucun texte français résiduel en
    mode EN.

## Architecture

```
DeviceFinder.sln
├── packaging/
│   ├── DeviceFinder.iss        # script d'installeur Inno Setup (français, Npcap embarqué)
│   └── build-release.ps1       # génération des deux livrables dans publish\
└── src/DeviceFinder.App/
    ├── App.xaml(.cs), MainWindow.xaml(.cs), app.manifest, AssemblyInfo.cs
    ├── Models/
    │   ├── Device.cs          # device identifié par MAC, INPC pour l'UI
    │   ├── DeviceStore.cs     # registre thread-safe des devices détectés
    │   └── ManualProbeItem.cs # ligne de résultat du mode IP manuelle
    └── Services/
        ├── AppSettings.cs          # paramètres + trace des IP alias (%APPDATA%/DeviceFinder)
        ├── OuiLookup.cs            # base OUI IEEE embarquée (Resources/oui*.csv)
        ├── SendArpResolver.cs      # SendARP (iphlpapi) en P/Invoke
        ├── NetworkAdapterService.cs# énumération des cartes, conflits de sous-réseau
        ├── BroadcastListenerService.cs # SSDP, WS-Discovery, mDNS, NBNS
        ├── ArpCaptureService.cs    # capture ARP Npcap/SharpPcap (mode promiscuous)
        ├── NpcapService.cs          # détection + installeur Npcap embarqué (extraction + installation)
        ├── AliasIpService.cs        # IP alias via netsh (store=active) + garantie robuste/idempotente
        ├── ApipaScanService.cs      # balayage ARP périodique du 169.254.0.0/16
        ├── LocalHostFilter.cs       # exclusion de la machine locale (MAC/IP, toutes cartes)
        ├── ManualProbeService.cs    # mode IP manuelle : parsing de plage + sonde ping/SendARP
        ├── DhcpServerService.cs     # mini-serveur DHCP : OFFER/ACK/NAK, baux par MAC, pool configurable
        ├── ReachabilityMonitor.cs   # ping ICMP toutes les 5 s par device
        └── DeviceOpenerService.cs   # ouverture navigateur + logique d'alias
```
