# Décision 0001 — Socle Windows, catalogue et médias

Date : 30 octobre 2026. Statut : acceptée, à revalider sur une installation Windows propre.

## Décision

| Couche | Choix | Version | Licence | Justification |
|---|---|---|---|---|
| Runtime | .NET | 10.0 (SDK 10.0.401) | MIT | LTS, un seul runtime auto-contenu |
| Interface | WinUI 3 / Windows App SDK | 1.8.260921001 | MIT | natif Windows, `NavigationView`, `ContentDialog`, accessibilité |
| Catalogue | SQLite via `Microsoft.Data.Sqlite` | 10.0.14 | MIT / domaine public | fichier unique, migrations simples, requêtes par date |
| Images | SkiaSharp | 4.154.0 | MIT | JPEG, PNG, **WebP**, composition, miniatures |
| Vidéo | LibVLCSharp + LibVLC | 3.10.2 / 3.0.24 | LGPL-2.1-or-later | HEVC/H.264 sans dépendre de l'extension Microsoft |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 | MIT | génération de propriétés et commandes |
| Tests | MSTest | 4.0.2 | MIT | intégré au SDK |

Cible : Windows 10 21H2 (build 19041) et Windows 11, **x64**. ARM64 non traité dans cette version.

## Validation effectuée

- Un prototype WinUI 3 non empaqueté, auto-contenu (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`) compile et se lance sur Windows 11 build 26200.
- `LibVLCSharp.WinUI` s'intègre via `LibVLCSharp.Platforms.Windows.VideoView`. La lecture d'un échantillon **HEVC** de l'export Snapchat a été vérifiée à l'écran (sortie Direct3D11).
- L'extension HEVC Microsoft est présente sur la machine de développement, mais **LibVLC est conservé** pour ne pas en dépendre.
- SkiaSharp et le décodage WebP restent à valider par les tests du projet `Memo.Media`.

## Conséquences

### Stockage

```
<Bibliothèque>/
  catalog.db
  library.json
  media/<sha256[0..2]>/<sha256>.<ext>     octets sources immuables
  derived/thumbs/<memoryId>.jpg           miniatures reconstructibles
  derived/composite/<memoryId>.webp       rendus composés optionnels
  imports/<batchId>.json                  provenance des lots
```

Les chemins stockés sont **relatifs** à la racine de la bibliothèque ; aucune référence absolue ne doit être nécessaire à la restauration.

### Répartition des responsabilités

- `Memo.Core` : domaine, catalogue SQLite, import, dates, découverte, sauvegarde. Aucune dépendance UI.
- `Memo.Media` : inspection binaire, décodage, orientation, composition, miniatures, extraction d'image vidéo.
- `Memo.App` : WinUI 3, navigation, lecteur, OCR Windows, visages, notifications, export vidéo.
- `tools/Memo.Corpus` : génération d'exports synthétiques, sans données personnelles.

### Pourquoi pas Media Foundation seul

Le corpus réel contient une forte proportion de HEVC. La lecture via `MediaPlayerElement` dépend de l'extension HEVC du Store, absente d'une installation propre. LibVLC garantit la lecture sans dépendance implicite. Le surcoût de paquet (~120 Mo) est assumé pour la première version.

### Pourquoi pas FFmpeg directement

LibVLC embarque FFmpeg et fournit une API .NET stable, le rendu Direct3D et les callbacks de frames nécessaires aux miniatures. Ajouter FFmpeg brut doublerait la surface d'intégration native.

## Distribution

- Publication `dotnet publish -c Release -r win-x64 --self-contained` puis installateur **Inno Setup 6** non signé.
- Installation par utilisateur dans `%LOCALAPPDATA%\Programs\Memo`, désinstallation propre, données utilisateur conservées sur choix explicite.
- Les DLL LibVLC restent séparées et remplaçables (conformité LGPL) ; `LICENSES/` est livré avec l'application.
- Aucune donnée personnelle dans le paquet ni dans les journaux de build.

## Risques ouverts

1. Validation sur une installation Windows propre (sans extension HEVC ni WebP) non encore réalisée.
2. Taille de l'installateur (~150–200 Mo) et temps de premier lancement.
3. Extraction de miniatures vidéo sans fenêtre : callbacks LibVLC à valider.
4. Absence de signature de code : SmartScreen affichera un avertissement.
5. Le format d'index Snapchat avec liens n'est pas validé ; aucun téléchargement distant n'est activé.
