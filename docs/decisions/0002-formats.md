# Décision 0002 — Formats activés et formats refusés

Date : 30 octobre 2026. Statut : acceptée pour le corpus et le contrat d'import.
Elle ne vaut pas validation d'un export réel complet.

## Contexte

Le seul format de souvenirs décrit par les pièces de cadrage est un ancien
export avec `memories/<uuid>-main.jpg|mp4` et `-overlay.png`. Les signatures
binaires n'ont pas été revérifiées sur les médias ; l'index HTML fourni ne
présentait pas de rubrique Souvenirs. Aucun index contemporain, aucun lien
distant et aucune variante multi-compte n'ont été examinés.

Il faut néanmoins commencer l'import, les associations et les tests sans
inventer un format ni promettre une compatibilité non démontrée.

## Décision

### Formats activés

| Élément | Décision | Justification |
|---|---|---|
| Découverte `memories/<uuid>-main.<ext>` | Activée | Motif observé, UUID validé dans l'espace source |
| Découverte `memories/<uuid>-overlay.<ext>` | Activée | Rôle observé, calque conservé même seul |
| JPEG | Activé par signature | Format photo observé |
| PNG | Activé par signature | Format calque observé |
| WebP | Activé par signature | WebP sous `.png` rapporté ; SkiaSharp le décode |
| MP4 ISO-BMFF | Activé comme conteneur | `creation_time`, dimensions et rotation lisibles |
| H.264 / HEVC | Acceptés comme contenu, lecture non garantie | Le fichier source reste conservé même si le codec n'est pas lisible |
| Calque RGBA | Activé | Transparence et tailles variables font partie du format observé |
| Plusieurs ZIP et dossiers extraits | Activés | Un export peut être réparti ; l'ordre d'import ne doit pas compter |
| Index HTML/JSON synthétique | Lu comme donnée de test uniquement | Aucun format réel validé |
| Réimport et doublons d'octets | Activés | Empreintes et occurrences, sans fusionner les souvenirs |

Le format est déterminé par signature binaire. L'extension annoncée est
conservée comme information et comme source de diagnostic, jamais comme preuve.

### Formats et comportements refusés

| Élément | Décision | Raison |
|---|---|---|
| Téléchargement d'URL trouvées dans un index | Refusé / inactif | Domaines, jetons, expiration et vérification non validés |
| Index HTML/JSON réel comme source d'autorité | Refusé tant que non validé | Risque d'inventer une structure et de prioriser une fausse date |
| Exécution de HTML/JavaScript d'une archive | Refusé | Contenu non fiable |
| Extraction avec `..`, chemin absolu, UNC ou lettre de lecteur | Refusé | Évasion de la racine et écrasement de fichiers |
| Archives chiffrées | Refusées avec diagnostic | Aucun cas réel ni support validé |
| Archives imbriquées ouvertes automatiquement | Refusées | Périmètre et profondeur non définis |
| Médias de conversations | Hors périmètre par défaut | Le produit cible les Souvenirs |
| Écrasement d'un rôle en conflit | Refusé | Les deux candidats restent des variantes |
| Remplacement d'une date de souvenir par la date du ZIP | Refusé | L'horodatage du ZIP est celui de l'archive |
| Fusion de deux comptes sur un UUID ou un nom | Refusé | L'identifiant n'est valable que dans son espace source |

Les entrées inconnues ne font pas échouer toute l'archive : elles produisent un
diagnostic et les entrées reconnues sont importées. Une archive corrompue est
signalée ; la bibliothèque déjà importée reste utilisable.

### Conséquences

1. L'application peut importer les médias disponibles sans index.
2. Une photo sans EXIF et un calque orphelin restent visibles, respectivement
   dans « date inconnue » et « à compléter ».
3. Un WebP nommé `.png` est décodé selon son contenu et produit un diagnostic
   de format cohérent.
4. Les conflits de rôle et les UUID présents dans deux espaces ne sont pas
   fusionnés silencieusement.
5. L'adaptateur de liens distants (`SD-010`) reste inactif jusqu'à nouvel
   examen.
6. Le corpus `tools/Memo.Corpus` et `tests/Memo.TestSupport` constituent la
   référence exécutable de cette décision.

## Conditions de réexamen

La décision sera revue lorsqu'un export réel supplémentaire sera disponible,
en particulier avec un index contemporain. Le réexamen devra vérifier :

1. La structure exacte de l'index HTML/JSON, ses champs de date, d'identifiant
   et de rôle, ainsi que ses liens éventuels.
2. Le nommage réel des médias, la casse, les sous-dossiers et la stabilité des
   UUID entre exports successifs.
3. La proportion réelle de WebP sous `.png`, les codecs vidéo et la présence
   d'EXIF.
4. La matrice de rotation, les dimensions encodées/affichées et le comportement
   du décodeur, sans double rotation.
5. Les règles de téléchargement distant : domaines autorisés, redirections,
   expiration, reprise, vérification du contenu et non-journalisation des
   jetons.
6. Les cas multi-comptes, les archives imbriquées, les archives chiffrées et
   les limites de ressources à grande échelle.

Tant que ces points ne sont pas observés sur un exemple autorisé, ils restent
« non validés » et ne doivent pas être présentés comme pris en charge.
