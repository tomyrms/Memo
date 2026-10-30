# Analyses locales (`src/Memo.App/Analysis/`)

Ce dossier contient les analyses **facultatives et strictement locales** de Memo. Aucun appel
réseau, aucun paquet supplémentaire : uniquement Windows App SDK/WinRT et SkiaSharp (déjà
référencés par `Memo.App`). Les fichiers sources de la bibliothèque ne sont jamais modifiés ; les
résultats sont écrits dans `catalog.db` via `MemoLibrary.Analysis`.

| Fichier | Rôle |
|---|---|
| `LocalOcrService.cs` | OCR via `Windows.Media.Ocr.OcrEngine` |
| `VisualHashService.cs` | empreinte perceptuelle dHash 64 bits et recherche par similitude |
| `FaceAnalysisService.cs` | détection `Windows.Media.FaceAnalysis.FaceDetector` et regroupement |
| `AnalysisRunner.cs` | orchestration d'une passe sur une bibliothèque |
| `AnalysisImage.cs` | conversions internes SkiaSharp → BGRA/Gray8 (non public) |

## Activer une passe

```csharp
var runner = new AnalysisRunner(library);
var options = new AnalysisRunOptions
{
    Ocr = library.Settings.Current.OcrEnabled,
    VisualHash = true,
    Faces = library.Settings.Current.FacesEnabled,
    SkipExisting = true,
};

var progress = new Progress<AnalysisProgress>(p =>
{
    StatusText.Text = p.Message;
    AnalysisBar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
});

AnalysisSummary summary = await runner.RunAsync(options, progress, cancellationToken);
```

`RunAsync` peut être appelé depuis le fil d'interface : les décodages SkiaSharp sont exécutés dans
`Task.Run`, et les écritures SQLite sont asynchrones. Une annulation retourne un bilan avec
`summary.Cancelled == true` ; elle ne lève pas d'exception.

## API publique

### OCR

```csharp
var ocr = new LocalOcrService();
OcrResult result = await ocr.RecognizeAsync(path, ct);

result.Succeeded;          // image lue, hors annulation
result.Text;               // texte complet (peut être vide)
result.Lines;              // OcrLine { Text, Words }
result.Lines[0].Words[0];  // OcrWord { Text, X, Y, Width, Height } normalisés 0..1
result.Language;           // ex. "fr-FR"
result.Duration;           // TimeSpan
result.Error;              // message français ou null
result.Cancelled;          // annulation demandée
result.EngineUnavailable;  // aucun pack OCR : inutile de réessayer chaque image
```

Ordre de création du moteur : langues du profil Windows, puis `fr-FR`, `en-US`, `en-GB`. Pour
relire un pack installé pendant que Memo tourne, appeler `LocalOcrService.ResetEngineCache()`.
`GetAvailabilityMessage()` retourne le diagnostic ou `null` si un moteur existe.

### Empreinte visuelle

```csharp
ulong hash = VisualHashService.ComputeFromFile(thumbnailPath);
int distance = VisualHashService.HammingDistance(hash, otherHash);
IReadOnlyList<SimilarImage> matches =
    VisualHashService.FindSimilar(hash, storedHashes, maxDistance: 10);
```

`ComputeFromFile`, `ComputeFromBytes`, `ComputeFromBitmap`, `ComputeFromRegion` et
`TryComputeFromFile` (sans exception) sont disponibles. Les miniatures existantes suffisent : il
est inutile de recharger les originaux. `FindSimilar` retourne les candidats triés par distance
croissante.

### Visages

```csharp
var faces = new FaceAnalysisService();
FaceDetectorAvailability availability = await faces.GetAvailabilityAsync();
FaceDetectionResult detection = await faces.DetectAsync(path, ct);

FaceGroupingResult grouping = await faces.RebuildGroupsAsync(
    library, sources, progress: null, ct);
```

`DetectedFace` contient `Bounds` (normalisé), `Descriptor` (dHash 64 bits) et `X/Y/Width/Height`
en pixels de l'image dans son sens d'affichage, directement utilisables par
`AnalysisStore.AddFaceAsync`. `DescriptorToBytes` / `DescriptorFromBytes` sérialisent le
descripteur en 8 octets big-endian.

`RebuildGroupsAsync` détecte chaque source, puis remplace les détections existantes
(`ClearFacesAsync`) juste avant l'écriture et crée des groupes **sans nom** pour les grappes d'au
moins deux visages provenant d'au moins deux souvenirs différents. Une annulation ou une panne du
détecteur pendant la lecture laisse donc les anciennes détections intactes. Les visages isolés sont
conservés avec `group_id = NULL`. Aucun nom n'est déduit ni inventé.

## Limites connues

- **Langues OCR** : dépendent des packs installés dans Windows. Le repli automatique se limite à
  `fr-FR`, `en-US` et `en-GB`. Une machine sans pack reçoit un message explicite ; la passe OCR
  s'arrête après le premier échec au lieu de répéter le diagnostic.
- **WebP** : décodé par SkiaSharp, pas par le codec Windows. L'OCR et les visages fonctionnent donc
  sur les WebP même si le codec système est absent (Windows N).
- **Orientation EXIF** : appliquée une seule fois par `Memo.Media.ImageDecoder` avant réduction.
- **Taille** : OCR ≤ 2 000 px de plus grand côté, visages ≤ 960 px. Les coordonnées restituées sont
  ramenées à l'image d'origine.
- **Vidéos** : ignorées par cette passe (le média principal n'est pas une image) et comptées dans
  `AnalysisSummary.VideosSkipped`. Les miniatures vidéo ne sont pas analysées.
- **Exclusions** : `MemoryCard.IsExcluded` est toujours respecté ; aucun texte, empreinte ou visage
  n'est écrit pour une carte exclue.
- **Visages (Windows N)** : `FaceDetector` dépend du module « Analyse des visages » de Windows
  Media Foundation. Son absence produit un diagnostic, jamais une exception. La disponibilité est
  testée **avant** d'effacer les détections existantes.
- **Qualité du regroupement** : le descripteur 64 bits est un dHash du cadrage du visage, pas un
  vecteur d'identité. Il regroupe des apparences proches (même personne, cadrage et lumière
  similaires) mais peut confondre des personnes ou séparer la même personne selon la pose. Les
  groupes sont des propositions à renommer, fusionner ou supprimer par l'utilisateur ; aucune
  reconnaissance certaine n'est promise (cf. `PRODUCT.md`, hors périmètre).
- **Reconstruction** : les groupes nommés par l'utilisateur restent dans le catalogue après
  `ClearFacesAsync`, même s'ils deviennent vides. L'interface doit proposer de les fusionner ou de
  les supprimer ; le service ne les renomme ni ne les efface jamais. Si toutes les images sont
  illisibles, les anciennes détections sont conservées ; si elles sont toutes lisibles sans visage,
  elles sont effacées (résultat honnête : plus aucun visage).
- **Doublons** : relancer `RebuildGroupsAsync` remplace les détections (pas d'accumulation). Pour
  OCR et empreintes, `AnalysisRunOptions.SkipExisting` évite de recalculer ce qui existe déjà.
- **Groupes vides** : un groupe n'est créé que si au moins un de ses visages a pu être écrit en
  base.

## Validation effectuée

Validation locale sur Windows 11 x64 (build Debug de `Memo.App` réussi) avec des images
synthétiques SkiaSharp :

- OCR PNG et WebP : texte `BONJOUR MEMO / Analyse locale 2026` reconnu, langue `fr-FR`,
  coordonnées normalisées cohérentes, annulation propre.
- OCR d'un JPEG dont les pixels sont stockés couchés avec l'orientation EXIF 6 : texte
  `PHOTO ORIENTEE / EXIF SIX` reconnu, donc l'orientation est bien appliquée avant le moteur.
- dHash : PNG ↔ WebP = 1 bit, PNG ↔ réduction 500 px = 1 bit.
- Détection de visages : détecteur disponible, 0 visage sur les images sans visage, aucune erreur.
- Bibliothèque jetable : import de 3 photos, 1 exclue → OCR et empreintes sur 2 cartes seulement ;
  `SkipExisting` saute bien les 2 déjà traitées ; annulation retourne un bilan annulé ; groupes de
  visages reconstruits sans erreur.
- Regroupement synthétique de descripteurs : deux grappes attendues retrouvées.
