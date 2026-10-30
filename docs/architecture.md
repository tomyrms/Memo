# Architecture et règles d'import

Document de conception, pas une description de code déjà implémenté.

## Socle à valider

Piste : interface Windows native C#/.NET + WinUI 3, catalogue SQLite, traitements d'import et de médias isolés de l'interface. La première issue doit valider les versions maintenues, la distribution, les migrations et le moteur de lecture/composition.

Les exemples mentionnant FFmpeg, SkiaSharp, ImageSharp ou libwebp dans une analyse externe sont des pistes, pas des dépendances retenues. La prise en charge HEVC/WebP doit être testée sur une machine Windows propre et les conditions de distribution des composants vérifiées lors du choix.

## Séparation des responsabilités

| Composant | Responsabilité |
|---|---|
| Interface | Imports, grille, Story, rappels, corrections et diagnostics |
| Service d'import | Inventaire progressif, reprise et adaptateurs d'export |
| Catalogue | Sources, souvenirs, fichiers, relations et métadonnées candidates |
| Stockage de médias | Octets importés et empreintes ; chemins internes portables |
| Moteur de médias | Inspection, orientation, décodage, calques et miniatures |
| Redécouverte | Jour anniversaire, sélections, exclusions et historique des suggestions |
| Traitements facultatifs | OCR, catégories et regroupements de visages |

## Modèle proposé

Les noms suivants sont des entités de l'application, pas des champs prétendument exportés par Snapchat.

| Entité | Informations principales |
|---|---|
| `SourceSpace` | Identifiant interne, nom choisi, provenance du rattachement ; séparation des comptes |
| `ImportBatch` | Parties importées, adaptateurs utilisés, état et diagnostic, complétude attendue si démontrable |
| `ArchivePart` | Empreinte, taille, nom source local, état de lecture, position de reprise |
| `Asset` | Empreinte des octets, extension annoncée, format détecté, rôle, dimensions encodées, transformation, dimensions affichées, disponibilité |
| `Memory` | Identifiant interne stable, identité source scoped, relations vers les variantes et état d'association |
| `SourceOccurrence` | Archive et chemin d'origine d'un fichier ou d'une entrée d'index |
| `DateCandidate` | Valeur brute, source, signification, précision, zone/offset, instant éventuel, confiance et conflits |
| `UserDateOverride` | Date choisie, précision, zone éventuelle et historique de correction |
| `LocationCandidate` | Coordonnées, source, précision et éventuelle validation manuelle |
| `Collection` / `Selection` | Albums, récapitulatifs, ordre des souvenirs et règles de génération |
| `UserPreference` | Favoris, exclusions, fuseau du calendrier, lecture et rappels |
| `DerivedArtifact` | Miniature, rendu ou index d'analyse, version du traitement et dépendances |

Le stockage peut mutualiser les mêmes octets sans fusionner les souvenirs logiques. Un même média peut avoir été enregistré deux fois avec des identités, calques ou dates distincts.

## Adaptateurs

### Médias avec identifiant et rôle dans le nom

Un ancien format rapporté contient `memories/<identifiant>-main.jpg`, `-main.mp4` et `-overlay.png`. Cet adaptateur reconnaît ce motif, contrôle les formats binaires et associe les rôles par identifiant dans le bon espace source. Il ne suppose pas que tous les exports utilisent ce motif ni que l'identifiant reste identique dans tous les exports successifs.

Un calque isolé reste conservé. Un média seul est consultable. Plusieurs candidats pour le même rôle restent des variantes à résoudre ; aucun écrasement silencieux.

### Index HTML/JSON et médias locaux ou distants

Identifier les fichiers candidats à partir de leur structure effectivement examinée. Lire HTML et JSON comme des données, sans exécuter les scripts embarqués. Un nombre de liens dans `index.html` ne constitue pas une version de format fiable.

Si l'index référence un média local, tenter l'association avec sa provenance. Si un format validé référence une URL distante, confier le téléchargement à un service séparé : progression, reprise, vérification de contenu et diagnostic de lien expiré. Les domaines, redirections et limites doivent correspondre au format validé ; ne pas suivre arbitrairement des URL issues d'une archive.

Ne jamais journaliser les jetons des URL. L'application ne collecte pas les mots de passe Snapchat. Un nouvel index peut enrichir une bibliothèque existante.

### Format inconnu

Conserver les sources et rendre un diagnostic indiquant les structures non reconnues. Ne pas inventer de métadonnées ni interpréter des historiques de conversations comme des souvenirs. Des archives imbriquées ne sont traitées que par un adaptateur qui les reconnaît dans le périmètre choisi.

## Réimport et rapprochement

1. Reconnaître une archive déjà traitée par son empreinte ; son nom ne suffit pas.
2. Inventorier les nouvelles occurrences et comparer identités/empreintes dans leur espace source.
3. Ajouter les éléments manquants, enrichir les métadonnées et conserver la provenance de chaque valeur.
4. Préserver les identifiants internes auxquels sont attachés albums, favoris et corrections.
5. Conserver les conflits et proposer leur résolution. Ne pas fusionner deux comptes sur un identifiant ou un nom ressemblant.

Une nouvelle archive peut faire passer un souvenir de « calque seul » à « média et calque ». Une nouvelle date peut le faire passer de « date inconnue » à « daté ». Une correction manuelle ne disparaît pas lors de ces enrichissements.

## Lecture sûre et robuste des archives

Traiter les entrées progressivement avec limites de ressources, validation des chemins, refus de sorties du dossier cible et gestion des erreurs par élément. Un arrêt, un disque plein ou une entrée illisible doit permettre un diagnostic et une reprise. L'intégrité structurelle d'un ZIP ne démontre pas la complétude de tout l'export.

Ce point est une exigence de l'importeur de fichiers locaux, pas une demande d'accès supplémentaire à l'utilisateur.

## Orientation et composition

- Distinguer dimensions encodées et affichées ; relever la matrice/rotation d'affichage du média.
- Définir si le décodeur applique déjà la rotation avant d'en appliquer une dans l'application.
- Transformer le média dans l'espace d'affichage puis aligner le calque dans ce même espace.
- Valider les différences de taille et d'aspect sur un corpus synthétique et sur des exemples autorisés ; ne pas généraliser une déformation arbitraire à tous les exports.
- Préserver transparence et ordre de composition. Un calque entièrement transparent reste une donnée source conservée.
- Préférer une composition à l'affichage pour la consultation ; réserver le réencodage aux exports explicitement demandés.

## Validation ciblée

| Cas | Résultat attendu |
|---|---|
| Média dans A, calque dans B, index dans C | Même résultat final quel que soit l'ordre d'import |
| Réimport d'une même archive | Aucune multiplication des entrées ; choix utilisateur conservés |
| Contenu identique sous deux identifiants | Octets éventuellement mutualisés, souvenirs logiques conservés |
| WebP nommé `.png` | Décodage selon le contenu et diagnostic de format cohérent |
| Paysage encodé avec rotation / portrait déjà orienté | Affichage correct sans double rotation |
| Calque d'une autre taille | Composition vérifiée dans l'espace affiché |
| Date UTC près de minuit | Jour de rappel conforme au calendrier choisi |
| Changement d'heure, 29 février, date partielle | Règles déterministes et documentées |
| Aucun index et photo sans EXIF date | Média visible dans « Date inconnue » |
| Correction manuelle puis index supplémentaire | Correction préservée, nouveau candidat enregistré |
| Lien expiré ou média indisponible | Diagnostic par élément ; bibliothèque existante utilisable |
| ZIP interrompu, chemin invalide, disque plein | Sources protégées et reprise maîtrisée |

## Sources de cadrage

- Demandes et corrections du propriétaire du projet : plusieurs ZIP, Windows, consultation locale, Flashbacks et autres fonctions de redécouverte.
- Deux versions d'une analyse externe du même ZIP et un inventaire CSV fournis le 30 octobre 2026. Ils ne remplacent pas la validation binaire des médias.
- [Export officiel Snapchat](https://help.snapchat.com/hc/en-us/articles/7012305371156-How-do-I-download-my-data-from-Snapchat) : procédure ZIP/index puis récupération des Souvenirs, consultée pendant le cadrage.
