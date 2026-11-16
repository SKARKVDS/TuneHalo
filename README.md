# TuneHalo

Petite télécommande Windows (WPF, .NET 10) pour **Spotify desktop** : une bulle flottante, un panneau « lecture en cours » et des raccourcis clavier globaux. Une sorte de Stream Deck maison, sœur de QuotaHalo.

## Fonctions

- **Lecture / pause, piste précédente / suivante**, pochette, titre, artiste et progression.
- **Volume Spotify** (le curseur dans Spotify bouge aussi), réglable au clavier ou à la molette.
- **Like / retirer le like** du morceau en cours (nécessite la connexion à un compte Spotify, voir plus bas).
- **Muet**.

## Affichages

- **Bulle flottante** : toujours au premier plan, déplaçable (sa position est retenue).
  - clic : ouvre le panneau de lecture ;
  - clic milieu : lecture / pause ;
  - molette : volume ;
  - clic droit : menu (lecture/pause, like, ouvrir Spotify, paramètres, masquer la bulle, quitter).
- **Panneau** : pochette, titre, barre de progression et boutons de contrôle.
- **Zone de notification** : clic gauche = lecture / pause, l'info-bulle affiche le morceau en cours ; clic droit = menu (suivant, afficher/masquer la bulle, paramètres, quitter).

## Raccourcis clavier

Touche(s) à maintenir (par défaut **Ctrl + Alt**, réglable : Ctrl + Maj, Alt + Maj, Ctrl + Win) + **pavé numérique** :

| Touche | Action |
|---|---|
| `0` | Lecture / pause |
| `4` / `6` | Piste précédente / suivante |
| `8` / `2` | Volume + / − (répétition si maintenue) |
| `5` | Liker / retirer le like |
| `1` | Muet |
| **molette** (n'importe où) | Volume + / − |

- Le **Verr. Num doit être activé** (sinon le pavé envoie Inser, Fin, flèches…).
- La combinaison modificateur + molette est interceptée globalement (hook souris bas niveau) et n'est pas transmise à la fenêtre sous le curseur. Utile pour les casques dont la molette de volume est analogique (jack 3,5 mm) et donc invisible pour Windows.
- Si un raccourci est déjà pris par une autre application, les paramètres l'indiquent ; choisir un autre modificateur.
- Pas de volume : 5 % par défaut, réglable (`VolumeStep` dans les réglages).

## Fonctionnement

| Fonction | Source | Compte requis |
|---|---|---|
| Lecture, piste, pochette, progression | Windows SMTC (contrôles multimédia du système) | non |
| Volume | Spotify Web API `PUT /me/player/volume` si connecté, sinon la session audio de Spotify dans le mélangeur Windows (NAudio WASAPI) | optionnel |
| Like | Spotify Web API `/me/library` (repli sur `/me/tracks`) | oui |

Sans connexion, le volume ne règle que le mélangeur Windows : le curseur de Spotify ne bouge pas.

### Connecter un compte Spotify (like + volume Spotify)

1. Sur [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard), créer une application (API : *Web API*).
2. Ajouter la Redirect URI exacte : `http://127.0.0.1:8765/callback`.
3. En mode développement, le compte utilisé doit figurer dans *User Management* de l'application (le propriétaire y est par défaut).
4. Dans TuneHalo, **Paramètres › Like (compte Spotify)** : coller le **Client ID**, puis se connecter dans le navigateur.

Authentification OAuth PKCE (aucun secret client). Scopes demandés : `user-read-currently-playing`, `user-read-playback-state`, `user-modify-playback-state`, `user-library-read`, `user-library-modify`. Après une mise à jour qui ajoute un scope, cliquer une fois sur **Reconnecter**.

Le jeton de rafraîchissement est chiffré avec Windows DPAPI (lié à l'utilisateur Windows) avant d'être enregistré.

## Réglages

`%APPDATA%\TuneHalo\settings.json` : position de la bulle, modificateur, pas de volume, Client ID, jeton chiffré. Option « Lancer TuneHalo au démarrage de Windows » (clé `HKCU\...\Run`).

## Structure du code

| Fichier | Rôle |
|---|---|
| `App.xaml(.cs)` | démarrage, icône de notification, aiguillage des actions |
| `BubbleWindow` | bulle flottante (progression, badge like, OSD) |
| `PanelWindow` | panneau « lecture en cours » |
| `SettingsWindow` | paramètres, raccourcis, connexion Spotify |
| `MediaService.cs` | lecture et état via SMTC |
| `VolumeController.cs` | choix Web API / mélangeur Windows |
| `SpotifyVolume.cs` | volume de la session audio Spotify (NAudio) |
| `SpotifyApi.cs` | OAuth PKCE, player, like |
| `Hotkeys.cs` | raccourcis globaux `RegisterHotKey` |
| `MouseWheelHook.cs` | hook `WH_MOUSE_LL` pour modificateur + molette |
| `Settings.cs` | réglages JSON, DPAPI, démarrage automatique |

## Compiler et lancer

Prérequis : .NET 10 SDK, Windows 10 2004 (19041) ou plus récent.

```powershell
dotnet build -c Release
dotnet run -c Release
```

## Installer

Télécharger `TuneHalo-Setup-<version>.exe` depuis la page [Releases](https://github.com/SKARKVDS/TuneHalo/releases) et le lancer :
- installation par utilisateur, sans droits administrateur, dans `%LOCALAPPDATA%\Programs\TuneHalo` ;
- exe autonome (aucun runtime .NET à installer) ;
- raccourci menu Démarrer, option de démarrage avec la session, désinstalleur (Paramètres Windows › Applications).

L'installeur n'est pas signé : Windows SmartScreen peut afficher « Windows a protégé votre ordinateur », cliquer sur **Informations complémentaires › Exécuter quand même**.

## Construire l'installeur

Prérequis : .NET 10 SDK et Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File installeruild-installer.ps1 -Version 1.0.0
```

Produit `installer\Output\TuneHalo-Setup-<version>.exe`. L'icône est `app.ico` (intégrée à l'exe et utilisée pour la zone de notification).
