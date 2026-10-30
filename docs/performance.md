# Performance sur une bibliothèque volumineuse

Mesures réalisées le 30 octobre 2026 sur une bibliothèque synthétique de **1 260 souvenirs**,
sans média réel et sans réseau. Aucun code applicatif n'a été modifié : le harnais vit hors du
dépôt et référence `Memo.Core` et `Memo.Media`.

Le document décrit ce qui a été mesuré, les chiffres obtenus, les points de rupture observés et
les optimisations recommandées. Les recommandations ne sont pas implémentées ici ; elles doivent
être validées par une seconde campagne après modification.

## Environnement

| Élément | Valeur |
|---|---|
| Machine | HP OMEN 17-cm2xxx |
| Processeur | Intel Core i7-13700HX, 16 cœurs / 24 threads, 2,1 GHz de base |
| Mémoire | 32 Go (33 980 952 576 octets) |
| Disque système | SK hynix PC801 1 To, NVMe, NTFS (`C:`) |
| Système | Windows 11 Professionnel, build 26200 |
| Runtime | .NET 10.0.12, SDK 10.0.401, x64 |
| GC | workstation, non concurrent au lancement du harnais |
| Fuseau | `W. Europe Standard Time` |
| Réseau | aucun accès utilisé |

La machine n'était pas isolée : d'autres processus (conteneurs, builds, journaux) ont fait varier
l'espace libre entre environ 0,5 et 8 Gio pendant la campagne. Deux exécutions complètes à
1 200 souvenirs ont été conservées pour mesurer cette variabilité. Les temps inférieurs à
100 ms doivent être lus comme des ordres de grandeur, pas comme des constantes.

## Corpus et protocole

### Corpus

`tools/Memo.Corpus` limite chaque exécution à 500 souvenirs. Le corpus de 1 200 éléments a donc
été généré en trois lots déterministes :

| Lot | Graine | Souvenirs demandés | Souvenirs produits |
|---|---:|---:|---:|
| 1 | 42 | 500 | 500 |
| 2 | 43 | 500 | 500 |
| 3 | 44 | 200 | 200 |

Total : **18 archives ZIP, 63,7 Mio**, donnant **1 260 souvenirs** après ajout des calques
orphelins et des cas limites du générateur.

| Contenu | Valeur |
|---|---:|
| Souvenirs | 1 260 |
| Photos / vidéos (éléments principaux) | 799 / 401 |
| Médias principaux importés | 1 206 |
| Calques importés | 1 020 |
| Fichiers sources | 2 226 |
| Assets uniques (SHA-256) | 1 211 |
| Entrées examinées dans les ZIP | 2 238 |
| Archives en erreur | 3 (tronquée) + 3 entrées `../` refusées |
| Doublons d'archive ignorés | 3 |

Les photos synthétiques font 1080 × 1920 ou 1920 × 1080 (JPEG), les calques sont WebP ou PNG.
Les MP4 synthétiques sont des conteneurs minimaux **non décodables** : ils permettent de mesurer
l'import et les requêtes, pas la lecture vidéo ni les miniatures vidéo.

### Harnais

Le harnais est une application console `net10.0` créée dans `$Root\harness`, avec des
`ProjectReference` absolus vers `src/Memo.Core` et `src/Memo.Media`. Il est compilé en Release.
Le script `scripts/perf-check.ps1` le génère, le compile et l'exécute ; il n'ajoute aucun fichier
au dépôt en dehors de ce script et de ce document.

Séquence mesurée :

1. création de la bibliothèque (`MemoLibrary.CreateAsync`) ;
2. import de toutes les archives en un seul `ImportService.RunAsync`, avec un échantillonneur
   de progression toutes les 200 ms (mémoire, WAL, phases) ;
3. réouverture à froid puis à chaud ;
4. requêtes : page 60, page 500, offsets 500/1 000/5 000, recherche large et précise,
   `CountAsync`, résumés annuels et mensuels, fiche, détails, dates extrêmes, `GetThisDayAsync`,
   filtre lieu ;
5. génération de 200 miniatures photo (SkiaSharp, 512 px, qualité 82) et tentative de 2 miniatures
   vidéo ;
6. sauvegarde ZIP complète, restauration dans un dossier vide, vérification du nombre de
   souvenirs ;
7. test de montée en charge : copie de la bibliothèque, duplication des lignes SQLite (médias
   partagés) pour obtenir 10 080, 15 120 et 20 160 souvenirs, puis mêmes requêtes.

Chaque requête est exécutée une fois à blanc, puis `Iterations` fois (5 par défaut) ; la médiane
est reportée. Chaque appel ouvre sa propre connexion SQLite, comme dans l'application.

### Deux exécutions complètes

| Métrique | Exécution A | Exécution B |
|---|---:|---:|
| Durée de l'import | 160,4 s | 187,5 s |
| Débit d'entrées | 14,0/s | 11,9/s |
| Première page (60) | 31,8 ms | 40,9 ms |
| Page 500 | 43,7 ms | 50,8 ms |
| Recherche large « a » | 41,1 ms | 52,6 ms |
| `CountAsync` | 25,6 ms | 34,3 ms |
| 200 miniatures photo | 8,75 s | 10,15 s |
| Sauvegarde | 4,75 s | 7,55 s |
| Restauration | 2,73 s | 2,92 s |

L'exécution B s'est déroulée avec moins de 1 Gio libre et davantage d'activité disque : elle est
10 à 60 % plus lente. Les tableaux suivants utilisent l'exécution A, avec les valeurs B en note
quand l'écart est significatif.

## Résultats

### Import et stockage

| Métrique | Corpus 300 (315 souvenirs) | Corpus 1 200 (1 260 souvenirs) |
|---|---:|---:|
| Archives | 6 | 18 |
| Entrées examinées | 561 | 2 238 |
| Médias + calques importés | 557 | 2 226 |
| Durée totale | 67,8 s | 160,4 s |
| Débit d'entrées | 8,3/s | 14,0/s |
| Débit de fichiers média | 8,2/s | 13,9/s |
| Coût par fichier source | 121,7 ms | 72,1 ms |
| Débit octets (ZIP lus) | 0,23 Mio/s | 0,40 Mio/s |
| Mémoire de travail avant → après | 42,1 → 58,5 Mio | 42,0 → 54,7 Mio |
| Pic de mémoire du processus | 59,4 Mio | 61,9 Mio |
| Allocations managées | 162,8 Mio | 646,1 Mio |
| Handles avant → après | 283 → 292 | 283 → 285 |
| WAL maximal échantillonné | 0 Mio | 0 Mio |

Le débit du lot de 300 est plus faible que celui du lot de 1 200 : la première exécution paie
le démarrage à froid des assemblies et du cache disque. Le chiffre à retenir est le coût par
fichier, **environ 70 à 120 ms**, soit 14 fichiers/s au mieux sur cette machine.

Stockage après import :

| Élément | 315 souvenirs | 1 260 souvenirs |
|---|---:|---:|
| `catalog.db` | 0,6 Mio | 1,74 Mio |
| `media/` | 15,2 Mio | 60,9 Mio |
| `derived/` | 0 Mio | 0 Mio |
| Sauvegarde ZIP | 12,7 Mio | 50,6 Mio |
| Bibliothèque restaurée | 15,8 Mio | 62,6 Mio |
| Pages SQLite | 151 | 445 |
| Intégrité `PRAGMA integrity_check` | ok | ok |

Le catalogue représente environ **1,4 Kio par souvenir** ; les médias synthétiques environ
48 Kio par souvenir. Ces ratios ne préjugent pas des médias réels (HEIC, 4K, vidéos longues).

### Requêtes à 1 260 souvenirs

Médiane sur 5 passes, en millisecondes, avec le nombre de résultats retournés.

| Requête | Médiane | min–max | Résultats |
|---|---:|---:|---:|
| Première page (60) | 31,8 | 26,8–36,0 | 60 |
| Page 500, toutes dates | 43,7 | 38,3–48,6 | 500 |
| Page 500, offset 500 | 35,8 | 28,9–39,8 | 500 |
| Page 500, offset 1 000 | 37,7 | 35,4–41,4 | 260 |
| Page 500, offset 5 000 | 34,2 | 28,1–38,3 | 0 |
| Recherche large « a » | 41,1 | 40,1–48,9 | 500 (1 126 trouvés) |
| Recherche précise (préfixe UUID) | 33,5 | 22,3–43,9 | 1 |
| Recherche sans résultat | 26,6 | 22,0–27,5 | 0 |
| `CountAsync` (tout) | 25,6 | 19,1–29,4 | 1 260 |
| `CountAsync` (recherche « a ») | 24,4 | 22,4–26,9 | 1 126 |
| `GetYearSummariesAsync` | 13,5 | 11,4–16,4 | 7 années |
| `GetMonthSummariesAsync` | 9,3 | 8,4–10,2 | 73 mois |
| `GetCardAsync` | 19,5 | 16,1–22,3 | 1 |
| `GetDetailsAsync` | 32,1 | 29,4–34,4 | 1 |
| `GetEarliestDateAsync` | 8,5 | 8,2–8,8 | 1 |
| `GetLatestDateAsync` | 8,2 | 7,1–9,1 | 1 |
| `GetThisDayAsync` (31/12) | 44,5 | 39,8–50,0 | 2 |
| `GetThisDayAsync` (29/02) | 20,7 | 15,7–23,2 | 0 |
| Avec lieu (page 500) | 25,1 | 20,4–32,7 | 126 |

Le 29/02 ne retourne rien : le souvenir de `2020-02-29 23:58:30 UTC` est classé au
1er mars en heure locale (`W. Europe Standard Time`). C'est le comportement attendu du traitement
des dates proches de minuit, pas une anomalie de requête.

`GetDetailsAsync` coûte 32 ms alors que `GetCardAsync` en coûte 19,5 : la fiche détaillée
enchaîne plusieurs connexions (carte, candidates, override, sources, collections, note) au lieu
d'en ouvrir une seule.

### Montée en charge du catalogue

Bibliothèque clonée, lignes SQLite dupliquées, médias partagés. Les temps absolus ne sont pas
directement comparables à ceux de la bibliothèque live (le clone est compact et les médias ne
sont pas recopiés), mais la forme de croissance est significative.

| Souvenirs | Première page | Page 500 | Offset 5 000 | Recherche « a » | `CountAsync` | Résumé annuel | `ThisDay` 31/12 | 100 000 cartes |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 260 (live) | 31,8 | 43,7 | 34,2 | 41,1 | 25,6 | 13,5 | 44,5 | 40,5 |
| 10 080 | 48,2 | 50,6 | 64,1 | 169,5 | 146,9 | 50,8 | 195,2 | 341,5 |
| 15 120 | 75,6 | 79,4 | 266,3 | 188,5 | 173,1 | 66,3 | 214,3 | 375,6 |
| 20 160 | 216,0 | 231,6 | 298,9 | 294,6 | 220,5 | 83,6 | 296,1 | 500,5 |

Valeurs en millisecondes. Le `QueryAsync` « 100 000 cartes » correspond à `Limit = 100 000`
et retourne toutes les cartes ; à 20 160 souvenirs il alloue 11,7 Mio et ne fait croître le
working set que de 4,7 Mio.

Il n'y a pas de point de rupture brutal à 1 260. En revanche :

- **entre 15 000 et 20 000 souvenirs**, la première page passe de 76 à 216 ms et la page 500 de
  79 à 232 ms ; le budget de 100 ms pour un premier affichage est dépassé ;
- la pagination profonde (`OFFSET 5 000`) passe de 64 ms à 10 000 à 266 ms à 15 000, puis 299 ms
  à 20 000 ;
- la recherche large passe de 41 ms à 1 260 à 170 ms à 10 000 et 295 ms à 20 000 ;
- `CountAsync` passe de 26 ms à 147 ms puis 221 ms ;
- `GetThisDayAsync` passe de 44 ms à 195 ms puis 296 ms.

La marche entre 15 000 et 20 000 suggère un passage du tri `ORDER BY` sur un tri en mémoire à un
tri temporaire sur disque (ou un changement de plan SQLite). Elle n'a pas été confirmée par
`EXPLAIN QUERY PLAN` ; c'est une hypothèse à vérifier avant d'optimiser.

### Miniatures

| Métrique | Valeur (exécution A) |
|---|---:|
| Photos traitées | 200 |
| Durée totale | 8,75 s |
| Débit | 22,9 images/s |
| Médiane par image | 43,3 ms |
| p95 | 51,0 ms |
| Maximum | 126,8 ms |
| Octets produits | 2,24 Mio |
| Résolution des chemins (`GetAssetPath`) | 1,17 s, soit 5,85 ms par appel |
| Working set avant → après | +2,1 Mio |
| Pic du processus | 97,3 Mio |
| Allocations managées | 46,4 Mio |
| Échecs | 0 |

Les 200 miniatures ont été générées **séquentiellement**. À 23 images/s, 1 260 photos demandent
environ 55 s et 10 000 photos environ 7 minutes. Une première grille de 60 vignettes coûte donc
environ 2,6 s si aucune miniature n'est en cache, alors que la requête de cartes ne coûte que
32 ms : le premier affichage est dominé par le décodage, pas par SQLite.

La résolution des chemins ajoute 1,17 s pour 200 appels, car `GetAssetPath` ouvre une connexion
SQLite par média. C'est un coût caché qui n'existe pas si le chemin est déjà connu.

Vidéos : 2 tentatives, 0 réussite. Diagnostic constant :
« La lecture s'est terminée avant qu'une image ne soit disponible. » Les MP4 synthétiques ne
contiennent pas de piste vidéo décodable. Aucun MP4 réel n'étant disponible, la génération de
20 miniatures vidéo **n'a pas été mesurée**.

### Sauvegarde et restauration

| Métrique | 315 souvenirs | 1 260 souvenirs (A) | 1 260 souvenirs (B) |
|---|---:|---:|---:|
| Sauvegarde | 1,44 s | 4,75 s | 7,55 s |
| Taille du ZIP | 12,7 Mio | 50,6 Mio | 50,6 Mio |
| Débit de sauvegarde | 8,8 Mio/s | 10,6 Mio/s | 6,7 Mio/s |
| Restauration | 1,05 s | 2,73 s | 2,92 s |
| Taille restaurée | 15,8 Mio | 62,6 Mio | 62,6 Mio |
| Débit de restauration | 15,1 Mio/s | 22,9 Mio/s | 21,4 Mio/s |
| Souvenirs vérifiés | 315 | 1 260 | 1 260 |

La sauvegarde produit un ZIP plus petit que la bibliothèque sur ce corpus synthétique, parce que
les images de test se compressent. Avec des JPEG/MP4 réels, le ZIP sera proche de la taille des
médias, et la compression `Optimal` coûtera du CPU sans réduire beaucoup.

Extrapolation pour 1 Gio, aux débits observés : **1,6 à 2,5 minutes** de sauvegarde et **45 à
50 secondes** de restauration, hors lecture/écriture du disque source. La sauvegarde crée d'abord
une copie `VACUUM INTO` du catalogue dans `%TEMP%`, puis le ZIP, puis la restauration crée une
copie complète : prévoir **au moins 2 à 3 fois la taille de la bibliothèque** en espace libre.
Cette extrapolation n'a pas été mesurée sur 1 Gio.

### Ouverture, mémoire, handles et verrous

| Métrique | Valeur |
|---|---:|
| Création de la bibliothèque | 150 ms |
| Ouverture à froid (1 260 souvenirs) | 59 ms |
| Ouverture à chaud, médiane | 18,6 ms (14,8–22,4) |
| Ouverture du catalogue à 20 160 | 54 ms (clone) |
| Working set après import | 54,7 Mio |
| Pic pendant l'import | 61,9 Mio |
| `QueryAsync` 1 260 cartes | 40,5 ms, 0,78 Mio alloués, WS +0,03 Mio |
| `QueryAsync` 20 160 cartes | 500 ms, 11,7 Mio alloués, WS +4,7 Mio |
| Handles après 30 requêtes | 288, delta 0 |
| `catalog.db` ouvrable en exclusif après fermeture | oui |
| WAL après fermeture propre | 0 octet |
| `PRAGMA integrity_check` | ok |

Le pic de mémoire reste entre 59 et 62 Mio pour 16, 61 et 64 Mio de corpus, et l'import ne
dépasse pas 62 Mio alors que les médias sur disque en font 61 Mio. **Aucune opération mesurée ne
charge l'ensemble des médias en mémoire** : l'import copie avec un tampon de 128 Kio, les
miniatures décodent une image à la fois, et les requêtes ne retournent que des `MemoryCard`
(métadonnées). Le point de vigilance est le décodage d'une image 1080 × 1920 : un bitmap BGRA
occupe environ 8,3 Mio, ce qui explique le pic de 97 Mio pendant les miniatures.

Le WAL n'a jamais été observé à l'échantillonnage (0 Mio). Ce n'est pas une preuve qu'il n'existe
pas : l'import ouvre et ferme une connexion par opération, et SQLite supprime le WAL à la
fermeture de la dernière connexion. Le journal est donc recréé et checkpointé en permanence, ce
qui fait partie du coût d'import. `journal_mode=wal`, `synchronous=NORMAL` et
`busy_timeout=15000` sont bien appliqués ; aucun conflit de verrou n'a été observé, mais aucun
test concurrent (import + requêtes UI) n'a été exécuté.

## Goulots d'étranglement

### 1. Une connexion SQLite par opération

`MemoCatalog.OpenConnectionAsync` crée une connexion, applique trois PRAGMA, puis la referme.
`Pooling=false` et `Cache=Private`. L'import enchaîne environ sept opérations par fichier
(`EnsureAsset`, `AddSourceFile`, `EnsureMemory`, `LinkMemory`, une ou deux candidates de date,
`UpdateMemoryStatus`), chacune sur une connexion neuve.

Preuves mesurées :

- `SetLocationAsync` : 17,2 ms par écriture pour deux instructions ;
- `GetAssetPath` : 5,85 ms par appel, soit 1,17 s pour 200 chemins ;
- import : 72 ms par fichier source, alors que la copie et l'inspection des en-têtes sont
  largement sous la milliseconde pour ces fichiers.

C'est le premier levier : un import de 2 226 fichiers passe 160 s, dont l'essentiel en
ouvertures/fermetures de connexions et commits WAL.

### 2. Le calcul des dates est refait à chaque requête

`MemoCatalog.Query.cs` construit un CTE `ranked_dates` avec `ROW_NUMBER() OVER (PARTITION BY
memory_id ...)`, puis `effective_date` pour **tous** les souvenirs, avant de filtrer. Ce calcul
est refait pour chaque page, chaque `CountAsync`, chaque résumé et chaque `ThisDay`. Il n'y a pas
de colonne de date effective matérialisée dans `memories`, donc pas d'index d'ordre direct.

`CountAsync` est particulièrement coûteux : il enveloppe la requête de cartes complète
(avec les jointures et les `EXISTS`) dans `SELECT COUNT(*)`, au lieu d'un comptage léger.

### 3. La recherche texte est un balayage complet

`instr(lower(m.external_key), @search)`, `instr(lower(n.text), ...)` et
`instr(lower(ocr.text), ...)` : aucun index, aucun FTS. Le coût dépend du nombre de lignes et de
la taille des notes/OCR. La recherche large passe de 41 ms à 1 260 à 295 ms à 20 000.

### 4. Pagination par OFFSET

`LIMIT @limit OFFSET @offset` avec un `ORDER BY` sur des expressions de date. SQLite doit
produire et trier les lignes précédentes. À 15 000 souvenirs, `OFFSET 5 000` coûte 266 ms contre
79 ms pour la page 500. Une pagination par curseur (`date`, `heure`, `id`) éviterait ce coût.

### 5. Miniatures : décodage pleine résolution et chemins non groupés

Chaque miniature décode l'image entière (1080 × 1920), puis la réduit à 512 px. Le décodage plein
résolution coûte 43 ms et 8,3 Mio de bitmap. `GetAssetPath` ajoute une connexion par image.
Aucune génération en arrière-plan ni parallélisme n'est mesuré ici ; le harnais est séquentiel.

### 6. Sauvegarde : compression de médias déjà compressés

`CreateBackupAsync` utilise `CompressionLevel.Optimal` pour chaque entrée, y compris les JPEG et
MP4. La base et le JSON gagnent à être compressés ; les médias non. Le débit observé est de
6,7 à 10,6 Mio/s, dominé par la lecture, la copie `VACUUM INTO` et la compression.

### 7. Détails et résumés

`GetDetailsAsync` enchaîne quatre connexions. `GetYearSummariesAsync` exécute deux
`EXISTS` corrélés par souvenir. Ces coûts restent modérés à 1 260 (13 à 32 ms) mais deviennent
visibles à 20 000 (84 ms pour les résumés annuels).

## Recommandations

### Priorité 0 — import et requêtes de base

1. **Une connexion et une transaction par archive** (ou par lot de N fichiers) dans
   `ImportService` : réutiliser `SqliteConnection`, préparer les commandes, remplacer
   `INSERT OR IGNORE` + `SELECT id` par `INSERT ... RETURNING id`, grouper les candidates de date
   et le statut. Objectif : passer de ~72 ms à ~10–20 ms par fichier. Non mesuré après
   modification.
2. **Matérialiser la date effective** dans `memories` (`local_date`, `local_time`,
   `date_precision`, `date_source`), mise à jour à l'import, lors d'un override et d'un changement
   de fuseau, avec un index :
   `CREATE INDEX ix_memories_date ON memories(local_date DESC, local_time DESC, id DESC);`
   Réécrire `QueryAsync` sans le CTE de classement. Cela supprime le coût O(n) par page.
3. **Comptage léger** : `SELECT COUNT(*) FROM memories ...` avec seulement les filtres, sans les
   jointures de cartes ni les `EXISTS` de favoris/exclusions quand ils ne sont pas demandés.
4. **Recherche FTS5** sur `external_key`, `memory_notes.text` et `analysis_ocr.text`, avec
   déclencheurs de synchronisation. Garder `instr` pour les termes de moins de trois caractères
   ou comme repli si FTS5 n'est pas disponible.
5. **Pagination par curseur** : `WHERE (local_date, local_time, id) < (@date, @time, @id)` pour
   le défilement, au lieu d'`OFFSET`. Plafonner les pages UI à 100–200 cartes ; le plafond actuel
   de `QueryAsync` est 100 000.
6. **Miniatures décodées à l'échelle** : utiliser `SKCodec.GetScaledDimensions` (ou un décodage
   ciblé) pour éviter le bitmap pleine résolution, générer en arrière-plan avec 2 à 4 workers,
   mettre en cache le chemin et ne jamais bloquer le premier affichage. Regrouper la résolution
   des chemins (`GetAssetPath` en lot ou chemin inclus dans la carte).

### Priorité 1 — détail, découverte, sauvegarde

7. **Fiche détail** : une seule connexion, une transaction, des commandes préparées pour la
   carte, les candidates, l'override, les sources, les collections et la note.
8. **`GetThisDayAsync`** : limiter les cartes retournées (par exemple 200) et calculer les
   compteurs séparément ; mettre en cache le résultat par jour et l'invalider sur écriture.
9. **Résumés annuels** : précalculer les compteurs par année ou utiliser une agrégation
   conditionnelle unique au lieu de deux `EXISTS` par souvenir.
10. **Sauvegarde** : utiliser `CompressionLevel.NoCompression` pour les entrées média et
    `Optimal` pour `catalog.db`, `library.json` et `manifest.json`. Vérifier l'espace libre avant
    de commencer (2–3 fois la taille de la bibliothèque), et documenter le temps estimé.
11. **Limites d'import** : afficher une estimation (fichiers × 70 ms) et un avertissement
    au-delà de 5 000 fichiers ; conserver la reprise et l'annulation. Ne pas imposer de limite
    arbitraire silencieuse : un export volumineux doit rester possible, mais progressif et
    interruptible.

### Priorité 2 — mesure continue

12. Ajouter un test de non-régression de performance dans le harnais (pas dans `dotnet test`,
    qui doit rester rapide) : 1 000 souvenirs, seuils sur première page, page 500, recherche et
    `CountAsync`.
13. Instrumenter l'UI (temps de premier rendu, nombre de miniatures demandées, temps de
    décodage) : les mesures actuelles s'arrêtent à `Memo.Core` et `Memo.Media`.

## Ce qui n'a pas été mesuré

- **Interface WinUI 3** : thread UI, virtualisation XAML, rendu de la grille, animations,
  défilement, premier rendu réel, ouverture d'une Story.
- **Vidéo** : décodage LibVLC, lecture, export MP4, HEVC. Les MP4 synthétiques ne sont pas
  décodables ; aucun MP4 réel n'a été utilisé.
- **Miniatures vidéo** : 2 tentatives seulement, toutes deux en échec attendu.
- **Médias réels** : HEIC, JPEG 4K, WebP réels, vidéos longues, calques réels, EXIF variés.
  Les débits d'import et les tailles de catalogue seront différents.
- **OCR et visages** : `PROGRESS.md` mentionne 5,7 s pour 13 OCR, mais ce n'est pas reproduit ici.
- **Réseau** : aucun accès, aucune bibliothèque distante, aucun partage SMB.
- **Disque lent** : le test utilise un NVMe. Aucun HDD, USB 2, clé lente ou disque réseau.
- **Antivirus / Defender** : non désactivé, non mesuré séparément ; il peut doubler le coût
  d'écriture de milliers de petits fichiers.
- **Concurrence** : aucun import simultané à des requêtes UI, aucun test de verrou SQLite sous
  contention, aucun multi-instance.
- **Mémoire longue durée** : pas de test de fuite sur des heures, pas de fragmentation du
  catalogue, pas de `VACUUM` répété.
- **Au-delà de 20 160 souvenirs** : le test de montée en charge duplique les lignes d'un
  catalogue de 1 260 souvenirs ; les médias ne sont pas dupliqués. Le comportement à 50 000 ou
  100 000 souvenirs reste extrapolé.
- **Sauvegarde de 1 Gio** : extrapolée à partir de 50,6 Mio ; non exécutée.
- **Restauration sur un autre volume** et reprise après coupure : non mesurées.
- **Machines modestes** : un seul profil matériel (i7-13700HX, 32 Go, NVMe).

## Reproduire

```powershell
# Campagne de référence : 1 200 souvenirs, 200 miniatures, catalogue ×8
pwsh -File scripts/perf-check.ps1 -Count 1200 -Root "$env:TEMP\memo-perf"

# Montée en charge à 15 120 ou 20 160 souvenirs (mêmes archives, lignes dupliquées)
pwsh -File scripts/perf-check.ps1 -Count 1200 -Root "$env:TEMP\memo-perf-x12" `
    -CatalogScale 12 -Photos 0
pwsh -File scripts/perf-check.ps1 -Count 1200 -Root "$env:TEMP\memo-perf-x16" `
    -CatalogScale 16 -Photos 0

# Campagne rapide, artefacts conservés pour inspection
pwsh -File scripts/perf-check.ps1 -Count 300 -Root "$env:TEMP\memo-perf-300" `
    -Photos 100 -Iterations 3 -CatalogScale 0 -Keep
```

Le script :

- refuse un `-Root` qui contient le dépôt ou qui est la racine d'un volume ;
- génère le corpus par lots de 500 (limite de `tools/Memo.Corpus`) avec des graines successives ;
- écrit le harnais dans `$Root\harness`, le compile en Release et l'exécute ;
- affiche le tableau complet des mesures et un résumé ;
- supprime `$Root` en cas de succès, sauf avec `-Keep` ; en cas d'échec, conserve les artefacts
  pour diagnostic.

Durée observée : environ 12 à 15 minutes pour 1 200 souvenirs sur cette machine, dont 7 minutes
de génération et 2,5 à 3 minutes d'import. Pour 2 000 souvenirs, compter environ 20 minutes.
Chaque test individuel reste sous 10 minutes ; c'est la campagne complète qui dépasse ce seuil.

Les résultats JSON contiennent l'environnement, chaque métrique avec sa médiane, son min/max,
son nombre de passes et son détail, ainsi que les notes d'échec (miniatures vidéo, erreurs
d'import). Ils sont supprimés avec `$Root` sauf avec `-Keep`.
