# Analyse critique de l'échantillon fourni

## Périmètre et preuve

Deux textes fournis décrivent la même analyse externe d'un ancien ZIP Snapchat. Le troisième fichier utile est un inventaire CSV à séparateur point-virgule. Il ne s'agit pas de trois audits indépendants.

Le ZIP et ses médias n'ont pas été fournis dans cet environnement. Les comptages ci-dessous ont été recalculés depuis le CSV. Les observations binaires restent celles du rapport externe. Le fichier `index.html` transmis séparément a bien été lu : sa navigation ne présente pas de rubrique Souvenirs.

**L'échantillon porte sur un seul ZIP.** Les résultats ne permettent pas de déclarer l'export entier incomplet, ni de conclure qu'une date ou un média est définitivement perdu. Le produit doit prendre en charge plusieurs archives d'un même export.

Aucun inventaire contenant des identifiants personnels, aucun historique de compte et aucun média privé n'est inclus dans ce dossier de projet. Seuls des résultats agrégés et des contraintes génériques sont conservés ici.

## Résultats recalculés depuis l'inventaire

| Mesure | Résultat |
|---|---:|
| Lignes de fichiers | 825 |
| Identifiants distincts | 816 |
| Médias de base | 531 |
| Vidéos listées | 354 |
| Photos listées | 177 |
| Calques listés | 294 |
| Identifiants avec média et calque dans cet inventaire | 9, dont 8 vidéos et 1 photo |
| Médias sans calque associé dans cet inventaire | 522 |
| Calques sans média associé dans cet inventaire | 285 |
| Taille totale listée | 1 722 383 330 octets, soit 1,722 Go ou 1,604 Gio |
| Vidéos avec une valeur de date | 354 sur 354 |
| Photos avec une date EXIF renseignée | 0 sur 177 |
| Rotation indiquée dans le CSV | 339 vidéos à 90°, 15 à 0° |

Ces nombres sont des propriétés du document transmis. Ils ne valident pas les signatures binaires, les empreintes ni la présence réelle de chaque fichier dans l'archive source.

## Observations rapportées à valider sur les médias

Le rapport externe indique notamment 214 calques au format WebP malgré une extension `.png`, 80 PNG, des vidéos H.264 et HEVC, des calques de dimensions différentes du média, ainsi que certains contenus strictement identiques sous des identifiants différents.

Ces observations justifient des cas de validation et une architecture capable de les traiter. Le CSV ne comporte ni signature binaire ni codec ni empreinte ; il ne permet donc pas de les confirmer indépendamment. Sa colonne `type` vaut `png` pour tous les calques : elle ressemble à une extension annoncée, pas à un format détecté.

## Corrections à apporter aux conclusions du rapport

### Dimensions et rotation

Toutes les vidéos du CSV ont déjà des dimensions portrait, y compris celles indiquées à 90°. Le rapport décrit pourtant des dimensions encodées paysage avant rotation. Les colonnes ne précisent pas leur espace de référence.

Il faut donc séparer dimensions encodées, transformation et dimensions affichées dans l'application. Réutiliser les colonnes telles quelles avec une rotation supplémentaire risquerait une double transformation.

### Flashbacks et fuseaux

Le rapport annonce 198 couples mois/jour et 72 présents sur plusieurs années. Ces valeurs correspondent au calcul UTC, malgré une recommandation de classement selon le calendrier suisse.

| Calendrier appliqué aux valeurs datées du CSV | Jours distincts | Jours présents dans plusieurs années |
|---|---:|---:|
| UTC | 198 | 72 |
| Europe/Zurich | 200 | 75 |

40 vidéos changent de jour dans cette conversion. Le fuseau est donc une règle produit, pas une simple option de formatage. Cette conversion ne prouve pas que les souvenirs ont été capturés dans ce fuseau.

### Fiabilité des dates

Le nom de colonne `date_capture_mp4` ne prouve pas que la valeur représente la prise de vue. Le rapport indique comme source `creation_time`, une donnée du conteneur. Conserver valeur, origine et signification probable sans les transformer en certitude.

Une date absente du média peut apparaître dans un index d'une autre archive. L'horodatage ZIP observé correspond à la génération de cette archive et ne doit pas remplacer la date du souvenir.

### Absence de calque et qualité de l'image

Un média sans calque associé n'est pas nécessairement un média sans édition. Certains effets peuvent être intégrés, ou un calque peut se trouver ailleurs.

Une faible résolution ou une petite taille ne démontrent pas à elles seules une recompression par Snapchat. Le produit doit parler de « fichier source exporté », et ne pas promettre un original photographique qui n'a pas été vérifié.

### Identifiants et séquences

Les UUID listés sont syntaxiquement des UUID v4. Leur proximité lexicographique n'apporte pas un ordre chronologique. Des vidéos partageant un horodatage ne doivent pas être fusionnées automatiquement ; un éventuel découpage doit être vérifié et ordonné par d'autres informations ou par l'utilisateur.

### Données de compte

Le profil, les abonnements et l'historique général de localisation ne sont pas des dépendances obligatoires de l'importeur. Un dernier fuseau connu au niveau du compte n'est pas un fuseau de capture attesté pour tous les médias. Un point GPS voisin dans le temps n'est pas une preuve du lieu exact d'un souvenir.

## Conséquence pour le projet

Le développement peut commencer avec des exemples synthétiques et l'adaptateur de l'ancien format rapporté. Obtenir d'autres archives ou un index contemporain enrichira les cas de validation ; ce n'est pas un préalable à la création du projet.

L'application doit fonctionner avec ce qui a été importé, expliquer les données encore inconnues et enrichir la même bibliothèque lorsque de nouvelles parties arrivent.
