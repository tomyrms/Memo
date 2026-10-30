# Décision 0003 — Index de liens : contrat préparé, format Snapchat non validé

Date : 30 octobre 2026. Statut : acceptée pour le contrat interne et les tests.
Elle ne vaut pas validation d'un index Snapchat réel et n'active aucun téléchargement.

## Contexte

L'issue #10 (`SD-010`) demande de télécharger les médias référencés par un index de liens.
L'architecture et la décision 0002 posent déjà la limite : un index HTML/JSON réel n'est pas un
format validé, et aucun téléchargement distant ne doit être activé tant que la structure, les
domaines, les redirections, l'expiration et les jetons n'ont pas été observés sur un exemple
autorisé.

Aucun exemple de ce type n'est disponible. L'`index.html` fourni pendant le cadrage ne présentait
pas de rubrique Souvenirs, aucun index contemporain complet n'a été examiné, et aucune URL signée
réelle n'a été observée. Inventer un schéma maintenant produirait un adaptateur qui reconnaîtrait
peut-être un fichier sans rapport, ou qui téléchargerait des URL arbitraires.

## Décision

### 1. Aucun format d'index Snapchat n'est validé

Aucun nom de fichier, champ JSON, balise HTML, domaine, paramètre de requête, en-tête ou code de
réponse n'est déclaré comme faisant partie d'un index Snapchat. Le contrat décrit ci-dessous est
une **forme d'accueil** pour un futur adaptateur, pas une description d'un export existant.

### 2. Aucun adaptateur n'est activé par défaut

`LinkIndexAdapterRegistry.Empty` ne contient aucun adaptateur. Un adaptateur dont
`ILinkIndexAdapter.IsValidated` vaut `false` ne peut être enregistré qu'avec l'option explicite
`allowUnvalidatedAdapters: true`, réservée aux tests et aux serveurs locaux. La détection exige un
marqueur explicite propre au format ; une simple ressemblance de champs, de noms de fichiers ou de
structure JSON ne suffit pas.

### 3. Aucun téléchargement automatique dans l'import

`LinkDownloadService` n'est branché ni sur `ImportService`, ni sur `MemoLibrary`, ni sur un parcours
de l'interface. Aucun import existant ne contacte le réseau. Le service est opt-in : il faut
construire explicitement une politique de domaines, un adaptateur validé et une entrée d'index.

### 4. Le format `memo-index-v1` est un fixture de test, pas Snapchat

`MemoIndexV1Adapter` lit un JSON synthétique documenté uniquement pour exercer le contrat et le
serveur HTTP local. Il est marqué `IsValidated == false`, n'est enregistré par aucun chemin de
production et ne doit jamais être présenté comme un format Snapchat. L'implémentation est interne
à `Memo.Core` et n'est accessible aux tests que par `InternalsVisibleTo` ; aucun code de production
ne l'instancie.

Forme du fixture :

```json
{
  "format": "memo-index-v1",
  "schemaVersion": 1,
  "entries": [
    {
      "id": "synthetic-001",
      "date": "2021-07-14T18:30:00Z",
      "type": "photo",
      "url": "https://media.example.test/synthetic/001.jpg",
      "size": 12345,
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
    }
  ]
}
```

Les champs `id`, `type` et `url` sont obligatoires. `date`, `size` et `sha256` sont facultatifs mais
validés s'ils sont présents. Un `type` autre que `photo` ou `video`, une URL non HTTPS, un domaine
hors liste, un fragment, des identifiants dans l'URL, une taille incohérente ou une empreinte
malformée produisent un diagnostic et l'entrée est écartée. Le marqueur `format` et
`schemaVersion: 1` sont comparés exactement.

### 5. Contrat interne

| Type | Rôle |
|---|---|
| `ILinkIndexAdapter` | `Id`, `IsValidated`, `Detect(ReadOnlySpan<byte>)`, `Read(...)` |
| `LinkIndexEntry` | `Id`, `Date`, `Type`, `Url`, `ExpectedSize`, `ExpectedSha256` |
| `LinkIndexResult` | adaptateur, reconnaissance, entrées retenues, diagnostics |
| `LinkIndexDiagnostic` | gravité, code stable, message, identifiant d'entrée, domaine seulement |
| `LinkIndexAdapterRegistry` | registre vide par défaut, opt-in explicite, refus des formats ambigus |
| `LinkDownloadService` | téléchargement opt-in, progression, reprise, limites, SHA-256, diagnostic |

`LinkIndexEntry.Url` est exclue de la sérialisation JSON et `ToString()` n'affiche qu'une URL
expurgée (`https://domaine/<redacted>`). Les diagnostics ne contiennent jamais l'URL complète, la
query string, le fragment ou un jeton.

### 6. Règles d'URL et de redirection

- HTTPS uniquement. HTTP n'est accepté que sur une adresse de boucle locale lorsque
  `LinkUrlPolicy.AllowInsecureLoopback` est explicitement activé pour le serveur de test.
- Domaine autorisé par correspondance exacte, insensible à la casse. Aucun sous-domaine ni
  caractère générique implicite.
- Refus des URL avec `userinfo`, fragment, schéma inconnu ou domaine absent.
- Redirections suivies manuellement, uniquement vers la même origine (schéma, hôte, port) et
  après la même validation de domaine, avec un nombre maximal de sauts. Une destination hors de
  l'origine ou hors domaine produit `link.redirect-refused`.
- Aucune URL d'index n'est suivie par défaut : le service n'est appelé que sur une entrée déjà
  lue et validée.

### 7. Téléchargement et stockage

- Écriture dans un fichier partiel sous `imports/download-<empreinte>.part`. Le nom du partiel est
  dérivé de l'identifiant et de l'URL complète, mais seule l'empreinte SHA-256 apparaît.
- Reprise par `Range` si un partiel existe. Si le serveur ignore `Range` et renvoie `200`, le
  partiel est tronqué et le téléchargement repart de zéro.
- Limite de taille vérifiée sur la taille déclarée, sur `Content-Length` et pendant la lecture.
- Vérification SHA-256 lorsque l'index en fournit une ; une différence produit
  `link.hash-mismatch` et le partiel est supprimé.
- Détection binaire du format après téléchargement. Un contenu non reconnu n'est pas conservé.
- Écriture finale dans `media/<deux premiers caractères>/<sha256>.<extension>` ; un fichier déjà
  présent est réutilisé sans écrasement.
- Diagnostics distincts : `link.expired` (404/410), `link.forbidden` (401/403),
  `link.server-error` (5xx), `link.network-error` (réseau, coupure, timeout),
  `link.size-exceeded`, `link.hash-mismatch`, `link.redirect-refused`, `link.storage-error`,
  `link.url-*`.

## Pourquoi #10 ne peut pas être déclarée terminée

Le critère « aucun schéma HTML ou JSON supposé actuel n'est activé sans exemple vérifié » est
respecté par l'inactivité, mais la fonctionnalité elle-même n'est pas démontrée sur un index réel.
Sans exemple Snapchat autorisé, il est impossible de prouver :

- que le marqueur et les champs du fixture correspondent à un export réel ;
- que les domaines et redirections observés sont ceux du service ;
- que l'expiration et les jetons se comportent comme supposé ;
- qu'un nouvel index permet réellement de reprendre les liens devenus inutilisables ;
- que les dates et identifiants de l'index ont la signification qu'on leur prête.

L'issue reste donc ouverte. Le contrat, le fixture et le service sont une préparation vérifiable,
pas une prise en charge d'un format.

## Conditions de réexamen

Le réexamen exigera un exemple réel autorisé, anonymisé et fourni par le propriétaire du projet,
sans média personnel dans le dépôt. Il devra établir :

1. **Structure** : nom et emplacement de l'index, format HTML ou JSON, champs exacts, types,
   valeurs obligatoires, casse et version.
2. **Domaine** : liste exacte des hôtes de médias, sous-domaines éventuels, CDN, ports et schéma.
   La liste devient une allowlist explicite, jamais un motif large.
3. **Redirections** : codes observés, nombre de sauts, domaines de destination, paramètres
   conservés ou remplacés.
4. **Expiration** : durée de vie, code de réponse observé (401, 403, 404 ou 410), comportement
   d'un nouvel index et règles de reprise.
5. **Jetons** : noms et emplacement des paramètres signés, présence éventuelle dans le chemin,
   règles de non-journalisation et de non-persistance. Aucun jeton réel ne doit être copié dans
   un test, un rapport ou un journal.
6. **Intégrité** : présence de `Content-Length`, `Content-Type`, taille attendue, SHA-256 ou autre
   empreinte, support de `Range`.
7. **Identités et dates** : stabilité des identifiants entre exports, doublons, fuseaux et
   signification réelle des dates.
8. **Cadre** : export obtenu par l'utilisateur pour ses propres données, sans collecte
   d'identifiants Snapchat et sans contournement des conditions du service.

Tant que ces points ne sont pas observés, l'adaptateur reste absent du registre par défaut, le
service reste non branché et aucun téléchargement n'est effectué.

## Conséquences

1. L'import local de ZIP et de dossiers extraits reste le seul parcours actif.
2. Un index inconnu produit `index.no-adapter` ou `index.unknown-shape`, sans interprétation.
3. Un futur format validé pourra être ajouté en implémentant `ILinkIndexAdapter` avec
   `IsValidated == true` et une allowlist issue de l'observation.
4. Les tests de ce contrat utilisent uniquement un serveur HTTP local et des octets synthétiques.

## Validation

```powershell
dotnet build src/Memo.Core
dotnet test tests/Memo.Core.Tests
```

Les tests `LinkIndexTests` et `LinkDownloadTests` couvrent la détection par marqueur, le refus d'un
schéma inconnu, le refus d'une URL HTTP, le refus d'un domaine hors allowlist, le refus d'une
redirection hors domaine, le téléchargement local réussi, la reprise après coupure, l'erreur 404,
la taille dépassée, l'empreinte incorrecte et l'absence de jeton dans les diagnostics.
