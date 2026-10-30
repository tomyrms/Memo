# Rapport de QA — Memo 0.1.0 (`feat/qa`)

Date : 30 octobre 2026

Commit testé : `c620cba` (`feat: analyses, recherche visuelle, export et finitions de la bibliotheque`)

Worktree : `memo-wt/qa`, branche `feat/qa`

Objet : QA indépendante de bout en bout, sans modification du code applicatif.

> **Mise à jour du 30 octobre 2026** — ce rapport décrit le commit `c620cba`. Plusieurs constats ont été corrigés depuis et revérifiés par build, tests et captures : QA-03 (récapitulatifs relus), QA-05 (limites ZIP), QA-12 (recherche harmonisée), QA-16 (contenu des albums), QA-18 (hash de restauration), QA-27 (captures refaites), ainsi que les défauts couverts par les issues #21–#50 (démarrage, import, lecteur, recherche, sauvegarde, migrations, notifications). QA-14 (calques étirés dans le lecteur Story) reste ouvert. Les limites restantes sont tenues à jour dans `RELEASE.md` et `PROGRESS.md`.

## 1. Verdict

La construction et les tests automatisés passent. Le cœur import/catalogue est globalement solide : ordre d'import stable hors conflit, réimport idempotent, espaces sources séparés, dates corrigées conservées, sauvegarde/restauration fidèle, chemins internes relatifs et miniatures photo fonctionnelles.

En revanche, **la version n'est pas prête pour une release de confiance** : cinq défauts P1 touchent des promesses centrales (ordre d'import déterministe, exclusions, récapitulatifs enregistrés, correction de date explicite, limites de ressources ZIP). Aucun P0 (perte de données massive, crash systématique, exécution de code) n'a été constaté. Les tests d'installation propre, d'export MP4, de notifications et d'analyse OCR/visages n'ont pas pu être exécutés.

| Sévérité | Nombre | Synthèse |
|---|---:|---|
| P0 | 0 | Aucun défaut bloquant de type crash/perte de données constaté |
| P1 | 5 | Conflit dépendant de l'ordre, exclusions ignorées à l'accueil, récapitulatifs inaccessibles, correction de date involontaire, limites ZIP absentes |
| P2 | 16 | Provenance, diagnostics, compteurs, corpus, index, recherche, fuseau, composition, récapitulatifs, albums, sauvegarde, export, navigation, lieu sans coordonnées |
| P3 | 12 | Libellés, pluralisation, états, documentation, captures, limites d'échelle, filtrage des espaces |

## 2. Environnement et méthode

| Élément | Valeur |
|---|---|
| OS | Windows 11 Professionnel, build 10.0.26200, x64 |
| SDK .NET | 10.0.401 |
| Runtime .NET | Microsoft.NETCore.App 10.0.12 |
| Machine | Intel Core i7-13700HX |
| Build | `Debug`, `win-x64`, application WinUI 3 non empaquetée |
| Bibliothèques de test | corpus synthétique `Memo.Corpus` uniquement |
| Médias réels | aucun ; un fichier de démonstration du dépôt a servi au test de miniature vidéo |

Méthode :

1. lecture de `README.md`, `PROGRESS.md`, `PRODUCT.md`, `DESIGN.md`, `docs/specification.md`, `docs/architecture.md`, `docs/formats.md` ;
2. inspection du code réel de `Memo.Core`, `Memo.Media`, `Memo.App` et `tools/Memo.Corpus` ;
3. build complet, tests Core et Media ;
4. génération et inspection binaire de deux corpus synthétiques (20 et 40 souvenirs) ;
5. harnais console **hors dépôt** référençant directement `Memo.Core` et `Memo.Media` pour les scénarios d'intégration ;
6. lecture des captures fournies avec l'outil d'image ;
7. rédaction des constats sans modifier `src/`, `tests/`, `installer/` ni les documents existants.

Les données du harnais et des corpus sont dans le dossier temporaire de la session, pas dans le dépôt. Aucun média personnel, aucune URL signée et aucun identifiant de compte n'ont été utilisés.

## 3. Commandes exécutées et résultats

### 3.1 Build

```powershell
dotnet build Memo.slnx -c Debug
```

Résultat : **succès**, 0 erreur, 12 avertissements (6 avertissements distincts, doublés dans le journal de build), durée 1 min 03 s.

Avertissements distincts :

- `MainWindow.xaml.cs(11,31)` CS0108 : `MainWindow.Current` masque `Window.Current` ;
- `MemoryTile.g.i.cs(32,65)` CS0108 : `MemoryTile.Loading` masque `FrameworkElement.Loading` ;
- `MemoMap.cs(94,21)`, `(99,21)`, `(104,17)` CS0618 : `SKPath.MoveTo`, `LineTo`, `Close` obsolètes au profit de `SKPathBuilder` ;
- `MediaStage.cs(34,18)` CS0169 : champ `_disposed` jamais utilisé.

Aucune erreur de compilation, aucun avertissement provenant de `Memo.Core` ou `Memo.Media`.

### 3.2 Tests automatisés

```powershell
dotnet test tests/Memo.Core.Tests/Memo.Core.Tests.csproj -c Debug --no-build
dotnet test tests/Memo.Media.Tests/Memo.Media.Tests.csproj -c Debug --no-build
```

| Suite | Résultat | Durée |
|---|---|---:|
| `Memo.Core.Tests` | **39/39 réussis**, 0 échec, 0 ignoré | 3 s |
| `Memo.Media.Tests` | **69/69 réussis**, 0 échec, 0 ignoré | 49 s |

Total : **108 tests réussis**. Ces tests couvrent le domaine, l'import, les dates, la sauvegarde, les formats, EXIF, WebP, rotation, composition et miniatures. Ils ne couvrent pas l'interface WinUI, l'export Media Foundation, les notifications ni l'OCR/visages réels.

### 3.3 Corpus synthétique 20 et 40

Commandes :

```powershell
dotnet run --project tools/Memo.Corpus -c Debug --no-build -- `
  --out <Temp>\corpus20 --seed 42 --count 20
dotnet run --project tools/Memo.Corpus -c Debug --no-build -- `
  --out <Temp>\corpus40 --seed 42 --count 40
```

| Mesure | Corpus 20 | Corpus 40 |
|---|---:|---:|
| Souvenirs (hors orphelins) | 20 (13 photos, 7 vidéos) | 40 (26 photos, 14 vidéos) |
| Dates annoncées par le manifeste | 17 datés, 3 sans date | 35 datés, 5 sans date |
| Calques | 18 (12 WebP, 6 PNG) | 34 (24 WebP, 10 PNG) |
| Calques orphelins | 2 | 2 |
| Souvenirs partageant des octets | 1 | 1 |
| Archives générées | 6 | 6 |
| `memo-media-1.zip` | 11 entrées, 417,5 Kio | 21 entrées, 887,5 Kio |
| `memo-media-2.zip` | 10 entrées, 485,2 Kio | 20 entrées, 891,5 Kio |
| `memo-overlays.zip` | 19 entrées, 153,5 Kio | 35 entrées, 300,6 Kio |
| `memo-conflit.zip` | 3 entrées, 157,2 Kio | 3 entrées, 157,2 Kio |
| `memo-autre-espace.zip` | 1 entrée, 0,8 Kio | 1 entrée, 0,8 Kio |
| `memo-corrompu.zip` | 1 entrée, 43,3 Kio | 1 entrée, 43,3 Kio |

Contrôles binaires et structurels :

- **Signatures** : JPEG (`FF D8 FF`), PNG (`89 50 4E 47…`), WebP (`RIFF....WEBP`) et MP4 (`ftyp` à l'offset 4) reconnus ; **0 discordance** extension/signature sur les archives valides.
- **WebP sous extension `.png`** : 9 entrées dans le corpus 20, 20 dans le corpus 40. Les octets sont bien du WebP malgré l'extension annoncée ; le test Core `WebpNamedPng_IsDetectedFromSignature_AndStoredAsWebp` passe.
- **ZIP** : les cinq archives valides s'ouvrent ; `memo-corrompu.zip` échoue à l'ouverture avec `End of Central Directory record could not be found`, comportement attendu pour une archive tronquée.
- **Conflit de rôle** : `memo-conflit.zip` contient deux entrées `memories/<uuid>-main.jpg` de tailles 82 055 et 82 056 octets, plus `../hors-export.jpg`.
- **Traversée** : `../hors-export.jpg` est présent dans `memo-conflit.zip` et n'a jamais été extrait hors de la bibliothèque (recherche récursive effectuée).
- **Calques orphelins** : 2 UUID avec `-overlay` sans `-main`, conservés en `OverlayOnly`.
- **29 février** : le JSON de `memo-overlays.zip` contient bien `2020-02-29T23:58:30+00:00` ; le cas existe donc dans les octets. En revanche, son classement dépend du fuseau (voir QA-10 et QA-13).
- **Minuit** : valeurs présentes notamment `2018-12-31T23:59:00Z`, `2019-01-01T00:00:30Z`, `2021-07-14T00:01:15Z`.
- **Déterminisme** : une seconde génération avec la même graine produit **8 fichiers identiques octet pour octet** (6 ZIP + `corpus-manifest.json` + `LISEZMOI.txt`), pour 20 et pour 40 souvenirs.

### 3.4 Harnais d'intégration hors dépôt

Le harnais est un projet console temporaire référençant `src/Memo.Core/Memo.Core.csproj` et `src/Memo.Media/Memo.Media.csproj`. Il n'ajoute aucun fichier au dépôt.

Résultats synthétiques :

| Scénario | Résultat observé | Verdict |
|---|---|---|
| Trois ordres d'import sans conflit (`overlays` d'abord, médias d'abord, mélangé) | Snapshot canonique identique : 22 souvenirs, 28 assets, 38 sources, 15 sans date | **OK** |
| Import 40 souvenirs | 42 souvenirs (40 + 2 orphelins), 48 assets, 74 sources, 14 vidéos, 28 sans date | **OK** |
| Conflit de rôle, média puis conflit / conflit puis média | Même statut `Conflict`, mais **variante principale différente** (`e37e…` contre `80d2…`) | **ÉCHEC** |
| Réimport du même lot | 0 souvenir créé, 0 mis à jour, 4 doublons, comptages inchangés, favori conservé | **OK** |
| Espaces sources | A = 22 souvenirs, B = 1 ; même UUID dans les deux, octets différents, aucune fusion ; A inchangé | **OK** |
| Deuxième import dans B | B = 10 souvenirs, A = 22 ; asset identique mutualisé (asset 2) | **OK** |
| Date proche de minuit / 29 février | En `Europe/Paris`, le souvenir 29/02 est classé 01/03/2020 ; en `UTC`, il est classé 29/02/2020 | **OK mathématiquement**, cas corpus ambigu |
| Correction de date puis réimport | Correction `2018-01-01` conservée, `DateSource.User`, candidat d'origine conservé, suppression possible | **OK** |
| Sauvegarde → restauration vide → réouverture | 22 souvenirs, 28 assets, 38 sources identiques ; album, favori, exclusion, date et lieu conservés | **OK** |
| Chemins internes | `stored_path` et `relative_path` relatifs, aucun lecteur ni `..` | **OK** |
| Miniature photo | JPEG 512×288, max 512, enregistrée et résolue | **OK** |
| Miniature vidéo synthétique | Échec propre, aucun fichier partiel (MP4 structurel non décodable) | **OK attendu** |
| Miniature vidéo de démonstration | JPEG généré | **OK** |
| Archive vide | Terminée sans diagnostic, 0 souvenir | **Défaut P3** |
| Dossier vide | Terminé sans diagnostic | **Défaut P3** |
| ZIP corrompu | Diagnostic `archive.corrupt`, lot `failed`, 0 souvenir, bibliothèque réutilisable ensuite | **OK** |
| Entrée `../` | Diagnostic `entry.invalid-path`, aucun fichier extrait, souvenir valide importé | **OK** |
| Fichier verrouillé | Diagnostic d'erreur, pas de crash | **OK** |
| Chemin inexistant / invalide | `path.missing` / `path.invalid`, pas de crash | **OK** |
| Bibliothèque absente / manifeste invalide / catalogue absent | `InvalidOperationException` explicite | **OK** |
| Restauration dans un dossier non vide | Refusée, fichier existant préservé | **OK** |
| Sauvegarde tronquée / incomplète / chemin invalide | Refusées avec exception | **OK** |
| Dossier de six ZIP | 6 archives inventoriées, 5 importées (corrompue exclue), 22 souvenirs, 2 conflits, 2 diagnostics | **OK mais cas corpus non réaliste** |

**Seul échec bloquant du harnais** : la variante principale d'un conflit de rôle dépend de l'ordre d'import (QA-01). Les autres constats sont des défauts P2/P3 ou des limites de test.

### 3.5 Captures d'écran inspectées

Captures lues : `home.png`, `library.png`, `albums.png`, `recaps.png`, `search.png`, `map.png`, `imports.png`, `settings.png`, `story.png`, `story-portrait.png`, `story-photo-portrait.png`, `home-portrait.png`, `library-portrait.png`, `albums-portrait.png`, `recaps-portrait.png`, `search-portrait.png`, `imports-portrait.png`, `settings-portrait.png`, `import-ui-portrait.png`, `screen-second.png`.

Dimensions : les captures paysage font 1320×860 et les portraits 1040×1800, conformément à `DESIGN.md`. Deux fichiers (`home1.png`, `home2.png`) sont des captures du bureau entier en 1920×1080, pas des captures de l'application ; ils ne sont pas exploitables pour la revue visuelle et ne doivent pas être publiés.

Constats visuels principaux :

- **Accueil** : hiérarchie lisible, état vide « Rien pour ce jour » utile, badge démo présent. Les libellés « 15 souvenir(s) », « 1 vidéo(s) », « 2 calque(s) » sont mécaniques. Le lien « Tout voir » de « Ce jour-là » ne filtre pas la journée.
- **Bibliothèque** : la grille est très vide quand seuls les médias datés sont visibles ; les photos du corpus sont toutes sans date et reléguées dans « Date inconnue », sous la ligne de flottaison. Les captures anciennes montrent des tuiles vidéo noires avec seulement « 0:03 » ; le code actuel de `MemoryTile` prévoit pourtant un état « Vidéo · aperçu à générer ». Les captures `recaps-portrait.png` et `search-portrait.png` montrent cet état, ce qui confirme une **incohérence de build entre captures**.
- **Albums** : « 6 souvenir(s) », « 4 souvenir(s) · sans date », « depuis le 1 juin 2021 » ; pluralisation et grammaire à corriger. Les vignettes recadrent le texte dessiné dans les images synthétiques, ce qui est attendu mais peu lisible.
- **Récapitulatifs** : « 2 souvenir(s) · 2 vidéo(s) · 0 favori(s) » ; couverture correcte ; bouton « Enregistrer » sans liste des récapitulatifs enregistrés.
- **Recherche** : la capture paysage montre « texte reconu… » alors que le XAML actuel dit « texte reconnu… » ; capture ancienne. La grille mélange médias datés puis non datés, ce qui est cohérent avec le tri.
- **Carte** : carte très sombre, frontières peu contrastées ; « 7 souvenir(s) » ; pas de zoom/pan, conforme aux limites annoncées. Le dialogue de fiche annonce encore « dans cette démonstration » même sur une bibliothèque réelle.
- **Imports** : la capture paysage 1320 est rognée à droite (« Nouvel esp… » coupé) alors que la capture portrait 1040 est complète ; il s'agit probablement d'une capture d'une fenêtre plus large recadrée, pas d'un défaut de mise en page reproductible. La capture `import-ui-portrait.png` montre « 17 souvenir(s) mis à jour » après un import neuf, les codes bruts `entry.invalid-path` et `archive.corrupt`, et « 2 erreur(s) de lecture » pour deux causes différentes.
- **Réglages** : « 1 album(s) » ; fuseau tronqué à « Stock… » (Stockholm) dans la liste ; les libellés « Animations système » et « Notification discrète » restent ambigus quand les rappels sont désactivés.
- **Story photo** : la capture portrait affiche « Date inconnue » puis « date inconnue » en sous-titre. Le code actuel de `StoryView.BuildSubtitle` n'ajoute plus la date : capture ancienne, probablement corrigée depuis.
- **Story vidéo** : `story.png` montre une **fenêtre VLC séparée** « VLC (Direct3D11 output) », alors que `story-portrait.png` montre le lecteur embarqué et que `MediaStage` utilise bien les callbacks mémoire. La capture `story.png` est donc une capture d'une ancienne build ou d'un autre worktree ; elle ne prouve pas le comportement actuel, mais elle contredit les affirmations de `PROGRESS.md`/`DESIGN.md` si elle est utilisée comme preuve.
- **Contrastes** : les couples de la palette `DESIGN.md` ont été calculés. Le minimum textuel est de **5,11:1** (accent clair `#A64B1E` sur fond `#F4F1EC`) ; le secondaire sombre sur surface secondaire est à **6,64:1** ; le texte principal sombre est à **16,33:1**. Aucun couple texte/fond ne passe sous AA (4,5:1). Les états désactivés, focus et texte sur images n'ont pas été mesurés.
- **Captures non représentatives de la build QA** : les scripts `capture.ps1` et `second-screen.ps1` du dossier de captures pointent vers un autre worktree que `memo-wt/qa`. Les captures ne doivent pas être présentées comme une preuve de la build `feat/qa` sans recapture.

## 4. Bugs

### P0 — aucun

Aucun crash systématique, aucune perte de sources ni exécution de code arbitraire constatés pendant les tests exécutés.

### P1 — défauts à corriger avant release

#### QA-01 — La variante principale d'un conflit de rôle dépend de l'ordre d'import

**Fichiers** : `src/Memo.Core/Catalog/MemoCatalog.Query.cs:45-62` (`main_ranked`, `ROW_NUMBER … ORDER BY mf.source_file_id ASC`), `src/Memo.Core/Catalog/MemoCatalog.Import.cs:246-281` (`LinkMemoryAsync`), `src/Memo.Core/Import/ImportService.cs:889-943`.

**Reproduction** (harnais, corpus 20) :

1. bibliothèque neuve, espace « Démo A » ;
2. importer `memo-media-2.zip` puis `memo-conflit.zip` ;
3. lire la carte `f6238c86-924c-4d44-ab2e-f4eb7cf5fa8c` : le média principal est `e37e9fcb…` ;
4. recommencer avec `memo-conflit.zip` puis `memo-media-2.zip` : le média principal devient `80d21bc4…`.

Le statut reste `Conflict` dans les deux cas, mais l'image affichée, composée et exportée change. Le contrat `docs/architecture.md:93` et `PRODUCT.md:47` affirme « même résultat final quel que soit l'ordre d'import ».

**Impact** : résultat non déterministe, variante arbitraire présentée à l'utilisateur, export potentiellement différent. Le choix n'est pas exposé dans l'interface.

**Correctif suggéré** : définir une résolution de conflit stable (candidat par défaut trié par SHA/chemin, ou préférence explicite), enregistrer la sélection utilisateur, et ne pas fonder le principal sur l'ordre d'insertion `source_file_id`. Ajouter un test qui compare le principal, pas seulement le statut.

#### QA-02 — « Ce jour-là », « Surprends-moi » et « Une journée à redécouvrir » ignorent les exclusions

**Fichiers** : `src/Memo.App/Pages/HomePage.xaml.cs:21,43-48,83-116,140-172` ; `src/Memo.App/Services/AppState.cs:396-400`.

`AppState.ThisDay` filtre uniquement `LocalDate`, le mois/jour et l'année antérieure. Il ne filtre ni `IsExcluded`, ni `Status == OverlayOnly`, ni `DatePrecision >= Day`. `HomePage.Surprise` et `PickDay` utilisent directement `AppState.Cards` avec les mêmes lacunes. `DiscoveryService`/`MemoCatalog.QueryThisDayAsync` implémente pourtant correctement ces filtres, mais n'est pas utilisé par l'accueil.

**Reproduction** : masquer un souvenir du 30 octobre via la bibliothèque, ouvrir l'accueil un 30 octobre ; il peut encore apparaître dans « Ce jour-là », dans « Surprends-moi » ou dans « Une journée à redécouvrir ». Un calque seul peut aussi être proposé.

**Impact** : un élément masqué réapparaît dans une sélection et dans le lecteur ; violation directe de `docs/specification.md:60` (« Les exclusions s'appliquent avant la sélection et avant toute notification »), et `PRODUCT.md:29` rappelle qu'un calque seul ne doit pas être présenté comme un média.

**Correctif suggéré** : utiliser `Library.Discovery.GetThisDayAsync`/`SurpriseAsync` avec `ThisDayOptions`, filtrer `IsExcluded`, `OverlayOnly` et la précision, et utiliser le fuseau de la bibliothèque.

#### QA-03 — Les récapitulatifs enregistrés ne sont jamais relus par l'interface

**Fichiers** : `src/Memo.App/Pages/RecapsPage.xaml.cs:234-256` ; `src/Memo.App/Services/AppState.cs:119-123,308-316`.

« Enregistrer » crée une collection `CollectionKind.Recap` et affiche « conservé dans Albums et Récapitulatifs ». Or `AppState.ReloadAsync` ne charge que `CollectionKind.Album`, `AlbumsPage` ne charge que les albums, et `RecapsPage` ne contient aucune liste de récapitulatifs enregistrés. La collection existe dans `catalog.db` mais n'est accessible par aucun écran.

**Impact** : fonctionnalité annoncée « enregistrable » (`README.md:45`, `docs/specification.md:72`) non utilisable ; l'utilisateur perd l'accès à sa sélection après redémarrage.

**Correctif suggéré** : lister les `CollectionKind.Recap` dans `RecapsPage` (charger, rouvrir, supprimer), ou les afficher dans Albums avec un filtre de type ; couvrir par un test d'intégration UI/Core.

#### QA-04 — Ouvrir la fiche d'un souvenir et cliquer « Enregistrer » crée une correction de date involontaire

**Fichiers** : `src/Memo.App/Pages/LibraryPage.xaml.cs:348-408` ; `src/Memo.App/Services/AppState.cs:206-215`.

Le `CalendarDatePicker` est initialisé avec `card.LocalDate`. À la validation, le code exécute `if (picker.Date is { } chosen) SetDateOverrideAsync(...)` sans comparer à la date d'origine. Même si l'utilisateur ne modifie que le lieu, la date devient une correction utilisateur (`DateSource.User`), un enregistrement `date_overrides` est créé, et le fuseau de classement ne s'applique plus à ce souvenir.

**Reproduction** : ouvrir « Informations et date » sur un souvenir daté par MP4, modifier seulement le champ « Lieu », cliquer « Enregistrer » ; `GetCardAsync` renvoie `DateSource.User` et `GetDateOverrideAsync` renvoie une ligne, alors qu'aucune date n'a été changée.

**Impact** : provenance de date silencieusement altérée ; les recalculs de fuseau et les rappels peuvent diverger. La correction doit rester un acte explicite (`docs/specification.md:52`).

**Correctif suggéré** : mémoriser la date initiale, n'appeler `SetDateOverrideAsync` que si la valeur a changé, et ajouter une action « Retirer la correction » dans la fiche.

#### QA-05 — Aucune limite de ressources ZIP malgré le contrat de sécurité

**Fichiers** : `src/Memo.Core/Import/ImportService.cs:437-464` (boucle sur toutes les entrées sans plafond), `:794` et `:1016-1054` (copie/hachage en flux sans limite de taille), `docs/formats.md:286-295` (limites déclarées obligatoires).

Le code ne vérifie ni nombre maximal d'entrées, ni taille décompressée par entrée/archive, ni rapport de compression, ni durée maximale d'inspection. Il n'y a pas de diagnostic `entry-too-large`/`entry-limit`/`archive-encrypted`. Une archive ZIP bombe ou une entrée énorme peut remplir le disque ou monopoliser le processus avant toute règle de reprise. Le test n'a pas été exécuté avec une archive malveillante (pas de bombe créée), mais l'absence de garde est visible dans le code et contredit le document de sécurité.

**Impact** : déni de service local et saturation disque lors de l'import d'une archive non fiable ; le contrat de `docs/formats.md` n'est pas tenu.

**Correctif suggéré** : vérifier `entry.Length`/`CompressedLength` avant lecture, plafonner le nombre d'entrées et les octets décompressés, refuser les ratios anormaux, ajouter un timeout et les diagnostics documentés ; tester avec une archive contrôlée.

### P2 — défauts significatifs

#### QA-06 — Provenance affichée en dur comme « démonstration » sur une bibliothèque réelle

**Fichiers** : `src/Memo.App/Pages/LibraryPage.xaml.cs:342` (`Line("Provenance", "Jeu de démonstration synthétique")`) ; `src/Memo.App/Pages/MapPage.xaml.cs:70` (« Coordonnées liées au souvenir dans cette démonstration. »).

**Reproduction** : ouvrir la fiche d'un souvenir d'une bibliothèque réelle ; la provenance affichée est fausse. La fiche carte affirme également une démonstration.

**Impact** : information factuelle erronée, alors que `docs/specification.md:54` promet une provenance consultable. Le vrai `archive_path`/`entry_path` est disponible dans `GetDetailsAsync` mais n'est pas utilisé.

**Correctif suggéré** : afficher `MemoryDetails.Sources` (archive, entrée, SHA, rôle), ou retirer la ligne ; supprimer la mention « démonstration » de la carte pour une bibliothèque réelle.

#### QA-07 — Les codes de diagnostic documentés ne sont pas émis

**Fichier** : `docs/formats.md:241-247` annonce `role-conflict`, `overlay-only`, `media-only`, `date-conflict`, `archive-encrypted`, `entry-too-large`, `entry-limit`, etc. Une recherche dans `src/` ne trouve aucune occurrence de ces codes.

**Reproduction** : importer `memo-conflit.zip` ; le résultat ne contient que `entry.invalid-path`, alors que la carte est en `Conflict`. Aucun diagnostic `role-conflict` n'est visible dans le bilan.

**Impact** : l'interface ne peut pas expliquer un conflit ou un calque seul ; la documentation surestime les diagnostics. Le test ne protège pas ces contrats.

**Correctif suggéré** : émettre les diagnostics dans `UpdateMemoryStatusAsync`/l'association, avec sévérité et entrée concernée ; ajouter des tests par code.

#### QA-08 — La provenance du second candidat d'un conflit de même chemin est perdue

**Fichiers** : `src/Memo.Core/Catalog/CatalogSchema.cs:48` (`UNIQUE (space_id, archive_id, entry_path)`), `src/Memo.Core/Catalog/MemoCatalog.Import.cs:141-181` (`INSERT OR IGNORE` puis `SELECT` par chemin).

**Reproduction** : `memo-conflit.zip` contient deux entrées de même nom `…-main.jpg` avec des octets différents. Après import, `memory_assets` contient les deux assets, mais un seul `source_files` est créé. Le harnais compte **1 asset sans ligne `source_files`**. `GetDetailsAsync` affiche deux sources (média + premier candidat du conflit), pas les trois occurrences attendues.

**Impact** : les octets sont conservés, mais l'archive et le chemin du second candidat ne sont plus auditables ; la promesse « plusieurs candidats conservés, provenance de chaque valeur » n'est pas tenue.

**Correctif suggéré** : rendre l'unicité dépendante du SHA/occurrence, ou ajouter une table d'occurrences séparée de l'asset ; enregistrer les deux entrées même si le chemin est identique.

#### QA-09 — Les compteurs « créés » et « mis à jour » se recouvrent

**Fichiers** : `src/Memo.Core/Import/ImportService.cs:927-936` ; `:1107-1118`.

Une mémoire créée par le premier rôle est ajoutée à `CreatedMemoryIds` ; quand le second rôle arrive, `EnsureMemoryAsync` renvoie `Created=false` et elle est aussi ajoutée à `UpdatedMemoryIds`. Les deux ensembles ne sont jamais disjoints.

**Reproduction** (harnais, import neuf de trois archives) : `MemoriesCreated=22` et `MemoriesUpdated=16` ; avec le conflit, `MemoriesUpdated=17`. La capture `import-ui-portrait.png` affiche « 17 souvenir(s) mis à jour » après un import neuf. `ImportCounters` ne documente pas cette sémantique.

**Impact** : bilan d'import trompeur, tests qui pourraient croire à une mise à jour d'une bibliothèque existante.

**Correctif suggéré** : compter les mises à jour comme `UpdatedMemoryIds.Except(CreatedMemoryIds)`, ou suivre un état final par mémoire ; adapter le libellé UI (« enrichis ») si le chevauchement est voulu.

#### QA-10 — Le corpus n'exerce pas les dates des photos et son manifeste surestime les dates importables

**Fichiers** : `tools/Memo.Corpus/CorpusGenerator.cs:157-180` (fixe `CapturedAt` pour les photos), `tests/Memo.TestSupport/SyntheticImage.cs:38-58` (dessine la date mais n'écrit pas d'EXIF), `tests/Memo.TestSupport/ExportBuilder.cs:416-433` (JSON `capturedAtUtc`), `src/Memo.Core/Import/ImportService.cs` (ne lit que l'EXIF et `creation_time` MP4 ; le JSON n'est pas lu).

**Reproduction** : importer `memo-media-1`, `memo-media-2` et `memo-overlays` dans une bibliothèque neuve. Le manifeste annonce 3 souvenirs sans date sur 20 (`corpus-manifest.json`), mais la bibliothèque contient **15 sans date sur 22** (les 13 photos et 2 orphelins), car aucune photo JPEG n'a d'EXIF. Pour 40 : 5 annoncés, **28 observés sur 42**.

**Impact** : les scénarios « photo datée », « anniversaire photo », « correction d'une photo datée » ne sont pas réellement couverts ; `docs/formats.md:347` et le manifeste annoncent 3 souvenirs sans date, alors que l'import en produit 15. Les dates visibles dans l'image ne sont pas des métadonnées.

**Correctif suggéré** : écrire un bloc EXIF `DateTimeOriginal` dans les JPEG synthétiques, ou lire le JSON synthétique via un adaptateur explicitement test-only ; ajuster le manifeste et la documentation pour distinguer « date visuelle » et « date importable ».

#### QA-11 — Les index HTML/JSON locaux ne sont pas lus ; `DateSource.Index` n'est jamais produit

**Fichiers** : `src/Memo.Core/Import/ImportService.cs:533-551` (seul `memories/` est traité ; `index.html` et `json/*` sont ignorés) ; `docs/formats.md:223-225,144-145` annonce un candidat `Index`.

Le corpus contient `index.html` et `json/memories.json` avec des `capturedAtUtc` ; après import, les photos restent sans date. Le code ne contient aucune lecture de ces entrées.

**Impact** : fonctionnalité d'index local annoncée mais absente ; les dates fournies par un index synthétique ne sont pas exploitables. À distinguer de l'index distant, volontairement inactif.

**Correctif suggéré** : implémenter un adaptateur local validé pour `index.html`/JSON (lecture comme données, sans script), produire `DateSource.Index` et des diagnostics `unknown-index-shape` ; ou retirer l'affirmation des documents et du corpus.

#### QA-12 — La recherche de la bibliothèque et celle de l'écran Recherche ne cherchent pas les mêmes champs

**Fichiers** : `src/Memo.App/Pages/LibraryPage.xaml:51-57` (« un lieu, une date, un texte OCR ») ; `src/Memo.App/Pages/LibraryPage.xaml.cs:68-75` (lieu, clé externe, date formatée uniquement) ; `src/Memo.App/Pages/SearchPage.xaml:33-39` (« Lieu, date, texte reconnu… ») ; `src/Memo.App/Services/AppState.cs:292-306` (`SearchText` → clé externe, notes, OCR uniquement).

**Reproduction** : saisir un texte OCR dans la bibliothèque : aucun résultat ; saisir un lieu dans Recherche : aucun résultat. Les deux champs promettent pourtant les trois familles.

**Impact** : recherche incohérente et attentes non tenues ; `README.md:44` et `docs/specification.md:91` parlent d'une recherche texte/OCR.

**Correctif suggéré** : centraliser une requête unique (clé, notes, OCR, lieu, date) et l'utiliser dans les deux écrans ; ou corriger les placeholders pour refléter le périmètre réel.

#### QA-13 — L'accueil et les récapitulatifs utilisent le fuseau système, pas le fuseau de la bibliothèque

**Fichiers** : `src/Memo.App/Pages/HomePage.xaml.cs:21` (`DateOnly.FromDateTime(DateTime.Now)`), `src/Memo.App/Pages/RecapsPage.xaml.cs:32,86` (même chose) ; `MemoCatalog.QueryThisDayAsync` et `ReminderRunner` utilisent pourtant `TimeZoneHelper.Resolve(TimeZoneId)`.

**Reproduction** : bibliothèque classée en `Europe/Paris`, PC en `America/New_York` ; ouvrir l'accueil ou les récapitulatifs autour de minuit. La « journée » affichée peut être décalée d'un jour par rapport aux rappels et à la découverte.

**Impact** : « Ce jour-là » et les périodes de récapitulatif ne suivent pas le fuseau de classement, contrairement à `docs/specification.md:56` et `README.md:39`.

**Correctif suggéré** : calculer `today` avec `TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneHelper.Resolve(library.Info.TimeZoneId))`, ou exposer une méthode `AppState.Today`.

#### QA-14 — Le lecteur Story étire les calques au rectangle du média, contrairement au compositeur

**Fichiers** : `src/Memo.App/Controls/MediaStage.cs:277-280` (`canvas.DrawBitmap(_overlay, dest, …)` avec le `dest` du média) ; `src/Memo.Media/ImageComposer.cs:70-89` (ajustement « contain » avec ratio préservé).

**Reproduction** : média portrait 1080×1920 avec calque paysage 1920×1080 (ou l'inverse) ; le calque est dessiné sur tout le rectangle du média et déformé. L'export via `StoryExporter` utilise `FitInside` et ne déforme pas, donc Story et export divergent.

**Impact** : déformation visuelle dans le lecteur pour un calque de ratio différent ; `docs/specification.md:66` et `docs/architecture.md:84` demandent une composition dans l'espace d'affichage.

**Correctif suggéré** : calculer un rectangle « contain » comme `ImageComposer`/`StoryExporter`, ou réutiliser le même compositeur pour l'affichage et l'export.

#### QA-15 — Récapitulatifs mensuels/saisonniers limités à l'année courante ; hiver au 28 février en année bissextile

**Fichier** : `src/Memo.App/Pages/RecapsPage.xaml.cs:41-47,93-107`.

Les mois sont proposés pour `today.Year` uniquement ; les saisons aussi. En 2028, « Hiver » se termine au 28 février et exclut le 29. L'interface ne permet pas de générer un récapitulatif mensuel ou saisonnier d'une année passée.

**Impact** : couverture incomplète de `docs/specification.md:72` et gestion incomplète du 29 février (`docs/specification.md:58`).

**Correctif suggéré** : ajouter un sélecteur d'année pour mois/saison et utiliser `DateTime.DaysInMonth(year, 2)`.

#### QA-16 — Les albums ne permettent ni retrait ni réordonnancement malgré les API et le contrat

**Fichiers** : `src/Memo.App/Pages/AlbumsPage.xaml.cs` (aucun appel à `RemoveFromCollectionAsync`/`ReorderCollectionAsync`) ; `src/Memo.Core/Catalog/MemoCatalog.Query.cs:921-962` fournit pourtant les deux opérations ; `docs/specification.md:25` annonce ajout/retrait/ordre manuel.

**Impact** : un album créé par erreur ne peut pas être corrigé sans supprimer l'album entier ; l'ordre manuel annoncé n'existe pas dans l'UI.

**Correctif suggéré** : menu contextuel « Retirer de l'album », glisser-déposer ou boutons monter/descendre, puis persistance via les méthodes existantes.

#### QA-17 — Archive vide et dossier vide terminent silencieusement

**Fichier** : `src/Memo.Core/Import/ImportService.cs:51-56` : l'avertissement `import.empty` n'est émis que si `inputList.Count == 0`. Un ZIP valide de 0 entrée, ou un dossier vide, compte comme une entrée et termine avec 0 fichier, 0 diagnostic.

**Reproduction** (harnais) : créer un ZIP vide et l'importer ; résultat `archives 1/1, créés 0, erreurs 0, diagnostics 0`. Même comportement pour un dossier vide.

**Impact** : l'utilisateur voit « Import terminé » sans comprendre pourquoi rien n'a été importé.

**Correctif suggéré** : émettre un avertissement `archive.empty`/`folder.empty` quand le nombre d'entrées ou de fichiers retenus est nul.

#### QA-18 — La restauration ne vérifie que le nombre de souvenirs et l'identifiant de bibliothèque

**Fichier** : `src/Memo.Core/Backup/BackupService.cs:237-257`.

Après extraction, le service ouvre le catalogue, compare `restoredCount` à `manifest.MemoryCount` et `restoredId` à `manifest.LibraryId`. Il ne vérifie ni `SourceCount`, ni `TotalBytes`, ni la présence de chaque fichier média, ni les SHA. Un média tronqué ou manquant dans le ZIP peut donc passer la restauration ; l'erreur n'apparaîtra qu'à la lecture.

**Impact** : restauration annoncée réussie alors que des sources sont inutilisables ; risque de fausse confiance dans la portabilité.

**Correctif suggéré** : comparer `GetStorageStatsAsync` au manifeste, vérifier l'existence et la taille de chaque `stored_path`, et optionnellement les SHA ; tester avec un média tronqué dans le ZIP.

#### QA-19 — L'export d'une sélection existe dans le cœur mais n'est branché sur aucun écran

**Fichiers** : `src/Memo.Core/Backup/BackupService.cs:277-404` (`ExportSelectionAsync`), `src/Memo.Core/Abstractions.cs:67` ; aucune occurrence dans `src/Memo.App`.

**Impact** : `README.md:47` annonce « export d'une sélection » ; l'utilisateur ne peut exporter qu'une Story vidéo, pas les fichiers d'une sélection avec `metadata.json`.

**Correctif suggéré** : ajouter une action « Exporter la sélection » dans la barre de sélection de la bibliothèque, avec choix du dossier et des calques ; couvrir par un test UI.

#### QA-20 — Pas de navigation par jour dans la bibliothèque

**Fichiers** : `src/Memo.App/Pages/LibraryPage.xaml` (filtre type/année, boutons Date inconnue/À compléter) ; `LibraryPage.xaml.cs:83-94` (groupement par mois). Aucun filtre jour, alors que `README.md:40` et `docs/specification.md:21` annoncent une navigation année/mois/jour. `MainWindow.ShowDay` existe pour les rappels mais n'est pas relié à la bibliothèque.

**Impact** : fonctionnalité annoncée absente ; « Tout voir » depuis « Ce jour-là » ne filtre pas le jour.

**Correctif suggéré** : ajouter un filtre jour (ou une vue calendrier) et brancher `ShowDay`/`OpenLibrary(day)` sur une requête filtrée.

#### QA-33 — Un lieu saisi sans coordonnées est silencieusement supprimé

**Fichiers** : `src/Memo.App/Pages/LibraryPage.xaml.cs:410-418` ; `src/Memo.Core/Catalog/MemoCatalog.Query.cs:593-636` (`SetLocationAsync`).

La fiche permet de saisir un libellé de lieu et deux `NumberBox` de coordonnées. À la validation, si l'une des coordonnées est `NaN`, le code force `lat = null; lon = null;` puis appelle `SetLocationAsync(null, null, label)`. Or `SetLocationAsync` supprime la ligne `locations` dès que `latitude` est nul : le libellé seul n'est jamais enregistré.

**Reproduction** : ouvrir la fiche d'un souvenir, saisir « Lausanne » sans latitude/longitude, cliquer « Enregistrer » ; aucun lieu n'est conservé. Si un lieu existait déjà, il est supprimé.

**Impact** : perte silencieuse d'une saisie utilisateur, alors que la carte et la recherche de lieu reposent sur ces données (`docs/specification.md:74`).

**Correctif suggéré** : autoriser un lieu sans coordonnées (label seul) avec une colonne `locations` nullable, ou exiger les deux coordonnées et afficher une validation explicite avant d'effacer.

### P3 — finitions, documentation et robustesse

#### QA-21 — Pluralisation mécanique « (s) » dans toute l'interface

Exemples actuels : `HomePage.xaml.cs:88` (« 15 souvenir(s) », « 2 calque(s) »), `HomePage.xaml.cs:190` (« 1 vidéo(s) »), `RecapsPage.xaml.cs:156` (« 2 souvenir(s) · 2 vidéo(s) · 0 favori(s) »), `AlbumsPage.xaml.cs:54` (« 4 souvenir(s) »), `MapPage.xaml.cs:41` (« 7 souvenir(s) »), `SettingsPage.xaml.cs:60` (« 1 album(s) »), `ImportsPage.xaml.cs:191,202,284`, `LibraryPage.xaml.cs:100,151`.

**Impact** : qualité perçue, notamment dans les captures de revue. `PRODUCT.md:63` demande des libellés courts et concrets.

**Correctif suggéré** : helper `Plural(count, "souvenir", "souvenirs")` avec règles 0/1/2, et tests de libellés.

#### QA-22 — Le bilan d'import expose des codes techniques et compte comme « erreurs de lecture » des causes différentes

**Fichier** : `src/Memo.App/Pages/ImportsPage.xaml.cs:183-205`.

`Diagnostics` affiche `d.Code` en titre (`entry.invalid-path`, `archive.corrupt`) et `ResultNote`/`Notice` résument `Counters.Errors` en « erreur(s) de lecture ». Une entrée `../` refusée n'est pas une erreur de lecture ; un conflit n'apparaît pas.

**Impact** : message peu actionnable, comptage ambigu.

**Correctif suggéré** : mapper les codes vers des libellés français, distinguer « refusé pour sécurité », « archive illisible », « élément ignoré », et afficher le nom d'archive/entrée.

#### QA-23 — Le manifeste de sauvegarde annonce « settings » sans entrée correspondante

**Fichier** : `src/Memo.Core/Backup/BackupService.cs:48-57`.

`Included` contient `catalog.db`, `library.json`, `media/`, `settings`, mais le ZIP ne contient aucune entrée `settings` ; les réglages sont dans `catalog.db`. Le harnais l'a observé (31 entrées : 3 métadonnées + 28 médias).

**Impact** : ambiguïté pour un outil de vérification ou un utilisateur qui inspecte le ZIP.

**Correctif suggéré** : écrire « réglages inclus dans catalog.db » ou ajouter un fichier `settings.json` explicite.

#### QA-24 — Une miniature enregistrée mais illisible laisse une tuile vide sans état

**Fichier** : `src/Memo.App/Controls/MemoryTile.xaml.cs:75-107`.

Le cas « pas de miniature » affiche bien « Vidéo/Photo · aperçu à générer ». En revanche, `BitmapImage.ImageFailed` masque seulement le spinner et laisse `NoPreview` caché : une miniature corrompue produit une tuile vide sans explication.

**Correctif suggéré** : sur `ImageFailed`, afficher le même état `NoPreview` que pour l'absence de fichier.

#### QA-25 — Libellés tronqués et casse incohérente dans les captures

- « Stock… » pour Stockholm dans la liste des fuseaux (`settings-portrait.png`) ;
- « texte reconu… » dans l'ancienne capture `search.png` (le XAML actuel est corrigé) ;
- « Date inconnue » / « date inconnue » dans l'ancienne capture `story-photo-portrait.png` (le code actuel n'ajoute plus la date au sous-titre) ;
- « depuis le 1 juin 2021 » au lieu de « 1er juin 2021 » (`albums.png`).

Ces points sont en partie des captures anciennes ; à revérifier après recapture depuis la build QA. Le fuseau tronqué et la grammaire restent des défauts de libellé actuels.

#### QA-26 — Documentation : liens et fichiers absents sur `feat/qa`

`README.md:98-105` référence `docs/install.md` et `docs/release.md` : **ces fichiers n'existent pas dans le worktree `feat/qa`** (ni `scripts/build-installer.ps1`, ni `installer/`, ni `LICENSES/`, malgré `README.md:79,84` et `PROGRESS.md:28-29`). Ils existent sur `main`/`feat/installer`, mais la branche testée ne les contient pas.

**Impact** : installation, désinstallation, licences et release non vérifiables depuis cette branche ; liens README cassés. C'est un défaut d'intégration de branche, pas nécessairement un défaut du produit final.

**Correctif suggéré** : merger les fichiers d'installation/licences ou retirer les liens/claims de la branche QA avant de publier.

#### QA-27 — Les captures ne proviennent pas de la build QA

Les scripts de capture du dossier de captures pointent vers un autre worktree (`source/Memo`) que le worktree testé (`memo-wt/qa`) et ne fixent pas la bibliothèque. Les captures mélangent des états anciens (fenêtre VLC séparée, tuiles noires, statuts anglais, faute « reconu ») et des états récents (« Partiel », placeholder « aperçu à générer »). Elles ne constituent pas une preuve de la build `feat/qa`.

**Correctif suggéré** : recapturer depuis la build QA, avec une bibliothèque de test nommée et sans données personnelles ; conserver un manifeste des captures (date, commit, scénario).

#### QA-28 — Raccourci Ctrl+F et tests de largeur annoncés mais non implémentés/prouvés

`DESIGN.md:93` annonce Ctrl+F (recherche) et des tests à 1024/1280/1600 px avec barre latérale compacte. Aucun `KeyboardAccelerator` Ctrl+F n'existe dans `src/Memo.App` ; les captures disponibles montrent 1040 et 1320, pas 1024/1280/1600, et la barre latérale reste à 250 px à 1040. Les états de focus/lecteur d'écran ne sont pas testés.

**Correctif suggéré** : ajouter l'accélérateur, tester les largeurs annoncées, ou corriger `DESIGN.md` pour refléter le comportement réel.

#### QA-29 — « imports/ provenance des lots » n'est pas ce que le code stocke

`README.md:59` décrit `imports/` comme la provenance des lots. Dans `ImportService`, `imports/` ne contient que des fichiers temporaires `import-*.tmp`, supprimés après copie ; la provenance réelle est dans `archives.source_path`/`source_files.entry_path` de `catalog.db`.

**Impact** : documentation du stockage inexacte ; un utilisateur qui sauvegarde `imports/` ne sauvegarde pas la provenance.

**Correctif suggéré** : corriger le README, ou écrire un manifeste de provenance dans `imports/`.

#### QA-30 — Plafonds silencieux à 100 000 cartes et 500 résultats de recherche

`MemoCatalog.BuildCardSql` borne `Limit` à 100 000 (`MemoCatalog.Query.cs:265`) ; `AppState.ReloadAsync` demande `int.MaxValue` mais reçoit au plus 100 000 cartes ; `AppState.SearchAsync` limite à 500 résultats sans pagination. Aucun message n'indique la troncature.

**Impact** : bibliothèque volumineuse ou recherche large silencieusement incomplète ; `PRODUCT.md:16` annonce une bibliothèque potentiellement volumineuse.

**Correctif suggéré** : pagination explicite, compteur total, ou limite documentée et signalée.

#### QA-31 — Les espaces sources ne sont pas filtrables dans la bibliothèque

`AppState.ReloadAsync` charge toutes les cartes de tous les espaces ; `LibraryPage` n'a pas de filtre d'espace et n'affiche pas `SpaceName`. `CurrentSpaceId` ne sert qu'à l'import.

**Impact** : plusieurs comptes sont séparés en base mais mélangés à l'écran ; risque de confusion, voire de confidentialité si un utilisateur croit voir un seul compte.

**Correctif suggéré** : filtre d'espace dans la bibliothèque et affichage discret du nom d'espace dans la fiche.

#### QA-32 — Un lot avec une entrée `../` peut être marqué « terminé » malgré `Errors > 0`

`ImportService.AddError` incrémente `Errors` mais `SetFatal` n'est pas appelé pour `entry.invalid-path` ; `RunAsync` calcule `status = context.Fatal ? "failed" : "completed"`. Selon le chemin, l'historique peut afficher « Terminé » alors que le bilan contient une erreur de sécurité. À vérifier avec un import ne contenant que `memo-conflit.zip` et sans archive corrompue.

**Correctif suggéré** : définir une politique claire (avertissement non fatal vs lot partiel) et propager le statut `partiel` de façon cohérente.

## 5. Cohérence documentaire

### Ce qui est confirmé par l'exécution

- Build `Debug` et tests 39/39 + 69/69 ;
- import multi-ZIP, idempotence, espaces séparés, dates corrigées, sauvegarde/restauration, chemins relatifs ;
- corpus déterministe, signatures, ZIP, WebP sous `.png`, calques orphelins, entrée `../`, archive corrompue ;
- miniatures photo, échec propre des vidéos structurelles, miniature vidéo de démonstration ;
- palette et contrastes de `DESIGN.md` (calculs, pas capture).

### Affirmations non prouvées ou contredites

| Document | Affirmation | Constat |
|---|---|---|
| `README.md:5,38-49` | Application fonctionnelle, import/recherche/récap/export/rappels implémentés | Le cœur passe, mais récapitulatifs enregistrés inaccessibles, export de sélection non branché, limites ZIP absentes, accueil ignore les exclusions |
| `README.md:13-15,79,84` | Installateur, release, `scripts/`, `LICENSES/` | Absents de `feat/qa` ; non testés |
| `README.md:98-105` | Liens `docs/install.md`, `docs/release.md` | Fichiers absents de `feat/qa` |
| `PROGRESS.md:7` | « fonctionnelle sur bibliothèque réelle » | Seul le corpus synthétique a été testé ici ; aucune bibliothèque réelle |
| `PROGRESS.md:33` | Import du corpus 22 + 1, 21 médias, 18 calques, 2 diagnostics | Non reproduit à l'identique : le harnais obtient 22 A + 1 B, 18 calques, et 21 ou 22 médias selon l'inclusion du conflit ; écart de comptage à expliquer |
| `PROGRESS.md:34` | 13 OCR, 13 empreintes, 5,7 s | Non reproduit ; l'analyse OCR/visages n'a pas été exécutée |
| `PROGRESS.md:36` | Lecture HEVC et photo avec calque vérifiées à l'écran | Non reproduit ; `story.png` montre une fenêtre VLC séparée, contraire à la décision « frames mémoire » |
| `PROGRESS.md:52` | Notifications non observées | Toujours vrai ; non testé |
| `docs/formats.md:336-337` | « l'importeur réel et le catalogue restent à intégrer » | Faux : `ImportService`/`MemoCatalog` existent et sont testés. Section de document obsolète |
| `docs/formats.md:241-247` | Diagnostics `role-conflict`, `overlay-only`, `date-conflict`, etc. | Non émis dans le code |
| `docs/formats.md:286-295` | Limites de ressources obligatoires | Non implémentées |
| `docs/architecture.md:3` | « Document de conception, pas une description de code déjà implémenté » | Exact pour ce document, mais README/PROGRESS le présentent comme architecture du produit ; ne pas l'utiliser comme preuve d'implémentation |
| `DESIGN.md:111` | « plus de fenêtre VLC séparée » | Code actuel conforme, mais capture `story.png` contradictoire ; à recapturer |
| `DESIGN.md:92-93` | Tests 1024/1280/1600, barre compacte, Ctrl+F | Non prouvés ; aucun accélérateur Ctrl+F dans le code |
| `DESIGN.md:105` | Revues sur captures réelles 1320×860 et 1040×1800 | Captures présentes mais provenant d'un autre worktree/build |

## 6. Ce qui n'a pas pu être testé

- **Installation Windows propre** : `installer/`, `scripts/` et les documents d'installation ne sont pas dans `feat/qa` ; aucun `MemoSetup-0.1.0.exe` n'a été exécuté. SmartScreen, désinstallation, mise à jour et absence d'élévation ne sont pas vérifiés.
- **Notifications Windows** : réglages système désactivés sur la machine ; aucun toast observé. La tâche `schtasks`, le mode discret et l'activation par notification n'ont pas été testés de bout en bout.
- **Interface pilotée à la souris/clavier** : pas de test d'interaction réel ; les constats UI sont issus du code et des captures. Focus, lecteur d'écran, tabulation, Ctrl+F, haute DPI, 1024/1280/1600 px non testés.
- **Export MP4** : `StoryExporter` n'a pas été exécuté ; Media Foundation, H.264/AAC, HEVC en entrée, audio, annulation, disque plein et orientation réelle restent à valider.
- **OCR local** : dépend du pack de langue Windows ; aucun OCR réel exécuté. L'absence de pack est traitée comme un diagnostic, mais l'interface n'affiche pas clairement l'échec (le bilan peut ressembler à un succès avec 0 OCR).
- **Groupes de visages / recherche visuelle** : non exécutés ; les scores et le tri « de la plus proche à la plus éloignée » ne sont pas vérifiés.
- **HEVC/WebP sur machine propre** : non testé sans extensions ; `README.md:92` le reconnaît déjà.
- **Archives réelles Snapchat** : aucun export réel, aucun index HTML/JSON réel, aucun lien distant ; le corpus est synthétique.
- **Sécurité ZIP avancée** : ZIP64, archive chiffrée, liens symboliques, bombe de compression, très grande archive, disque plein non exécutés. La capture `screen-second.png` montre une archive de 2,11 Go / 1759 entrées en cours, mais elle n'a pas été produite par cette QA.
- **Performance** : 10 000 éléments, virtualisation, mémoire, import parallèle, annulation/reprise réelle non mesurés.
- **Sauvegarde sur un autre PC** : restauration dans un dossier vide sur la même machine seulement ; chemins relatifs vérifiés, mais pas de copie vers une autre machine.
- **Récapitulatifs enregistrés** : code inspecté ; aucun test UI de relecture (le défaut QA-03 est justement l'absence d'écran).
- **Multi-espaces dans l'UI** : séparation en base vérifiée, filtre d'espace non disponible.

## 7. Recommandations priorisées

1. **Avant release** : corriger QA-01 (conflit déterministe + choix), QA-02 (exclusions/Découverte), QA-03 (relire les récapitulatifs), QA-04 (correction de date explicite), QA-05 (limites ZIP).
2. **Compléter les tests** : conflit avec comparaison du média principal, exclusion dans « Ce jour-là », sauvegarde/relecture d'un récapitulatif, ouverture/fermeture de la fiche sans changement de date, corpus avec EXIF réel, archive vide, restauration avec média tronqué.
3. **Corriger les contrats documentaires** : ajouter ou retirer `docs/install.md`, `docs/release.md`, `LICENSES/`, `scripts/`, `installer/` ; mettre à jour `docs/formats.md` (section « non intégré », diagnostics, limites) et `PROGRESS.md` (compteurs, OCR, HEVC) ; ne pas présenter les captures d'un autre worktree comme preuve.
4. **Unifier les parcours** : recherche bibliothèque/Recherche, fuseau de la bibliothèque partout, compositeur de calques partagé entre Story et export, filtre d'espace, navigation par jour, export de sélection.
5. **Finitions UI** : pluralisation, libellés de diagnostic, états de miniature corrompue, grammaire des dates, largeur des libellés de fuseau.
6. **Sécurité/robustesse** : limites ZIP, vérification d'intégrité de restauration, symlinks/jonctions, archives chiffrées, disque plein, timeout et reprise.

## 8. Annexes — commandes et artefacts

```powershell
# Build
dotnet build Memo.slnx -c Debug

# Tests
dotnet test tests/Memo.Core.Tests/Memo.Core.Tests.csproj -c Debug --no-build
dotnet test tests/Memo.Media.Tests/Memo.Media.Tests.csproj -c Debug --no-build

# Corpus
dotnet run --project tools/Memo.Corpus -c Debug --no-build -- `
  --out <Temp>\corpus20 --seed 42 --count 20
dotnet run --project tools/Memo.Corpus -c Debug --no-build -- `
  --out <Temp>\corpus40 --seed 42 --count 40

# Harnais hors dépôt (référence Memo.Core et Memo.Media)
dotnet run --project <Temp>\memo-qa\harness -c Debug --no-build
```

Aucun fichier de `src/`, `tests/`, `installer/` ni document existant n'a été modifié. Seuls `docs/qa-report.md` et `docs/manual-tests.md` sont ajoutés.

## Suites données (30 octobre 2026, commit cca770f)

- **QA-01** corrigé : la variante principale d'un conflit est choisie par empreinte SHA-256, indépendamment de l'ordre des archives ; test d'intégration ajusté.
- **QA-02** corrigé : exclusions et précision de date appliquées à « Ce jour-là », « Surprends-moi » et « Une journée à redécouvrir » ; le jour courant suit le fuseau de la bibliothèque.
- **QA-03** corrigé : les récapitulatifs enregistrés sont chargés depuis le catalogue et affichés dans l'écran Récapitulatifs.
- **QA-04** corrigé : aucune correction de date n'est créée si la date n'a pas changé ; vider le sélecteur remet la date en inconnue.
- **QA-05** corrigé : limites d'entrées, de taille décompressée, de taille par entrée et de ratio de compression, avec diagnostic.
- **Provenance** corrigée dans la fiche d'un souvenir (bibliothèque réelle au lieu de « démonstration »).
- **Son** : le lecteur démarre non muet et le bouton HP reflète l'état.
- Les 12 tests d'intégration passent après ces changements.

Restent ouverts : pluralisation « (s) », codes de diagnostic bruts, export d'une sélection non branché, provenance détaillée du second candidat de conflit, tests de machine Windows propre et de très gros volumes.
