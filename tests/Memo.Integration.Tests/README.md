# Tests d'intégration Memo

Ce projet protège les risques réels du pipeline d'import de `Memo.Core` en
utilisant uniquement l'API publique de la bibliothèque, une vraie base SQLite et
de vrais fichiers sur disque. Aucun service externe, aucun média réel, aucun
décodeur vidéo et aucune dépendance à ffmpeg ne sont nécessaires.

## Exécution

```powershell
dotnet test tests/Memo.Integration.Tests/Memo.Integration.Tests.csproj -c Debug
```

Prérequis : SDK .NET 10 et MSTest 4. Les tests sont parallélisés au niveau
méthode ; chaque test crée son propre dossier temporaire, supprimé au teardown.
La suite complète prend environ 25 secondes sur la machine de référence, dont
environ 24 secondes pour le test de volume.

## Ce qui est couvert

| Fichier / test | Risque protégé |
| --- | --- |
| `ArchiveOrderingTests.ThreeBatches_InEveryPermutation_ProduceIdenticalLibrary` | Les trois lots d'un export sont séparés : médias dans A, calques dans B, index/JSON dans C. Les 6 permutations sont importées dans des bibliothèques neuves. Un instantané complet (statuts, nombre de souvenirs, associations main/overlay par SHA-256, dates, candidats de date, sources, compteurs) doit être identique dans tous les cas. |
| `ReimportTests.SameBatchTwice_ThenComplement_KeepsPersonalizationWithoutDuplicates` | Réimport du même lot, puis ajout d'un lot complémentaire (calque d'un souvenir existant + nouveau souvenir). Vérifie l'absence de doublons, les compteurs, la transition `MediaOnly` → `Complete`, et la conservation du favori, de l'album et de la date corrigée, y compris après réimport du complément. |
| `SpaceIsolationTests.SameUuid_InTwoSpaces_WithDifferentContent_StaysSeparate` | Même UUID dans deux espaces sources avec des contenus différents (photo sans date dans A, vidéo datée dans B). Vérifie deux souvenirs distincts, des assets et sources distincts, l'absence de fuite de date ou de provenance entre espaces, et les comptages par espace. |
| `ConflictTests.TwoDifferentMains_ForSameUuid_ProduceStableConflict` | Deux fichiers `-main` différents pour le même UUID dans le même espace. Vérifie le statut `Conflict`, la conservation des deux assets, un principal déterministe et sa stabilité après réimport dans l'ordre inverse. |
| `DateRuleTests.LeapDayNearMidnight_FollowsTimeZone_ThisDay_AndManualCorrection` | `creation_time` MP4 à 22:30 UTC le 29 février 2024, changement de fuseau Europe/Zurich ↔ Pacific/Auckland, 29 février, date partielle (mois seul), horodatage ZIP ignoré, photo sans date. Vérifie la date effective, les candidats, `ThisDay` (avec et sans préfixe d'année), la correction manuelle exacte puis partielle, sa suppression et le retour à la date du conteneur. |
| `BackupRestoreTests.BackupThenRestore_IntoEmptyFolder_ReproducesLibraryIntegrally` | Après import, favori, exclusion, lieu, note, album réordonné, date corrigée et réglages personnalisés : sauvegarde, restauration dans un dossier vide, réouverture. La comparaison ne se limite pas aux fichiers : identifiants, espaces, cartes, candidats, overrides, sources, collections et ordre, réglages, statistiques, lots, résumés annuels/mensuels et arbre `media/` hashé doivent être identiques. |
| `RobustnessTests.TruncatedZip_ProducesDiagnostic_WithoutThrowing` | ZIP tronqué : aucun crash, diagnostic `archive.corrupt`, lot marqué `failed`, aucun souvenir créé. |
| `RobustnessTests.EntryWithParentTraversal_IsRejected_AndGoodEntryImported` | Entrée `../hors-export.jpg` : diagnostic `entry.invalid-path`, entrée valide conservée, aucun fichier écrit hors de la bibliothèque. |
| `RobustnessTests.EmptyZip_CompletesWithoutCreatingMemories` | Archive ZIP valide sans entrée : import terminé, aucun souvenir, compteurs cohérents. |
| `RobustnessTests.MissingPath_IsReportedAsFatalWithoutCrash` | Chemin inexistant : diagnostic `path.missing`, lot `failed`, import non annulé et aucun crash. |
| `RobustnessTests.Restore_RefusesNonEmptyDestination_AndPreservesItsContent` | Restauration vers un dossier non vide : exception explicite, contenu existant intact, aucun dossier temporaire résiduel. |
| `VolumeTests.OneHundredFiftyMemories_ImportWithExactCounts_AndConsistentPaging` | 150 souvenirs générés via `ExportBuilder`/`SyntheticImage`, dont 15 calques. Vérifie la fin de l'import, les compteurs et statistiques exacts, les statuts, et la cohérence de la pagination (pages de 37, ordre descendant, ordre ascendant, offset au-delà de la fin). |

## Ce qui n'est pas couvert

- Les variantes réelles et non documentées des exports Snapchat : seuls le
  format `memories/<uuid>-main|overlay.<ext>` et des index/JSON synthétiques
  sont utilisés. Les métadonnées JSON/index sont volontairement ignorées par
  l'importateur actuel.
- Le décodage réel des médias, la génération de miniatures, les composites,
  l'OCR, les visages et l'analyse d'images. Les MP4 sont structurellement
  valides mais non décodables (`SyntheticMp4`).
- L'interface graphique, l'installateur, les notifications et le comportement
  des applications consommatrices.
- L'annulation en cours d'import, les imports concurrents, les verrous de
  fichiers et la reprise après arrêt brutal du processus.
- Les très gros volumes et les performances. 150 souvenirs constituent un test
  de cohérence, pas un benchmark.
- Les migrations de schéma, les catalogues corrompus, les versions futures et
  les différences de base de fuseaux horaires entre machines.
- L'export de sélection, la sauvegarde des données dérivées (`derived/` est
  volontairement exclu) et la restauration d'une sauvegarde dont un média est
  absent.
- Les chemins et règles de nommage spécifiques à macOS/Linux. La suite est
  validée sous Windows.

## Choix de conception

- Les UUID, dates, tailles et contenus sont déterministes ; aucun test ne dépend
  du fuseau de la machine, de la culture courante ou du réseau.
- Les dates `ThisDay` utilisées sont dans le passé, ce qui garde le test valable
  indépendamment de l'année d'exécution.
- Les instantanés sont sérialisés en texte avec la culture invariante. Les
  assets sont identifiés par leur chemin relatif et leur SHA-256, jamais par un
  identifiant auto-incrémenté lorsque celui-ci dépend de l'ordre d'insertion.
- Après restauration, `manifest.json` reste présent dans la bibliothèque : il
  s'agit d'une métadonnée de sauvegarde, exclue de la comparaison sémantique.
  Le fichier `catalog.db` n'est pas comparé octet par octet (WAL, pagination et
  réorganisation interne), mais tout son contenu observable l'est.
- Aucun fichier de `src/`, aucun test existant, l'installateur et les documents
  du produit ne sont modifiés par ce projet.
