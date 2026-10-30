# Spécification produit

Date du cadrage : 30 octobre 2026. Destination : application Windows locale. Langue initiale : français.

## Objectif

Transformer un ensemble d'exports Snapchat en une bibliothèque pérenne et agréable à consulter, puis faire redécouvrir les souvenirs avec des rappels de dates, des Stories et des sélections modifiables.

Le projet porte d'abord sur les Souvenirs. Les médias des conversations constituent une source différente ; ils ne rejoignent pas automatiquement la bibliothèque.

## Écrans principaux

### À redécouvrir

Accueil composé de sections alimentées par les données disponibles : « Ce jour-là », « Il y a un an », autres années, récapitulatifs et « Une journée à redécouvrir ». Les sections vides ne simulent pas de contenu. Un rappel se lance en lecture Story.

« Surprends-moi » propose une ancienne journée ou une sélection courte, diversifie les périodes et évite de répéter immédiatement les éléments récemment proposés. Cette sélection respecte les exclusions. Le comportement des médias non datés est distinct des rappels anniversaires.

### Bibliothèque

Grille de photos et vidéos avec navigation année/mois/jour, filtre de type, favoris et sélection multiple. Une vue « Date inconnue » garde accessibles les médias dépourvus de date exploitable. Les miniatures se construisent en arrière-plan et la grille reste utilisable pendant l'import.

### Albums

Création, renommage, ordre manuel, ajout et retrait de souvenirs sans déplacement des sources. Un album peut contenir des médias non datés. Les favoris et albums appartiennent à la bibliothèque locale, même si aucun équivalent n'est exporté par Snapchat.

### Imports

Sélection de plusieurs ZIP, d'un dossier contenant des ZIP ou d'un export extrait. Affichage des archives inspectées, des fichiers traités, des associations et des éléments restant à compléter. La progression est annulable et reprenable.

Le message pour un calque sans base est « Média non trouvé dans les archives importées ». L'application ne déclare pas les souvenirs perdus. L'absence d'un manifeste complet ne permet pas de connaître le nombre total de parties attendues.

### Réglages

Emplacement de bibliothèque, sauvegarde/restauration, fuseau de classement, préférences de lecture, rappels facultatifs, exclusions et analyses locales facultatives. Le fuseau choisi reste stable lorsqu'on consulte la même bibliothèque après un voyage ; toute modification provoque un recalcul explicite des regroupements.

## Parcours d'import

1. Créer ou choisir un espace source pour les archives d'un même compte/export. Un nom convivial suffit si aucun identifiant de compte exploitable n'est disponible.
2. Inventorier toutes les parties sélectionnées, détecter les formats et présenter le périmètre.
3. Copier les médias sources et enregistrer leur provenance, en traitant les archives progressivement.
4. Associer bases, calques et métadonnées à travers les parties ; garder les variantes ambiguës.
5. Construire les dates candidates, orientations et miniatures.
6. Présenter les résultats utilisables et les données encore absentes.
7. Lors d'un import ultérieur, compléter ou proposer la résolution des conflits sans effacer les choix de l'utilisateur.

La consultation des fichiers déjà copiés doit continuer après déplacement ou retrait des ZIP d'origine.

## Dates et « Ce jour-là »

- Conserver plusieurs dates candidates avec valeur brute, source, signification connue ou supposée, précision et zone/offset éventuels.
- Une correction explicite de l'utilisateur prévaut pour la présentation, tout en conservant les valeurs importées.
- Une date fournie par un index n'est prioritaire que si son champ et sa signification ont été validés. Une date EXIF ou de conteneur ne doit pas être renommée automatiquement « prise de vue ».
- Une date de conteneur antérieure à l'export peut servir de date de classement provisoire, avec provenance consultable et politique d'inclusion explicite dans les rappels.
- Un souvenir sans date reste visible. Une date connue seulement au mois ou à l'année ne déclenche pas un anniversaire quotidien exact.
- Pour une date avec instant UTC, calculer le jour selon le fuseau de bibliothèque choisi ; ne pas utiliser silencieusement le dernier fuseau connu du compte comme preuve du lieu de capture.
- Pour une date locale sans offset, conserver cette information et ne pas inventer un instant UTC.
- Le 29 février doit avoir une règle visible : en première version, rappel exact les 29 février ; une option pourra proposer le 28 février les années non bissextiles.

« Ce jour-là » regroupe les souvenirs au même jour et mois, par années antérieures. La politique d'inclusion des dates supposées est paramétrable. Les exclusions s'appliquent avant la sélection et avant toute notification.

## Lecture et apparence

Lecture plein écran avec progression, pause, navigation, son et raccourcis clavier. La durée d'affichage des photos est réglable. L'application propose l'apparence composée avec calque et l'affichage du média seul lorsque les deux fichiers existent.

L'absence de calque n'affirme pas l'absence de texte déjà intégré au média. Les dimensions encodées, l'orientation et les dimensions affichées sont trois informations distinctes. Le lecteur et le compositeur doivent se partager clairement la responsabilité de la rotation.

Les fichiers porteurs d'une même date ne sont pas automatiquement collés en une vidéo. Un regroupement de segments doit disposer d'un ordre démontré ou validé par l'utilisateur.

## Récapitulatifs et collections suggérées

Proposer des sélections annuelles, mensuelles ou saisonnières équilibrées dans la période, en tenant compte des favoris, des répétitions et des exclusions. L'utilisateur peut ajouter, retirer et réordonner les éléments, puis conserver la sélection. Une faible couverture de dates doit rester visible dans les informations de la sélection.

Les événements sont des suggestions modifiables à partir de périodes proches. La carte utilise les coordonnées directement liées au souvenir ou saisies par l'utilisateur. Un historique général de localisation ou de connexion n'est pas une localisation certaine de chaque média.

## Rappels et contrôle

Notifications Windows facultatives, heure choisie, désactivation simple et absence de répétitions excessives. Le comportement fenêtre fermée doit être documenté et validé : démarrage avec Windows et tâche de fond ne sont pas activés silencieusement.

Masquer un souvenir ou une période des suggestions conserve les sources et les albums. Les notifications peuvent être discrètes, sans miniature ni nom de personne.

## Sauvegarde et export

Une sauvegarde contient catalogue, fichiers sources, dates/corrections, albums, favoris, exclusions et paramètres nécessaires à la restauration. Les caches reconstructibles peuvent être exclus. La restauration sur un autre PC ne doit pas dépendre des anciens chemins absolus.

L'export d'une sélection et l'export vidéo d'une Story génèrent de nouveaux fichiers. Ils ne remplacent jamais les sources. Les métadonnées disponibles et leur provenance peuvent être conservées dans un fichier associé quand le format de sortie ne les transporte pas correctement.

## Fonctions avancées facultatives

OCR, catégories visuelles et groupes de visages sont calculés localement après activation. Les noms de personnes viennent de l'utilisateur. Les résultats peuvent être corrigés, exclus et supprimés sans effacer les souvenirs. La suppression d'un index dérivé doit aussi retirer les informations correspondantes des suggestions.

## Critère de réussite du premier jalon

Un utilisateur importe plusieurs ZIP dans un ordre quelconque, retrouve les médias disponibles avec orientation et calques corrects, consulte les éléments non datés, corrige une date, ajoute une archive manquante sans doublon ni perte de personnalisation, lit « Ce jour-là » et restaure sa bibliothèque sur une installation Windows propre.
