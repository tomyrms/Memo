# Checklist de recette manuelle — Memo 0.1.0

Document de recette pour une build `feat/qa` ou une release candidate.

À utiliser avec `docs/qa-report.md`, qui contient les défauts connus et les tests non exécutés.

## Préparation

- [ ] Utiliser une VM Windows 11 x64 jetable, sans données personnelles, ou un compte Windows de test.
- [ ] Noter le commit testé : `git rev-parse --short HEAD`.
- [ ] Vérifier que la build est celle du worktree attendu (`feat/qa`), pas une build d'un autre worktree.
- [ ] Générer le corpus synthétique :

```powershell
dotnet run --project tools/Memo.Corpus -c Debug --no-build -- `
  --out <Temp>\memo-corpus20 --seed 42 --count 20
dotnet run --project tools/Memo.Corpus -c Debug --no-build -- `
  --out <Temp>\memo-corpus40 --seed 42 --count 40
```

- [ ] Considérer `memo-conflit.zip`, `memo-corrompu.zip` et `memo-autre-espace.zip` comme des fixtures de test, pas comme des exports normaux à importer tous dans le même espace.
- [ ] Vérifier qu'aucun média personnel, aucune URL signée et aucun identifiant de compte ne sont utilisés dans les captures ou les rapports.

Légende : `[ ]` non exécuté, `[x]` réussi, `[~]` bloqué ou défaut constaté, `[!]` régression critique.

## 1. Import via l'interface

### 1.1 Création de bibliothèque

- [ ] `Réglages → Bibliothèque → Créer`, choisir un dossier vide.
- [ ] Vérifier la création de `catalog.db`, `library.json`, `media/`, `derived/thumbs/`, `imports/`.
- [ ] Rouvrir la bibliothèque après redémarrage : elle est retrouvée.
- [ ] Tenter de créer une bibliothèque dans un dossier non vide : refus explicite, aucun fichier écrasé.
- [ ] Tenter d'ouvrir un dossier sans `library.json` : message explicite, pas de crash.
- [ ] Tenter d'ouvrir un dossier avec `library.json` mais sans `catalog.db` : message explicite.

### 1.2 Import multi-ZIP et ordre

- [ ] Créer un espace « Démo A ».
- [ ] Importer `memo-media-1.zip` puis `memo-media-2.zip` puis `memo-overlays.zip`.
- [ ] Vérifier : 22 cartes (20 souvenirs + 2 calques orphelins), 18 calques, 0 conflit, 15 sans date pour le corpus 20.
- [ ] Recommencer dans une bibliothèque neuve avec l'ordre inverse (`overlays` d'abord).
- [ ] Comparer les dates, statuts, médias principaux et calques : identiques.
- [ ] Importer `memo-conflit.zip` après `memo-media-2.zip` : carte en conflit, deux variantes conservées.
- [ ] Refaire l'ordre inverse : vérifier si la variante principale change (défaut QA-01 attendu).
- [ ] Vérifier que l'interface permet de choisir la variante principale ; si non, noter le blocage.

### 1.3 Réimport et personnalisation

- [ ] Mettre un souvenir en favori, l'ajouter à un album, le masquer, corriger sa date et son lieu.
- [ ] Réimporter exactement le même lot.
- [ ] Vérifier : aucun doublon, aucun nouveau souvenir, favoris/album/exclusion/date/lieu conservés.
- [ ] Vérifier le bilan : « 0 créé », doublons signalés, pas de « mis à jour » trompeur sur un import neuf (QA-09).

### 1.4 Espaces sources

- [ ] Créer « Démo B ».
- [ ] Importer `memo-autre-espace.zip` dans B.
- [ ] Vérifier que le même UUID apparaît dans A et B, avec des octets différents et sans fusion.
- [ ] Importer `memo-media-1.zip` dans B : B contient ses propres souvenirs, A reste inchangé.
- [ ] Vérifier la mutualisation des octets identiques entre espaces (même asset, cartes distinctes).
- [ ] Vérifier si un filtre d'espace existe dans la bibliothèque ; sinon noter QA-31.

### 1.5 Progression, annulation, reprise

- [ ] Importer une archive volumineuse (idéalement > 1 Go, sur VM isolée).
- [ ] Vérifier la phase d'inventaire, la barre de progression et le compteur `fichiers traités / total`.
- [ ] Annuler pendant la copie ; vérifier que la bibliothèque reste utilisable et que les éléments déjà copiés sont conservés.
- [ ] Relancer l'import : pas de doublon, reprise maîtrisée.
- [ ] Fermer l'application pendant un import ; rouvrir et relancer : comportement documenté et compréhensible.

### 1.6 Erreurs d'import

- [ ] Importer un ZIP corrompu (`memo-corrompu.zip`) : diagnostic clair, lot en échec, bibliothèque existante intacte.
- [ ] Importer un ZIP contenant `../hors-export.jpg` (`memo-conflit.zip`) : entrée refusée, aucun fichier écrit hors de la bibliothèque.
- [ ] Importer un ZIP vide : message attendu (actuellement silence, QA-17).
- [ ] Importer un dossier vide : message attendu (actuellement silence, QA-17).
- [ ] Importer un chemin inexistant : message `path.missing` lisible.
- [ ] Importer un fichier verrouillé : message clair, pas de crash.
- [ ] Vérifier le bilan : codes de diagnostic compréhensibles, distinction sécurité/lecture, compteurs exacts (QA-22).

## 2. Export

### 2.1 Export d'une Story en MP4

- [ ] Ouvrir une Story de plusieurs photos et vidéos.
- [ ] Cliquer « Exporter », choisir un fichier `.mp4`.
- [ ] Vérifier la progression, le pourcentage et l'item courant.
- [ ] Vérifier le fichier produit : taille > 0, durée cohérente, lecture dans Windows Media Player/VLC.
- [ ] Vérifier l'audio quand « Activer le son » est actif, et le silence quand il est désactivé.
- [ ] Vérifier les calques : position, ratio, transparence ; comparer avec la Story à l'écran.
- [ ] Vérifier les photos avec EXIF orientation et les vidéos portrait/paysage : pas de double rotation.
- [ ] Tester une vidéo HEVC sur une installation sans extension HEVC : message d'erreur explicite, pas de fichier final vide.
- [ ] Tester un fichier de sortie existant : refus ou confirmation explicite, pas d'écrasement silencieux.
- [ ] Tester un disque plein ou un dossier non inscriptible : message clair, fichier temporaire nettoyé.
- [ ] Annuler l'export : aucun fichier final conservé.
- [ ] Vérifier que les sources ne sont jamais modifiées.

### 2.2 Export d'une sélection

- [ ] Vérifier si une action « Exporter la sélection » existe dans la bibliothèque.
- [ ] Si absente : noter QA-19 (fonction présente dans `Memo.Core`, non branchée).
- [ ] Si présente : exporter plusieurs souvenirs avec et sans calques, vérifier `metadata.json`, les noms de fichiers, les SHA et les dates.

## 3. Rappels Windows

- [ ] Activer « Rappel quotidien » à une heure proche, avec une bibliothèque ouverte.
- [ ] Vérifier la création de la tâche `Memo.Reminder.<guid>` dans le Planificateur de tâches.
- [ ] Vérifier que la tâche est visible, non élevée, et pointe vers le bon `Memo.exe` et la bonne bibliothèque.
- [ ] Attendre l'heure : un toast doit apparaître.
- [ ] Vérifier le mode discret : pas de miniature, pas de nom de personne.
- [ ] Vérifier que les souvenirs masqués n'apparaissent pas dans la notification.
- [ ] Vérifier qu'une journée déjà notifiée ne produit pas de doublon.
- [ ] Vérifier le 29 février : comportement exact le jour bissextile ; documenter le 28 février les autres années.
- [ ] Fermer la fenêtre Memo : la tâche doit fonctionner ou le comportement doit être clairement documenté.
- [ ] Changer l'heure du rappel : l'ancienne tâche est remplacée, la nouvelle heure est appliquée.
- [ ] Désactiver le rappel : la tâche est supprimée ; réactiver : une seule tâche.
- [ ] Tester avec les notifications Windows désactivées : message explicite, pas de crash.
- [ ] Tester « Tester maintenant » : toast immédiat ou diagnostic clair.

## 4. Sauvegarde et restauration

### 4.1 Sauvegarde

- [ ] Depuis une bibliothèque contenant au moins deux espaces, favoris, albums, exclusions, dates corrigées et lieux, créer une sauvegarde ZIP.
- [ ] Inspecter le ZIP : `catalog.db`, `library.json`, `manifest.json`, `media/…`.
- [ ] Vérifier que `derived/` et `imports/` sont absents.
- [ ] Vérifier le manifeste : nombre de souvenirs, sources, octets, identifiant de bibliothèque.
- [ ] Vérifier que les médias sources sont présents et non réécrits.

### 4.2 Restauration

- [ ] Restaurer dans un dossier vide.
- [ ] Rouvrir la bibliothèque restaurée.
- [ ] Comparer les comptages : souvenirs, assets, sources, espaces, albums, favoris, exclusions, dates corrigées, lieux.
- [ ] Vérifier les chemins relatifs : la bibliothèque peut être déplacée dans un autre dossier.
- [ ] Vérifier que les miniatures sont absentes mais régénérables.
- [ ] Restaurer dans un dossier non vide : refus, fichiers existants préservés.
- [ ] Restaurer une sauvegarde tronquée : refus explicite, pas de dossier partiel.
- [ ] Restaurer une sauvegarde sans `catalog.db`/`manifest.json` : refus explicite.
- [ ] Restaurer une sauvegarde avec une entrée `../` : refus, aucune écriture hors cible.
- [ ] Tester un média manquant ou tronqué dans le ZIP : détection attendue (actuellement vérification faible, QA-18).
- [ ] Restaurer sur une autre machine ou un autre dossier utilisateur : aucune dépendance à l'ancien chemin absolu.

## 5. Recherche texte, OCR et visuelle

### 5.1 Recherche texte

- [ ] Rechercher une clé externe connue dans la bibliothèque.
- [ ] Rechercher un lieu connu dans la bibliothèque : résultat attendu.
- [ ] Rechercher une date formatée dans la bibliothèque : résultat attendu.
- [ ] Rechercher un texte OCR dans la bibliothèque : résultat attendu (actuellement absent, QA-12).
- [ ] Rechercher un lieu dans l'écran Recherche : résultat attendu (actuellement absent, QA-12).
- [ ] Rechercher un texte OCR dans l'écran Recherche : résultat attendu.
- [ ] Vérifier l'état vide et le compteur de résultats.
- [ ] Vérifier qu'un texte présent uniquement dans un calque est indexé si l'OCR est activé.

### 5.2 OCR local

- [ ] Sans pack de langue OCR : l'analyse doit signaler l'absence sans bloquer l'application.
- [ ] Avec pack de langue : lancer l'analyse, vérifier le nombre de textes, la durée et les résultats.
- [ ] Vérifier qu'un texte reconnu est cherchable.
- [ ] Vérifier que l'OCR d'un calque est associé au bon souvenir.
- [ ] Vérifier que désactiver l'OCR ne supprime pas les souvenirs.
- [ ] Vérifier que les résultats OCR peuvent être supprimés/regénérés sans perdre les sources.

### 5.3 Recherche visuelle et visages

- [ ] Lancer l'analyse des empreintes visuelles.
- [ ] Choisir une référence, lancer « Trouver des similaires ».
- [ ] Vérifier le nombre de résultats et l'ordre « de la plus proche à la plus éloignée » (actuellement l'ordre de `Cards` est réutilisé, à vérifier).
- [ ] Vérifier qu'aucune requête réseau n'est effectuée.
- [ ] Activer les groupes de visages, vérifier les suggestions, les noms et les limites.
- [ ] Vérifier qu'un groupe de visages ne crée pas d'identité certaine automatique.
- [ ] Vérifier que les éléments exclus ne réapparaissent pas dans les résultats/suggestions.

## 6. Albums

- [ ] Créer un album avec un nom valide ; vérifier la persistance après redémarrage.
- [ ] Renommer l'album ; vérifier le nom dans la liste et l'en-tête.
- [ ] Ajouter des souvenirs datés et non datés depuis la bibliothèque.
- [ ] Vérifier l'ordre, la couverture et le compteur.
- [ ] Retirer un souvenir de l'album (si l'action existe ; sinon QA-16).
- [ ] Réordonner les éléments (si l'action existe ; sinon QA-16).
- [ ] Supprimer l'album : confirmation, sources et souvenirs conservés.
- [ ] Réimporter une archive : l'album et ses membres restent.
- [ ] Restaurer une sauvegarde : l'album et ses membres reviennent.
- [ ] Vérifier « depuis le … » : grammaire et intervalle (première date seulement actuellement).
- [ ] Vérifier que les albums de deux espaces ne se mélangent pas.

## 6bis. Récapitulatifs

- [ ] Générer un récapitulatif annuel, un mensuel et un saisonnier.
- [ ] Vérifier la couverture, le compteur, les favoris et les vidéos.
- [ ] Vérifier qu'un récapitulatif vide ou à faible couverture est signalé sans inventer de contenu.
- [ ] Modifier la sélection manuellement, réordonner, puis vérifier la conservation.
- [ ] Enregistrer un récapitulatif ; vérifier qu'il est réellement retrouvable après redémarrage (défaut QA-03 attendu : aucune liste).
- [ ] Enregistrer deux fois le même récapitulatif : vérifier s'il crée un doublon.
- [ ] Vérifier que les exclusions ne réapparaissent pas dans une sélection générée.
- [ ] Vérifier qu'un récapitulatif d'une année passée peut être généré (mois/saison : actuellement année courante seulement, QA-15).
- [ ] Vérifier « Hiver » en année bissextile : le 29 février doit être inclus (QA-15).
- [ ] Vérifier le bouton « Lire la Story » et l'export du récapitulatif.

## 7. Dates, lieux et fuseau

- [ ] Vérifier la provenance d'une date MP4 : « creation_time du conteneur MP4 (UTC) », pas « prise de vue ».
- [ ] Vérifier la provenance EXIF : `DateTimeOriginal` prioritaire sur `DateTime`.
- [ ] Vérifier qu'une photo sans EXIF est « Date inconnue » et reste accessible.
- [ ] Corriger une date ; vérifier la priorité utilisateur, la date affichée et la conservation des candidats d'origine.
- [ ] Ouvrir la fiche sans changer la date et cliquer « Enregistrer » : aucune correction ne doit être créée (QA-04).
- [ ] Retirer une correction : la date d'origine revient.
- [ ] Changer de fuseau : les dates avec instant UTC se déplacent, les dates locales sans offset ne sont pas inventées.
- [ ] Tester 29 février : exactitude en année bissextile, règle visible pour les autres années.
- [ ] Tester une date proche de minuit : le jour de classement suit le fuseau de la bibliothèque.
- [ ] Tester un changement d'heure (DST).
- [ ] Tester une date partielle (mois/année) : pas d'anniversaire quotidien exact.
- [ ] Vérifier « Ce jour-là » : exclusions, dates inconnues, années antérieures, fuseau de la bibliothèque.
- [ ] Vérifier « Surprends-moi » : pas de répétition immédiate, pas d'élément masqué.
- [ ] Vérifier « Une journée à redécouvrir » : pas d'élément masqué, pas de calque seul.
- [ ] Saisir un lieu avec libellé seul, sans coordonnées : vérifier la conservation (QA-33) ou le message de validation.
- [ ] Saisir un lieu avec latitude/longitude : vérifier l'affichage sur la carte et la recherche.
- [ ] Vérifier qu'un lieu n'est jamais déduit de l'historique du compte ou du fuseau.

## 8. Lecteur Story

- [ ] Ouvrir une Story depuis l'accueil, la bibliothèque, un album et une recherche.
- [ ] Vérifier la progression, le compteur `n / total`, la pause, le suivant/précédent et la fermeture.
- [ ] Tester Espace, flèches gauche/droite, Échap, O (calque), M (son).
- [ ] Vérifier l'affichage photo avec et sans calque.
- [ ] Vérifier une vidéo H.264 et une vidéo HEVC (si le codec est disponible).
- [ ] Vérifier un calque de même ratio, plus grand, plus petit et de ratio différent : pas de déformation (QA-14).
- [ ] Vérifier un média manquant : état explicite, pas d'écran noir silencieux.
- [ ] Vérifier une miniature corrompue : état « aperçu à générer », pas une tuile vide (QA-24).
- [ ] Vérifier la durée d'affichage des photos réglée dans Réglages.
- [ ] Vérifier le son et le mode muet, y compris après changement d'item.
- [ ] Vérifier que la fermeture de la Story arrête la vidéo et libère les ressources.

## 9. Réglages, design et responsive

- [ ] Thème système, clair, sombre : appliqué à toute la fenêtre et à la barre de titre.
- [ ] « Réduire les animations » : comportement compréhensible ; le réglage Windows reste prioritaire.
- [ ] Durée photo entre 1 et 120 s ; valeur invalide refusée.
- [ ] Autoplay vidéo et son : effet réel dans la Story.
- [ ] Fuseau : libellé complet, sélection stable après redémarrage.
- [ ] Rappels : activation/désactivation, heure, mode discret, état de la tâche.
- [ ] OCR/visages : activation, analyse, suppression/regénération.
- [ ] Sauvegarde/restauration : parcours complet.
- [ ] Vérifier les largeurs 1024, 1280, 1600 px et le portrait 1040×1800 : pas de débordement, barre latérale compacte si annoncée.
- [ ] Vérifier les captures paysage 1320×860 : pas de bord droit coupé.
- [ ] Vérifier les contrastes des textes, boutons, badges, états désactivés et focus.
- [ ] Vérifier la navigation clavier complète, le focus visible et le lecteur d'écran.
- [ ] Vérifier Ctrl+F (si annoncé) ; sinon noter QA-28.
- [ ] Vérifier les états vides : aucun souvenir, aucun album, aucun récapitulatif, aucune recherche, aucun lieu, aucune tâche planifiée.

## 10. Erreurs, sécurité et données

- [ ] ZIP corrompu, tronqué, vide, chiffré, avec entrée `../`, avec nom réservé Windows, avec chemin UNC/lecteur absolu.
- [ ] ZIP bombe ou archive énorme : limites de ressources, pas de saturation disque (sur VM isolée uniquement).
- [ ] Archive imbriquée : ignorée avec diagnostic, pas d'ouverture récursive.
- [ ] Entrée `../` : aucun fichier écrit hors de la bibliothèque.
- [ ] Fichier verrouillé, disque plein, dossier non inscriptible : messages clairs, reprise.
- [ ] Bibliothèque déplacée : les chemins internes restent valides.
- [ ] Bibliothèque restaurée sur un autre PC : aucun chemin absolu requis.
- [ ] Vérifier qu'aucune requête réseau n'est effectuée par l'import, la recherche, l'OCR, les visages ou la carte.
- [ ] Vérifier que les journaux ne contiennent ni jeton, ni URL signée, ni chemin personnel.
- [ ] Vérifier que les captures et rapports ne contiennent aucune donnée personnelle.

## 11. Installation, mise à jour et release

- [ ] Construire l'installateur depuis la branche de release ; vérifier que les fichiers `installer/`, `scripts/` et `LICENSES/` sont bien présents.
- [ ] Vérifier que `artifacts\publish` contient `Memo.pri` et les fichiers `.xbf` de toutes les pages.
- [ ] Installer dans `%LOCALAPPDATA%\Programs\Memo` sans élévation.
- [ ] Vérifier SmartScreen et le SHA-256 publié.
- [ ] Lancer, créer une bibliothèque, importer, fermer, relancer.
- [ ] Désinstaller : programmes supprimés, données utilisateur conservées ou clairement documentées.
- [ ] Mettre à jour par-dessus une version précédente : bibliothèque et réglages conservés.
- [ ] Vérifier les liens README (`docs/install.md`, `docs/release.md`) et les licences.
- [ ] Vérifier la présence de `Memo-0.1.0-win-x64-setup.exe` et des notes de release.

## 12. Signature de recette

| Élément | Valeur |
|---|---|
| Testeur | |
| Date | |
| Commit | |
| Build / installateur | |
| Windows | |
| Corpus utilisé | |
| Défauts bloquants restants | |
| Décision | ☐ GO ☐ GO avec réserves ☐ NO-GO |

Rappel : ne cocher un test que s'il a réellement été exécuté. Les tests destructifs (ZIP bombe, disque plein, archive malveillante) doivent être réalisés dans une VM isolée et jetable, jamais sur une bibliothèque personnelle.
