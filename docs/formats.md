# Formats d'export et contrat d'adaptateur

Ce document décrit ce que Memo sait reconnaître, ce que le corpus synthétique
exerce et ce qui reste à valider. Il ne remplace pas l'observation d'un export
réel : il fixe un contrat pour l'importeur et les tests.

## Statut des preuves

| Étiquette | Signification |
|---|---|
| **Observé** | Décrit dans le rapport externe ou l'inventaire CSV fournis pendant le cadrage. Les comptages CSV ont été recalculés, mais les signatures binaires n'ont pas été vérifiées sur les médias. |
| **Synthétique** | Produit par `tests/Memo.TestSupport` et `tools/Memo.Corpus`. Sert à tester le comportement, pas à prouver ce que contient un export réel. |
| **Non validé** | Hypothèse, variante possible ou fonction volontairement inactive tant qu'un exemple réel ne l'a pas confirmée. |

Aucun export personnel, aucun média réel, aucun identifiant de compte et aucune
URL signée ne sont conservés dans le dépôt. Le corpus est généré à la demande ;
les `.zip` sont ignorés par git.

## 1. Format observé

### Arborescence et noms

Le rapport externe décrit un ancien export dont les souvenirs sont regroupés
sous `memories/` avec un identifiant et un rôle dans le nom :

```text
memories/<uuid>-main.jpg      photo principale
memories/<uuid>-main.mp4      vidéo principale
memories/<uuid>-overlay.png   calque associé
```

- **Observé** : les identifiants listés sont syntaxiquement des UUID v4. Leur
  ordre lexicographique ne donne pas un ordre chronologique.
- **Observé** : un média et son calque peuvent être dans des archives
  différentes, et un calque peut arriver sans média. Sur l'échantillon
  inventorié, 9 identifiants seulement avaient média et calque dans le même
  ZIP, 522 médias n'avaient pas de calque et 285 calques n'avaient pas de média.
- **Observé** : les noms de rôle et l'extension annoncée ne prouvent pas le
  format binaire. Une extension `.png` peut contenir un WebP.
- **Non validé** : la casse exacte, les sous-dossiers, les variantes de nommage
  et la stabilité des identifiants entre plusieurs exports successifs.

### Formats binaires

| Élément | Constat | Statut |
|---|---|---|
| Photos | JPEG, extension `.jpg` | Observé dans l'inventaire ; signatures non revérifiées |
| Vidéos | MP4, codecs H.264 et HEVC rapportés | Observé ; décodage réel non validé dans ce corpus |
| Calques | 214 WebP sous extension `.png`, 80 PNG sur 294 calques | Observé dans le rapport externe, non revérifié |
| Calques | RGBA avec transparence, dimensions parfois différentes du média | Observé dans le rapport ; composition à valider visuellement |
| Index | `index.html` fourni sans rubrique Souvenirs | Observé sur un fichier, non représentatif d'un format |
| Métadonnées | `date_capture_mp4` issue de `creation_time` | Observé ; la signification « prise de vue » n'est pas prouvée |

Le format doit donc être choisi par **signature binaire**, jamais par
l'extension seule. Les signatures minimales attendues sont :

```text
JPEG : FF D8 FF
PNG  : 89 50 4E 47 0D 0A 1A 0A
WebP : 52 49 46 46 ?? ?? ?? ?? 57 45 42 50   (RIFF....WEBP)
MP4  : box ftyp à l'offset 4, puis marque compatible (isom, mp42, etc.)
```

### Dates

- **Observé** : les vidéos de l'inventaire ont toutes une valeur de date,
  présentée comme `creation_time` du conteneur MP4.
- **Observé** : les photos de l'inventaire n'ont pas de date EXIF renseignée
  (0 sur 177). Une photo sans date reste un souvenir consultable.
- **Observé** : l'horodatage d'un ZIP correspond à la génération de l'archive,
  pas à la date du souvenir. Il ne doit jamais remplacer une date absente.
- **Non validé** : le fuseau de capture, la précision réelle de
  `creation_time`, la présence d'EXIF dans d'autres exports et la règle de
  classement local (le rapport montre que le choix UTC ou Europe/Zurich change
  le jour de 40 vidéos).

Une date candidate conserve toujours sa valeur brute, sa source, sa
signification probable, sa précision et sa confiance. Une correction manuelle
reste séparée et prioritaire.

### Rotation et dimensions

- **Observé** : 339 vidéos sont indiquées à 90° et 15 à 0° dans le CSV.
- **Observé** : les dimensions du CSV paraissent déjà portrait, y compris pour
  les vidéos à 90°. Les colonnes ne précisent pas leur espace de référence.
- **Non validé** : la matrice de rotation réelle, le comportement du décodeur
  (rotation déjà appliquée ou non) et les dimensions encodées avant rotation.

Le contrat distingue donc dimensions encodées, transformation et dimensions
affichées. Une seule rotation est appliquée, dans l'espace d'affichage. Le
corpus synthétique couvre le paysage encodé avec matrice 90° et la vidéo sans
rotation ; un encodage déjà portrait peut être produit en appelant
`SyntheticMp4.Create` avec des dimensions portrait et une rotation nulle.

## 2. Variantes synthétiques testées

`tests/Memo.TestSupport` produit les variantes suivantes, sans média réel.

### Images et calques

| Cas | Générateur | Ce qui est vérifié |
|---|---|---|
| Photo JPEG portrait 1080×1920 | `SyntheticImage.Jpeg` | date et libellé lisibles, signature JPEG |
| Photo JPEG paysage 1920×1080 | `SyntheticImage.Jpeg` | dimensions encodées différentes |
| Calque WebP nommé `.png` | `SyntheticImage.Overlay` + extension `png` | détection par signature, pas par extension |
| Calque WebP nommé `.webp` | idem, extension `webp` | extension cohérente |
| Calque PNG | `SyntheticImage.Overlay` avec `Png` | transparence RGBA |
| Calque entièrement transparent | `SyntheticImage.FullyTransparentOverlay` | conservation d'un calque vide |
| Échelles 0,89×, 1×, 1,04×, 1,73× | `SyntheticImage.OverlayScales` | composition dans l'espace affiché |
| Texte « 2021-07-14 », « BON APREM » | police bitmap 5×7 | rendu déterministe sans police système |

La police bitmap est volontaire : elle évite les variations de rastérisation
d'une police système entre processus et rend les archives reproductibles octet
pour octet avec la même graine.

### Vidéos

| Cas | Générateur | Ce qui est vérifié |
|---|---|---|
| MP4 structurel, rotation 0 | `SyntheticMp4.Create` | `ftyp`, `moov`, `mvhd`, `tkhd`, `mdat`, date UTC |
| MP4 structurel, rotation 90 | `SyntheticMp4.Create(rotation: 90)` | 1920×1080 encodé, 1080×1920 affiché, matrice 90° |
| MP4 décodable | `SyntheticVideo.RealMp4` | ffmpeg si présent ; sinon diagnostic et skip |
| Métadonnées de date | `creation_time` dans `mvhd`, `tkhd`, `mdhd` | lecture de la date conteneur |

`SyntheticMp4` est **structurellement** lisible par un parseur ISO-BMFF, mais
son `mdat` n'est pas un flux H.264 décodable. Il ne sert pas à valider la
lecture vidéo. `SyntheticVideo.RealMp4` produit un vrai H.264 avec ffmpeg
lorsque l'outil est disponible ; la rotation 90 y est obtenue par `transpose`,
donc l'encodage est portrait. Le cas « paysage encodé + matrice » reste couvert
par `SyntheticMp4`.

### Archives et cas d'import

| Cas | Option / méthode | Résultat attendu |
|---|---|---|
| Média et calque dans deux ZIP | `WriteMediaOverlaySplit`, `Roles` | même souvenir après rapprochement |
| Dates ZIP différentes | `ExportArchiveOptions.ZipTimestamp` | aucune date de souvenir inventée |
| Même contenu sous deux UUID | réutilisation du même `byte[]` | octets mutualisables, souvenirs distincts |
| Conflit de rôle | `EmitDuplicateRoleConflict` | deux candidats conservés, pas d'écrasement |
| Même UUID dans deux espaces | deux archives, `SpaceKey` différent | deux souvenirs séparés |
| Entrée `../` | `IncludeTraversalEntry` | entrée refusée, diagnostic, archive non extraite hors racine |
| Archive corrompue | `CorruptAfterWrite` | diagnostic, bibliothèque existante intacte |
| Calque seul | `SyntheticMemory` sans `MainBytes` | souvenir `OverlayOnly` visible |
| Index HTML synthétique | `IncludeIndexHtml` | lu comme donnée statique, sans script |
| JSON synthétique | `IncludeJson` | métadonnées de test, pas un index Snapchat |

Le corpus de démonstration inclut aussi un 29 février, des dates proches de
minuit, des souvenirs sans date et des calques orphelins.

### Helpers de test

| Helper | Rôle |
|---|---|
| `SyntheticImage` | photos JPEG/PNG/WebP et calques RGBA déterministes |
| `SyntheticMp4` | MP4 ISO-BMFF minimal, date UTC et rotation 0/90 |
| `SyntheticVideo.RealMp4` | MP4 H.264 réel via ffmpeg si disponible, sinon skip |
| `ExportBuilder` | ZIP synthétiques, répartition des rôles, conflits, corruption, traversée |
| `TempLibrary` | dossier temporaire jetable avec nettoyage tolérant aux verrous |

### Corpus de démonstration

```powershell
dotnet run --project tools/Memo.Corpus -- --out <dossier> --seed 42 --count 20
dotnet run --project tools/Memo.Corpus -- --out <dossier> --seed 42 --count 40 --format folder
```

- `--seed` rend les identifiants, dates, contenus et archives reproductibles.
- `--count` accepte 4 à 500 souvenirs.
- `--format zip` produit deux archives de médias et une archive de calques,
  plus des archives de conflit, d'autre espace et une archive tronquée.
- `--format folder` produit une arborescence extraite et les mêmes cas séparés.
- Le manifeste `corpus-manifest.json` résume les comptages et les cas couverts.

## 3. Contrat d'adaptateur

### Découverte

L'adaptateur reçoit un espace source (`SpaceKey` ou identifiant interne) et une
ou plusieurs archives ou un dossier déjà extrait.

1. Parcourir les entrées sans extraction aveugle.
2. Reconnaître le motif `memories/<uuid>-<role>.<ext>` avec `role` dans
   `main` ou `overlay`. Les autres entrées sont ignorées avec un diagnostic,
   sauf `index.html` et `json/*` qui peuvent être lus comme données.
3. Normaliser les séparateurs, rejeter les chemins absolus, les lettres de
   lecteur, les UNC, les segments `..`, les noms réservés Windows et les flux
   alternatifs (`:`).
4. Associer chaque entrée à une occurrence source : archive, chemin d'entrée,
   taille, SHA-256, extension annoncée, format détecté et rôle.

L'UUID n'est interprété que dans son espace source. Deux archives de même UUID
mais d'espaces différents ne sont jamais fusionnées.

### Inspection des médias

Pour chaque entrée retenue :

- lire les premiers octets et déterminer le format réel ;
- relever les dimensions encodées et, pour une vidéo, la matrice/rotation ;
- lire `creation_time` en UTC s'il existe, sans le promouvoir en date de prise
  de vue certaine ;
- lire les dates EXIF des images si présentes ;
- vérifier la présence d'un canal alpha pour un calque ;
- calculer l'empreinte et la taille ;
- conserver un diagnostic si le format annoncé et le format détecté diffèrent.

Un MP4 est accepté comme conteneur si `ftyp` est lisible. Le codec interne
(H.264, HEVC) est une information de lecture, pas une condition d'import.
Une vidéo dont le codec n'est pas lisible reste un fichier source conservé.

### Métadonnées

Les métadonnées ne sont jamais inventées. Chaque valeur conserve :

- sa valeur brute ;
- sa source (`Mp4CreationTime`, `ExifDateTimeOriginal`, `Index`, `User`) ;
- sa signification probable (`ContainerCreated`, `Capture`, `Export`,
  `UserProvided`) ;
- sa précision (`Second`, `Day`, `Month`, `Year`, `Unknown`) ;
- son fuseau ou offset s'il est connu ;
- sa confiance et les conflits éventuels.

Un index HTML/JSON synthétique peut fournir un candidat `Index`. Un index réel
n'est pas encore un format validé : il n'est pas prioritaire sur une date de
conteneur, et il ne remplace pas une correction utilisateur.

### Association

- Clé logique : `(espace source, UUID)`.
- Rôles : `Main` et `Overlay`. Un souvenir peut être `Complete`,
  `MediaOnly`, `OverlayOnly` ou `Conflict`.
- Plusieurs candidats pour le même rôle ne sont jamais écrasés : ils restent
  des variantes et produisent un diagnostic `role-conflict`.
- Des octets identiques sous deux UUID peuvent partager un asset, mais les
  souvenirs logiques restent distincts.
- Un calque orphelin est conservé et visible comme élément à compléter.
- Un réimport de la même archive ne duplique pas les occurrences ; l'empreinte
  de l'archive et les empreintes d'entrées servent au rapprochement.
- L'ordre d'import ne change pas le résultat final.

### Diagnostics

Chaque anomalie est rattachée à une entrée ou à une archive, avec une gravité
et un code stable. Exemples : `unsupported-format`, `extension-mismatch`,
`role-conflict`, `overlay-only`, `media-only`, `path-traversal`,
`archive-corrupt`, `archive-encrypted`, `entry-too-large`, `entry-limit`,
`unknown-index-shape`, `date-conflict`.

Une erreur sur une entrée ne doit pas annuler les entrées déjà importées. Une
archive corrompue ou interrompue laisse la bibliothèque existante utilisable et
permet une reprise.

### Correspondance avec le domaine

| Contrat | Types existants |
|---|---|
| Rôle | `MediaRole.Main`, `MediaRole.Overlay` |
| État du souvenir | `MemoryStatus.Complete`, `MediaOnly`, `OverlayOnly`, `Conflict` |
| Source de date | `DateSource.Mp4CreationTime`, `ExifDateTimeOriginal`, `ExifDateTime`, `Index`, `User` |
| Précision | `DatePrecision.Second`, `Day`, `Month`, `Year`, `Unknown` |
| Signification | `DateMeaning.ContainerCreated`, `Capture`, `Export`, `UserProvided` |
| Diagnostic | `ImportDiagnostic` |
| Compteurs | `ImportCounters` |

Les entités `Asset`, `Memory`, `SourceOccurrence`, `DateCandidate` et
`ImportBatch` du modèle d'architecture restent la référence pour le stockage.

## 4. Règles de sécurité ZIP

L'importeur traite des archives fournies par l'utilisateur, potentiellement
incomplètes ou malveillantes. Les règles suivantes sont obligatoires.

### Chemins

- Refuser les chemins absolus, les lettres de lecteur, les chemins UNC et les
  segments `..` avant toute écriture.
- Vérifier que le chemin canonique reste sous la racine cible, séparateur final
  compris ; ne pas se contenter d'un `StartsWith` naïf.
- Rejeter les noms réservés Windows (`CON`, `NUL`, `COM1`…), les points et
  espaces finaux, et les flux alternatifs (`:`).
- Ne pas suivre les liens symboliques éventuels ; les traiter comme des
  entrées non supportées.
- Préférer le streaming à l'extraction temporaire. Si une extraction est
  inévitable, utiliser un dossier temporaire contrôlé et le nettoyer.

### Limites de ressources

- Nombre maximal d'entrées par archive.
- Taille décompressée maximale par entrée et par archive.
- Rapport de compression maximal (protection ZIP bomb).
- Taille maximale d'un média et durée maximale d'inspection.
- Profondeur et nombre d'archives imbriquées : aucune archive imbriquée n'est
  ouverte sans adaptateur explicite.
- Annulation et reprise : une entrée lente ou bloquée ne doit pas figer
  l'interface ni empêcher le diagnostic.

### Contenu

- Vérifier la signature réelle ; une extension `.png` peut être un WebP, un
  exécutable ou un fichier tronqué.
- Vérifier le CRC de chaque entrée et signaler une archive corrompue.
- Ne jamais exécuter de HTML, JavaScript ou script contenu dans une archive.
- Ne pas suivre les URL trouvées dans un index. Aucun téléchargement distant
  n'est activé dans cette version.
- Ne pas journaliser les jetons, query strings signées ou chemins personnels.
- Conserver les doublons de noms comme candidats distincts ; ne pas laisser
  une entrée écraser silencieusement une autre.
- Refuser les archives chiffrées ou les méthodes de compression non prises en
  charge avec un diagnostic clair.
- Un ZIP structurellement valide ne prouve pas que l'export est complet.

## 5. Ce qui n'est pas validé

Les points suivants restent explicitement hors du contrat validé.

- **Index HTML/JSON Snapchat réel** : structure, nom des champs, dates,
  associations et liens. L'`index.html` fourni ne présentait pas de rubrique
  Souvenirs ; aucun index contemporain complet n'a été examiné.
- **Liens distants** : domaines, redirections, expiration, jetons, reprise et
  vérification de contenu. L'adaptateur de téléchargement est inactif.
- **Noms et arborescences réels** : casse, sous-dossiers, exports successifs,
  plusieurs comptes, identifiants stables ou non.
- **Signatures réelles** : proportion exacte de WebP sous `.png`, codecs
  réellement présents, présence d'EXIF dans d'autres exports.
- **Rotation** : matrice réelle, comportement du décodeur LibVLC, double
  rotation éventuelle, dimensions encodées versus affichées.
- **Calques** : ordre de composition, mélange, alpha prémultiplié, calques
  multiples, effets intégrés au média.
- **Vidéo** : lecture réelle du MP4 synthétique (non décodable par choix),
  HEVC sur une installation Windows propre, extraction de miniatures.
- **Sécurité avancée** : ZIP64, archives chiffrées, archives imbriquées,
  liens symboliques, bombes de compression et limites de performance n'ont pas
  été éprouvés à grande échelle.
- **Données de compte** : profil, abonnements, localisation, conversations et
  médias de chat restent hors périmètre par défaut.
- **Implémentation de production** : ce document décrit un contrat et un
  corpus ; l'importeur réel et le catalogue restent à intégrer.

## 6. Validation effectuée

Le 30 octobre 2026, sur Windows 11 avec .NET SDK 10.0.401 :

| Contrôle | Résultat |
|---|---|
| `dotnet build tests/Memo.TestSupport` | succès, 0 avertissement |
| `dotnet build tools/Memo.Corpus` | succès, 0 avertissement |
| `dotnet run --project tools/Memo.Corpus -- --out <temp> --seed 42 --count 20` | 6 archives, 20 souvenirs (13 photos, 7 vidéos), 18 calques (12 WebP, 6 PNG), 2 orphelins, 3 sans date |
| Génération répétée avec la même graine | archives, manifeste et LISEZMOI identiques octet pour octet |
| Signatures des médias | JPEG, PNG, WebP et MP4 reconnus par signature, y compris WebP sous `.png` |
| Répartition des rôles | `memo-media-1.zip` et `memo-media-2.zip` ne contiennent que des `-main`, `memo-overlays.zip` que des `-overlay` et le JSON |
| Conflit de rôle | deux entrées `-main` de même nom, 82 055 et 82 056 octets, toutes deux décodables |
| Entrée malveillante | `../hors-export.jpg` présent dans `memo-conflit.zip` |
| Archive corrompue | `memo-corrompu.zip` rejeté par `zipfile.BadZipFile` |
| UUID dans deux espaces | même UUID dans `memo-media-1.zip` et `memo-autre-espace.zip`, octets différents |
| `index.html` | aucun script, aucune URL distante |
| MP4 synthétique | `ftyp`/`moov`/`mdat` lisibles ; rotation 90 dans `tkhd`, 1920×1080 encodé, 1080×1920 affiché |
| `SyntheticVideo.RealMp4` | ffmpeg présent : MP4 H.264 de 287 Ko, `creation_time` UTC relu par ffprobe |

La lecture LibVLC du MP4 réel n'a pas été exercée dans cette validation. Le
MP4 structurel de `SyntheticMp4` n'est volontairement pas décodable.

## 7. Reproduire la validation

```powershell
dotnet build tests/Memo.TestSupport/Memo.TestSupport.csproj
dotnet build tools/Memo.Corpus/Memo.Corpus.csproj

dotnet run --project tools/Memo.Corpus -- --out "$env:TEMP\memo-corpus" --seed 42 --count 20
```

Inspecter ensuite les archives sans les extraire :

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
Get-ChildItem "$env:TEMP\memo-corpus\*.zip" | ForEach-Object {
    $zip = [IO.Compression.ZipFile]::OpenRead($_.FullName)
    $zip.Entries | Select-Object FullName, Length, CompressedLength
    $zip.Dispose()
}
```

Le corpus doit être identique pour une même graine. Les archives de conflit,
d'autre espace et l'archive tronquée sont des cas de test, pas des exports
valides à importer tels quels.
