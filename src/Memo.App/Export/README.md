# Export vidéo d'une Story

Ce dossier ajoute l'export d'une Story ou d'un récapitulatif en un seul fichier MP4.
Il ne modifie aucune source et n'ajoute aucune dépendance externe : le rendu utilise
`Windows.Media.Editing.MediaComposition` et Media Foundation, déjà présents dans Windows.

## API publique

Espace de noms : `Memo.App.Export`.

```csharp
var request = new StoryExportRequest(
    Items:
    [
        new StoryExportItem(
            MainPath: @"C:\Memo\media\photo.jpg",
            OverlayPath: @"C:\Memo\media\photo-overlay.png",
            MediaKind: MediaKind.Photo,
            DurationSeconds: 0,          // indicatif pour une photo
            Width: 1080,
            Height: 1920),
        new StoryExportItem(
            MainPath: @"C:\Memo\media\video.mp4",
            OverlayPath: null,
            MediaKind: MediaKind.Video,
            DurationSeconds: 12.5,       // estimation ; la durée réelle du clip est utilisée
            Width: 1080,
            Height: 1920),
    ],
    OutputPath: @"C:\Memo\exports\story.mp4",
    Title: "Été 2024",
    Quality: StoryExportQuality.Hd1080,
    IncludeAudio: true,
    PhotoDurationSeconds: 4.0,
    OverwriteExistingFile: false);

var progress = new Progress<StoryExportProgress>(p =>
{
    // p.Phase, p.Percent (0–100), p.CurrentItem, p.ItemIndex (1-based), p.Message
});

StoryExportResult result = await new StoryExporter().ExportAsync(
    request,
    progress,
    cancellationToken);

if (!result.Succeeded)
{
    // result.ErrorMessage nomme le problème et la sortie.
}
```

### Types

- `StoryExportQuality` : `Hd1080` (1080×1920 ou 1920×1080) ou `Hd720`
  (720×1280 ou 1280×720).
- `StoryExportPhase` : `Preparing`, `Rendering`, `Finalizing`, `Completed`,
  `Cancelled`, `Failed`.
- `StoryExportItem(string MainPath, string? OverlayPath, MediaKind MediaKind,
  double DurationSeconds, int Width, int Height)`.
  `Width` et `Height` sont les **dimensions d'affichage**, rotation EXIF comprise.
  `DurationSeconds` sert d'estimation pour la barre de progression ; elle ne coupe pas
  la vidéo.
- `StoryExportRequest(IReadOnlyList<StoryExportItem> Items, string OutputPath,
  string? Title = null, StoryExportQuality Quality = Hd1080, bool IncludeAudio = true,
  double PhotoDurationSeconds = 4.0, bool OverwriteExistingFile = false)`.
- `StoryExportProgress(StoryExportPhase Phase, double Percent, string? CurrentItem,
  int ItemIndex, int ItemCount, string Message)`.
- `StoryExportResult(bool Succeeded, string? OutputPath, TimeSpan Duration,
  long SizeBytes, string? ErrorMessage, bool Cancelled,
  IReadOnlyList<string> Warnings)`.
- `StoryExporter.ExportAsync(StoryExportRequest, IProgress<StoryExportProgress>?,
  CancellationToken)`.
- `StoryExportSmoke.RunSelfCheckAsync(string tempDirectory, CancellationToken)` :
  contrôle manuel sur des images synthétiques.
- `StoryExportPreviewBuilder.AnalyzeAsync(sources, progress, cancellationToken)` :
  inspection des en-têtes (dimensions d'affichage, durée, audio, calque) avant export.
- `StoryExportPreviewBuilder.CreatePlan(analyses, quality, includeAudio,
  photoDurationSeconds)` : lignes de prévisualisation, éléments exacts de la requête,
  durée totale, taille estimée et avertissements.

Les erreurs prévisibles (fichier existant, source absente, codec absent, espace
insuffisant, annulation) sont retournées dans `StoryExportResult`. Elles ne lèvent pas
d'exception. Seul un `request` nul lève `ArgumentNullException`.

## Comportement

### Validation

Avant tout rendu :

1. la Story n'est pas vide ;
2. `OutputPath` est un chemin absolu ou résolu, avec l'extension `.mp4` ;
3. le fichier de sortie n'existe pas, sauf si `OverwriteExistingFile` est vrai ;
4. chaque `MainPath` et `OverlayPath` existe ;
5. aucune source n'est aussi la destination (une source n'est jamais réécrite) ;
6. `PhotoDurationSeconds` est comprise entre 0,1 et 600 secondes ;
7. l'espace disque est estimé : débit vidéo (8 Mbit/s en 1080p, 5 Mbit/s en 720p)
   + 192 kbit/s d'audio, majoré de 25 %, plus une marge de 64 Mo. Une seconde
   vérification utilise les durées réelles des clips.

Si le volume ne peut pas être interrogé (UNC, volume non prêt), l'export continue avec
un avertissement dans `Warnings`.

### Durées et ordre

L'ordre de `Items` est l'ordre de lecture. Une photo dure `PhotoDurationSeconds`.
Une vidéo conserve sa durée réelle (`TrimmedDuration`, puis `OriginalDuration` en
repli) ; `DurationSeconds` n'est qu'une estimation pour la progression.

### Résolution et orientation

- Sortie portrait par défaut : **1080×1920** (`Hd1080`) ou **720×1280** (`Hd720`).
- Sortie paysage **1920×1080** ou **1280×720** uniquement si **tous** les éléments ont
  des dimensions connues et sont plus larges que hauts.
- Un élément dont les dimensions sont inconnues force le portrait.
- Chaque média est ajusté en « contain » dans la sortie, centré, avec des bandes
  noires. Aucun étirement.

### Calques

Un calque est un `MediaClip` image de la même durée que l'élément, placé dans un
`MediaOverlayLayer` propre à l'élément :

- `MediaOverlay.Opacity = 1` ;
- `MediaOverlay.Delay` = début de l'élément dans la composition ;
- `AudioEnabled = false` ;
- `Position` = rectangle d'affichage du média principal (letterbox calculé), réduit
  en « contain » si le ratio du calque diffère, pour ne jamais le déformer.

Un calque Snapchat qui partage les dimensions du média couvre donc exactement la zone
affichée. Un calque absent (`OverlayPath` nul ou vide) donne le média seul. Un calque
annoncé mais introuvable ou illisible fait échouer l'export avec le numéro de l'élément
et le chemin ; il n'est jamais ignoré silencieusement.

L'alpha des PNG est utilisé pour le détourage. Media Foundation peut toutefois rendre
les pixels **semi-transparents** plus opaques que dans le PNG source ; le calque reste
correctement positionné et détouré, mais un dégradé de transparence peut perdre en
finesse. C'est une limite du compositeur Media Foundation, pas un choix du module.

### Audio

- `IncludeAudio = true` : l'audio des vidéos est conservé. Le profil AAC est
  48 kHz, stéréo, 192 kbit/s.
- `IncludeAudio = false` : `MediaClip.Volume = 0` sur chaque vidéo **et** le profil
  d'encodage ne déclare aucune piste audio. Le MP4 produit n'a alors pas de flux audio.
- Une Story de photos n'a pas de piste audio, même avec `IncludeAudio = true`.
- Les fichiers sources ne sont jamais modifiés.

### Encodage et écriture

- Conteneur MP4, vidéo H.264, 30 images/s, `MediaTrimmingPreference.Precise`.
- Le fichier est écrit dans le dossier de destination sous
  `.memo-export-<guid>.tmp.mp4`, puis renommé avec `File.Move` (même volume,
  remplacement atomique). Le suffixe `.mp4` est conservé pour que Media Foundation
  reconnaisse le conteneur ; `.tmp` marque le fichier comme provisoire.
- Après le rendu, la taille du temporaire est vérifiée avant le renommage. Après le
  renommage, le fichier final est vérifié (existence, taille non nulle) et sa durée est
  relue par `MediaInspector`. Un fichier absent ou vide est supprimé, jamais présenté
  comme un succès.
- Le temporaire est supprimé en cas d'échec ou d'annulation. Si Media Foundation garde
  encore un handle, la suppression est retentée puis abandonnée : un
  `.memo-export-<guid>.tmp.mp4` peut alors rester dans le dossier de sortie. Il n'est
  jamais confondu avec le fichier final.
- Les conversions d'images de secours (WebP non décodé par Media Foundation) vont dans
  `%TEMP%\Memo\export\<guid>\`, nettoyé en fin d'export.

### Progression et annulation

La progression est globale : préparation 0–20 %, rendu 20–95 %, finalisation 96–100 %.
Pendant le rendu, l'élément courant est estimé à partir de sa position dans la durée
totale (Media Foundation ne fournit pas d'événement par clip).

`CancellationToken` annule l'opération `RenderToFileAsync` en cours. Le résultat porte
`Cancelled = true`, `OutputPath = null`, et le fichier final n'existe pas.

### Prévisualisation avant export

`StoryView` expose une prévisualisation avant de lancer le rendu. Elle liste, dans l'ordre :

- le numéro, le type et la date de l'élément ;
- ses dimensions d'affichage et son orientation (rotation EXIF comprise) ;
- sa durée (durée choisie pour les photos, durée réelle des vidéos) ;
- la présence d'un calque et d'une piste audio.

L'utilisateur choisit la qualité (1080p/720p), l'inclusion de l'audio et la durée des photos
(1 à 30 s). La durée totale et la taille approximative sont recalculées à chaque changement.
Un média introuvable est signalé et exclu de la requête ; un en-tête illisible, un format
inconnu ou des dimensions absentes produisent un avertissement. Un calque introuvable ou
illisible est signalé et ignoré. Les options et la sélection restent disponibles après un
export réussi ou annulé.

L'estimation de taille reprend les débits de `StoryExporter` (8 Mbit/s en 1080p, 5 Mbit/s
en 720p, 192 kbit/s d'audio, +25 % pour le conteneur), sans la marge de sécurité disque.

Après confirmation, `StoryExporter` reçoit exactement la requête prévisualisée. La progression
est affichée et un bouton « Annuler l'export » annule le rendu ; un export annulé ne conserve
aucun fichier final. La prévisualisation vérifie les en-têtes, pas le décodage complet ni les
codecs : un fichier peut donc échouer au rendu malgré l'aperçu, avec le diagnostic de
`StoryExporter`.

## Limites réelles

### Codecs

- **H.264 en sortie** : Media Foundation fournit normalement un encodeur H.264 sur
  Windows 10/11 desktop. Windows « N » et certaines installations réduites n'ont pas
  Media Foundation : il faut le Media Feature Pack. Le module ne peut pas installer de
  codec.
- **HEVC en sortie** : non utilisé et non garanti. La sortie est toujours H.264.
- **HEVC, AV1, VP9 en entrée** : le décodage dépend des codecs Windows installés.
  Une source HEVC produit un avertissement explicite dans `Warnings`. Si le décodeur
  manque, `CreateFromFileAsync` ou `RenderToFileAsync` échoue et le résultat contient
  `CodecNotFound`, le chemin de la source et la piste probable. Aucun fichier final
  n'est conservé.
- **HEVC Video Extensions** : l'extension Microsoft peut être payante ou absente selon
  l'édition de Windows. L'export ne la télécharge pas (aucun réseau).
- **HDR / 10 bits** : non garanti. Media Foundation peut convertir en 8 bits ou
  tone-mapper selon la source et l'encodeur disponibles.
- **Choix du codec matériel/logiciel** : laissé à Media Foundation. Aucune option de
  débit, de profil H.264 ou d'encodeur matériel n'est exposée.
- **Pas de FFmpeg, pas de LibVLC, pas de réseau.** Le module n'utilise que
  `Windows.Media.Editing`, `Windows.Storage`, Media Foundation et SkiaSharp (déjà
  présent dans Memo) pour la conversion PNG de secours et les images du self-check.

### Images et mise en page

- Formats acceptés par Media Foundation : JPEG, PNG et, selon l'installation, WebP.
  Si `CreateFromImageFileAsync` échoue, l'image est décodée par `ImageDecoder`
  (JPEG/PNG/WebP) puis réencodée en PNG temporaire, avec un avertissement. Une image
  corrompue ou d'un autre format fait échouer l'export avec le chemin et le détail.
- L'orientation EXIF des photos n'est **pas** appliquée par ce module : elle dépend du
  décodeur d'images de Media Foundation/WIC. `Width`/`Height` doivent être les
  dimensions d'affichage (utilisation de `DisplayGeometry.GetDisplaySize` côté
  appelant). Si le MP4 montre une photo tournée, fournir une copie déjà tournée comme
  source ; le module ne réécrit jamais l'original.
- Les vidéos dont la rotation est portée par les métadonnées sont affichées par
  Media Foundation dans leur sens de lecture. Là encore, `Width`/`Height` doivent être
  les dimensions d'affichage, pas les dimensions encodées.
- Pas de transitions, fondus, titres incrustés, sous-titres, musique de fond, effet
  Ken Burns, recadrage manuel ni découpage par élément. `Title` est conservé dans
  `MediaComposition.UserData` mais **n'est pas écrit dans les métadonnées du MP4** :
  `MediaComposition` ne l'expose pas au rendu.
- Une très longue Story peut consommer beaucoup de mémoire et de temps : tous les
  clips et calques sont ouverts avant le rendu. Le disque est vérifié, pas la RAM.

## Validation

### 1. Compilation dans le dépôt

```powershell
dotnet build src/Memo.App/Memo.App.csproj -c Debug
```

Attendu : `0 Erreur(s)`. Les avertissements XAML/SkiaSharp préexistants ne concernent
pas ce dossier.

### 2. Self-check hors application

`Memo.App` est une application WinUI 3 : un projet console ne peut pas la référencer
directement. Le contrôle se fait dans un projet console temporaire **hors du dépôt**,
qui lie les trois fichiers et référence `Memo.Core` + `Memo.Media`.

Créer `%TEMP%\memo-export-harness\Harness.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Platforms>x64</Platforms>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="C:\Users\jtoma\source\memo-wt\export\src\Memo.App\Export\StoryExportRequest.cs" Link="Export\StoryExportRequest.cs" />
    <Compile Include="C:\Users\jtoma\source\memo-wt\export\src\Memo.App\Export\StoryExporter.cs" Link="Export\StoryExporter.cs" />
    <Compile Include="C:\Users\jtoma\source\memo-wt\export\src\Memo.App\Export\StoryExportSmoke.cs" Link="Export\StoryExportSmoke.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="C:\Users\jtoma\source\memo-wt\export\src\Memo.Core\Memo.Core.csproj" />
    <ProjectReference Include="C:\Users\jtoma\source\memo-wt\export\src\Memo.Media\Memo.Media.csproj" />
  </ItemGroup>
</Project>
```

Créer `Program.cs` :

```csharp
using Memo.App.Export;

string report = await StoryExportSmoke.RunSelfCheckAsync(
    Path.Combine(Path.GetTempPath(), "memo-export-check"),
    CancellationToken.None);
Console.WriteLine(report);
```

Puis :

```powershell
cd $env:TEMP\memo-export-harness
dotnet run -c Debug
```

Attendu, par exemple :

```text
Contrôle d'export réussi : « C:\Users\...\memo-export-check\memo-export-smoke-xxxxxxxx\story-synthetique.mp4 » (71.4 Ko, 2.00 s, photo 1080×1920 avec calque).
```

Le self-check est volontairement autonome : il crée une photo unie 1080×1920 et un
calque PNG transparent avec un disque, exporte 2 secondes, vérifie l'existence du MP4,
une taille supérieure à 10 Ko, des dimensions 1080×1920 et une durée entre 1 et
4 secondes. Il ne lit aucune donnée personnelle. Le dossier produit reste sur disque
pour inspection.

### 3. Vérification du MP4

```powershell
ffprobe -v error -show_entries stream=index,codec_name,codec_type,width,height,duration `
  -show_entries format=duration,size -of default=noprint_wrappers=1 <fichier.mp4>
```

Attendu pour le self-check : une piste `h264` 1080×1920, aucune piste audio
(`IncludeAudio = false`), durée proche de 2 s, taille supérieure à 10 Ko.

Contrôles visuels utiles : extraire une image au milieu et vérifier que le disque du
calque est visible, que les coins restent de la couleur de la photo, et qu'un média
paysage dans une sortie portrait a bien des bandes noires en haut et en bas.

### 4. Scénarios manuels recommandés

- photo seule, puis photo + calque ;
- vidéo avec audio, `IncludeAudio = true` puis `false` (le second MP4 ne doit avoir
  aucune piste audio) ;
- story mixte portrait/paysage (letterbox) ;
- story entièrement paysage en `Hd720` (1280×720) ;
- fichier de sortie existant sans `OverwriteExistingFile` (refus, contenu inchangé) ;
- même fichier avec `OverwriteExistingFile = true` (remplacement réussi) ;
- annulation pendant le rendu (aucun fichier final, temporaire supprimé) ;
- source HEVC (avertissement, succès si le décodeur est présent, échec propre sinon) ;
- machine sans encodeur H.264 (Windows N sans Media Feature Pack) : échec
  `CodecNotFound`, message explicite, aucun fichier final.

### 5. Résultats obtenus le 30 octobre 2026

- `dotnet build src/Memo.App/Memo.App.csproj -c Debug` : 0 erreur, 12 avertissements
  préexistants (XAML/SkiaSharp), aucun avertissement dans `src/Memo.App/Export`.
- Self-check : MP4 H.264 1080×1920, 2,00 s, environ 71 Ko, calque visible au centre,
  aucun flux audio, aucun temporaire restant.
- Vérifications complémentaires avec un projet console externe et `ffprobe`/`ffmpeg` :
  - photo paysage + calque dans une story mixte : bandes noires, calque exactement sur
    la zone affichée ;
  - deux éléments avec calques : le calque rouge du premier disparaît au second ;
  - sortie 1280×720 quand tous les médias sont paysage ;
  - vidéo avec audio : volume moyen et maximal identiques à la source ;
  - `IncludeAudio = false` : aucune piste audio ;
  - source HEVC : avertissement HEVC, export réussi sur la machine de développement
    (décodeur HEVC présent) ;
  - refus d'écrasement : fichier existant intact ;
  - remplacement explicite : MP4 valide ;
  - annulation : `Cancelled = true`, aucun fichier final, aucun `.tmp.mp4` restant.
- Le self-check n'est **pas** branché dans l'interface : il reste un contrôle manuel. La
  prévisualisation, les options, la progression et l'annulation sont intégrées à
  `StoryView`, sans modifier les autres pages.
