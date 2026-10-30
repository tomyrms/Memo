# Audit d'accessibilité et de clavier — Memo

Date : 30 octobre 2026. Branche : `feat/a11y`. Commit audité : `c620cba`.
Périmètre : `src/Memo.App/**/*.xaml` et `*.xaml.cs`, plus les contrôles C# sans XAML
(`MediaStage`, `MemoMap`, `Widgets`) et les thèmes. Aucun fichier de `src/`, `tests/`
ou `installer/` n'a été modifié.

## Méthode et limites

L'audit est **statique** : lecture intégrale des 15 fichiers XAML, des 13 code-behind,
des contrôles personnalisés et des dictionnaires de thèmes. Les ratios de contraste
sont calculés avec la formule WCAG 2.2 (luminance relative, compositing alpha lorsque
le fond est translucide). Les numéros de ligne renvoient à l'état du dépôt au commit
ci-dessus.

**Ce que cet audit ne prouve pas.** Aucune compilation, aucune exécution, aucune
capture, aucun passage de Narrateur, NVDA ou JAWS. Les comportements décrits comme
« le nom est vide », « le focus s'échappe » ou « le ToolTip n'est pas un nom » reposent
sur le comportement documenté des AutomationPeers WinUI ; ils doivent être confirmés
à l'exécution. Les valeurs de contraste sur photo sont des **pires cas** calculés, pas
des mesures sur image réelle. Les seuils de taille et les débordements à 1024 px sont
déduits des largeurs déclarées, pas mesurés au pixel. Les points marqués « à vérifier »
dans les vérifications manuelles le sont pour cette raison.

## Résumé exécutif

La couche visuelle est disciplinée — palette chaude, cibles de 32 px, focus teinté,
états vides rédigés — mais la couche d'accessibilité programmatique est presque absente.

- **1 seul `AutomationProperties.Name`** dans toute l'application (`Ui/Widgets.cs:81`),
  pour les tuiles de l'accueil. **0 `LiveSetting`, 0 `HeadingLevel`, 0 `LabeledBy`,
  0 `HelpText`, 0 `AccessKey`, 0 `KeyboardAccelerator`, 0 `TabIndex`.**
- **Lecteur Story** : les 7 boutons de la barre et le bouton central sont des
  `FontIcon` sans nom ; un lecteur d'écran annonce des boutons sans libellé. Le
  changement d'élément, la progression et l'état du son ne sont jamais annoncés.
- **Tuiles mémoire** : les grilles de Bibliothèque, Albums, Récapitulatifs et
  Recherche n'ont aucun nom accessible par tuile. Une photo datée peut être annoncée
  « Aperçu à générer », rien du tout, ou un dump technique du `MemoryCard`.
- **Clavier** : le lecteur n'est pas modal. La tabulation sort vers la coquille
  masquée, et la fermeture dépose le focus dans le vide. Ctrl+F, Suppr et le menu
  contextuel clavier ne sont pas implémentés ; le menu contextuel souris lui-même
  n'est pas câblé (`RightTapped` absent du XAML).
- **Contraste** : la palette tient globalement. Trois écarts réels : badge
  « Démonstration » à **4,49:1** (texte de 11 px), compteur du lecteur sur photo
  claire à **2,11:1**, piste de progression à **2,78:1**. Les badges de tuile sur
  photo passent à 6,3–8,6:1 dans le pire cas.
- **1024 px et 200 %** : la rangée de filtres de la Bibliothèque demande ~800 px pour
  ~720 px disponibles ; la barre latérale ne passe jamais en compact ; plusieurs
  rangées à largeur fixe (export du lecteur, actions des récapitulatifs, colonnes du
  dialogue d'édition) se coupent à 200 %.
- **Mouvement** : aucun clignotement, mais le réglage « Réduire les animations » est
  persisté et **jamais lu** ; `UISettings.AnimationsEnabled` n'est utilisé nulle part ;
  la transition de `Frame` est inconditionnelle.
- **États** : les états vides et d'import sont honnêtes et textuels. En revanche, un
  média absent produit un écran noir sans message, et presque aucun changement d'état
  n'est annoncé.

## Tableau de scores

Notes sur 10, moyenne simple non pondérée. Les notes sanctionnent l'usage réel par une
personne qui dépend du clavier ou d'un lecteur d'écran, pas l'élégance du code.

| Axe | Note | Justification | Constats principaux |
|---|---:|---|---|
| Lecteur d'écran | **2/10** | Un seul nom explicite dans toute l'UI ; aucune région live ; lecteur et tuiles muets. | P0-1, P0-2, P0-4, P1-4, P1-5 |
| Clavier | **3/10** | Les raccourcis du lecteur et Échap fonctionnent, les contrôles natifs sont tabulables ; mais le focus s'échappe, se perd à la fermeture, et les raccourcis promis manquent. | P0-3, P1-1, P1-2, P1-2b, P1-9 |
| Contraste | **7/10** | Palette cohérente, la plupart des couples > 5:1 ; trois écarts réels et un marginal. | P2-1, P2-2, P2-3 |
| Échelle et densité | **4/10** | Cibles 32 px et tuiles 180×240 respectées ; mais 1024 px et 200 % cassent des rangées fixes, la nav ne se compacte pas. | P1-7, P2-6, P2-7, P2-8, P2-9 |
| Mouvement | **5/10** | Presque aucune animation, aucun clignotement ; le réglage de réduction est inopérant et la transition de page n'en tient pas compte. | P1-8 |
| États | **5/10** | Bons textes d'états vides et d'import ; média manquant = écran noir ; états non annoncés ; son/calque par opacité seule. | P1-3, P1-6, P2-10, P2-11 |
| **Global** | **4,3/10** | Interface visuellement soignée, inutilisable en autonomie au lecteur d'écran, fragile au clavier et à 200 %. | 4 P0, 13 P1 |

## Définitions de priorité

- **P0** : empêche une personne qui utilise un lecteur d'écran ou le clavier d'accomplir
  une tâche centrale (parcourir, lire, revenir).
- **P1** : barrière sérieuse ou promesse explicite du DESIGN non tenue ; à corriger
  dans ce cycle.
- **P2** : gêne réelle, contournable, ou risque à confirmer à l'exécution.
- **P3** : finition, cohérence, vérification.

---

## P0 — bloquants

### P0-1 · Les boutons icône du lecteur n'ont pas de nom accessible

- **Fichier** : `src/Memo.App/StoryView.xaml:103-198` (7 boutons), `StoryView.xaml:27-42`
  (bouton central `BigPlay`).
- **Constat** : chaque bouton a pour seul contenu un `FontIcon`. Les libellés sont
  fournis par `ToolTipService.ToolTip` (lignes 111, 125, 139, 152, 166, 180, 193), mais
  WinUI ne dérive pas le nom accessible d'un ToolTip : le nom d'un `Button` dont le
  contenu est un `FontIcon` est vide. `BigPlay` n'a même pas de ToolTip.
- **Impact** : au lecteur d'écran, le lecteur Story expose une succession de boutons
  « bouton » sans fonction. Exporter, couper le son, masquer le calque, précédent,
  lecture, suivant et fermer sont indistinguables. La barre de lecture est inutilisable
  en autonomie.
- **Correctif** : nommer chaque bouton, et exposer l'état des boutons à bascule.

```xml
<Button x:Name="ExportButton"
        AutomationProperties.Name="Exporter la Story en vidéo"
        AutomationProperties.HelpText="Ouvre une boîte de dialogue pour enregistrer un fichier MP4."
        ToolTipService.ToolTip="Exporter cette Story en vidéo">
```

Pour le son et le calque, remplacer `Button` par `ToggleButton` (l'état coché est alors
exposé par le pattern Toggle) et garder un nom stable :

```xml
<ToggleButton x:Name="MuteButton"
              AutomationProperties.Name="Son"
              AutomationProperties.HelpText="Raccourci M"
              ToolTipService.ToolTip="Couper le son (M)" />
```

`BigPlay` doit recevoir `AutomationProperties.Name="Lecture"` (ou « Reprendre » quand
la lecture est en pause) et être mis à jour dans `UpdatePlayIcon()`
(`StoryView.xaml.cs:372-375`).

### P0-2 · Les tuiles mémoire n'ont pas de nom accessible

- **Fichiers** : `src/Memo.App/Controls/MemoryTile.xaml:1-133` (aucune propriété
  d'automatisation) ; utilisations dans `Pages/LibraryPage.xaml:107-111`,
  `Pages/AlbumsPage.xaml:111-114`, `Pages/RecapsPage.xaml:143-146`,
  `Pages/SearchPage.xaml:51-54` et `:125-128`.
- **Constat** : `MemoryTile` est un `UserControl` sans `AutomationProperties.Name`. Le
  `GridViewItem` qui l'héberge dérive au mieux un fragment de texte d'un descendant
  (« Calque seul », « Aperçu à générer », une date) ; selon le peer, il peut aussi
  retomber sur `MemoryCard.ToString()`, qui est un dump positionnel de 24 champs
  (`Id`, `SpaceId`, `ExternalKey`, `SizeBytes`…). Dans aucun cas le nom n'est « photo
  du 12 mars 2019, Lausanne, calque disponible, favori ». Une photo dont la miniature
  est chargée n'expose aucun texte utile : la tuile est annoncée comme un élément sans
  nom, ou avec un contenu technique.
- **Impact** : la Bibliothèque, les Albums, les Récapitulatifs et la Recherche — les
  quatre surfaces de consultation — sont impossibles à parcourir au lecteur d'écran.
  C'est le blocage le plus large de l'application.
- **Correctif** : construire un nom stable dans `MemoryTile.Apply()`
  (`MemoryTile.xaml.cs:36-108`), et le poser sur la tuile **et** sur le conteneur.

```csharp
private static string BuildName(MemoryCard card)
{
    var date = card.LocalDate is { } d
        ? d.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", Services.AppState.French)
        : "Date inconnue";
    var kind = card.Kind == MediaKind.Video ? "vidéo" : "photo";
    var parts = new List<string> { $"{date}, {kind}" };
    if (card.DurationMs is > 0) parts.Add(TimeSpan.FromMilliseconds(card.DurationMs.Value).ToString(@"m\:ss"));
    if (!string.IsNullOrWhiteSpace(card.LocationLabel)) parts.Add(card.LocationLabel);
    if (card.Status == MemoryStatus.OverlayOnly) parts.Add("média non trouvé, calque seul");
    if (card.OverlayCount > 0 && card.Status != MemoryStatus.OverlayOnly) parts.Add("calque disponible");
    if (card.IsFavorite) parts.Add("favori");
    if (card.IsExcluded) parts.Add("masqué des suggestions");
    return string.Join(", ", parts);
}
```

Puis, dans `Apply()` :

```csharp
var name = BuildName(card);
AutomationProperties.SetName(this, name);
AutomationProperties.SetHelpText(this, "Entrée pour lire la Story.");
```

Pour que le conteneur porte le nom (et pas seulement l'enfant), le poser dans
`GridView.ContainerContentChanging` ou dans un `ItemContainerStyle` avec un
`GridViewItem` dérivé. Le patron existe déjà et fonctionne :
`Ui/Widgets.cs:69-84` pose un nom sur le `Button` qui encapsule la tuile de l'accueil.
Le réutiliser pour les grilles corrigerait les quatre pages d'un coup.

### P0-3 · Le lecteur Story ne piège pas le focus ; la tabulation sort vers la coquille masquée

- **Fichiers** : `src/Memo.App/MainWindow.xaml:7-12` (la `ShellPage` reste dans l'arbre,
  visible et active sous `StoryView`) ; `src/Memo.App/StoryView.xaml:1-7` ;
  `StoryView.xaml.cs:35-43` et `:392-399`.
- **Constat** : `StoryView` est un `UserControl` superposé, pas un dialogue modal. Rien
  ne désactive `ShellPage`, ne borne la portée de tabulation ni ne réagit à la touche
  Tab. Après le bouton « Fermer », la tabulation suivante atteint les
  `NavigationViewItem` de la coquille, pourtant recouverts par l'overlay opaque
  (`#0E0D0C`, `StoryView.xaml:9`).
- **Impact** : au clavier, on perd le focus dans une interface invisible ; les flèches
  et Espace cessent d'agir sur le lecteur ; on peut activer « Importer » ou changer de
  page sans le voir. C'est un défaut d'ordre de focus (2.4.3) et de contexte annoncé
  (4.1.2), pas un simple confort.
- **Correctif** : rendre la coquille inerte pendant la lecture et restaurer son état.

```csharp
// MainWindow.ShowStory (ligne 132)
private WeakReference<FrameworkElement>? _storyInvoker;

public void ShowStory(IReadOnlyList<MemoryCard> items, int index)
{
    if (items.Count == 0) return;
    _storyInvoker = new WeakReference<FrameworkElement>(
        FocusManager.GetFocusedElement(Root.XamlRoot) as FrameworkElement);
    Shell.IsEnabled = false;          // les enfants désactivés ne sont plus tabulables
    Story.Visibility = Visibility.Visible;
    Story.Start(items, Math.Clamp(index, 0, items.Count - 1));
}

private void OnStoryClosed(object? sender, EventArgs e)
{
    Story.Visibility = Visibility.Collapsed;
    Shell.IsEnabled = true;
    if (_storyInvoker?.TryGetTarget(out var target) == true && target.IsLoaded)
        target.Focus(FocusState.Programmatic);
    _storyInvoker = null;
}
```

Si `IsEnabled = false` modifie trop le rendu, utiliser `Shell.Visibility = Collapsed`
(l'overlay est opaque) ou un `FocusManager` avec `TabFocusNavigation="Cycle"` sur un
conteneur racine du lecteur. Vérifier ensuite que Maj+Tab depuis « Exporter » reste
dans le lecteur.

### P0-4 · Le changement d'élément, la progression et l'état du son ne sont jamais annoncés

- **Fichiers** : `src/Memo.App/StoryView.xaml:44-50` (`Counter`), `:85-97` (`Caption`,
  `CaptionTitle`, `CaptionSubtitle`), `:16-19` et `StoryView.xaml.cs:45-74`
  (progression), `:76-120` (`ShowCurrent`), `:372-388` (icônes et son).
- **Constat** : `Counter`, `CaptionTitle` et `CaptionSubtitle` changent à chaque
  élément sans `AutomationProperties.LiveSetting`. La progression est une file de
  `Border` de 3 px sans sémantique ni nom. `Stage` (`MediaStage`) est un canevas Skia
  sans nom : le lecteur d'écran ne sait ni quel média est affiché, ni combien d'éléments
  restent, ni si le son est coupé.
- **Impact** : même si les boutons étaient nommés, une personne au lecteur d'écran ne
  pourrait pas suivre la Story : aucun retour lors du passage à l'élément suivant, ni
  sur la pause, ni sur le calque. La fonction « Experience » centrale est muette.
- **Correctif** : une seule région live polie (la légende), un nom sur la scène, une
  sémantique de progression. Éviter deux régions live simultanées : le lecteur d'écran
  répéterait chaque élément.

```xml
<TextBlock x:Name="CaptionTitle"
           AutomationProperties.LiveSetting="Polite" />
<TextBlock x:Name="CaptionSubtitle" />
```

```csharp
// ShowCurrent, après avoir rempli CaptionTitle/CaptionSubtitle
AutomationProperties.SetName(CaptionTitle,
    $"{CaptionTitle.Text}. {CaptionSubtitle.Text}. Élément {_index + 1} sur {_items.Count}.");
AutomationProperties.SetName(Stage,
    $"{BuildSubtitle(card)}. {AppState.Current.FormatDate(card)}.");
AutomationProperties.SetName(Counter, $"Élément {_index + 1} sur {_items.Count}");
```

Le nom posé sur `CaptionTitle` remplace son texte pour UIA : il doit contenir la date
**et** le détail, sinon la légende visible est moins riche que l'annonce. `Counter` et
`Stage` n'ont pas de `LiveSetting` ; ils sont lus quand on les atteint, pas à chaque
changement, ce qui évite les doublons.

Remplacer la progression dessinée par un `ProgressBar` (ou ajouter un
`AutomationProperties.Name` et un `AutomationProperties.HelpText` au panneau) avec
`AutomationProperties.Name="Progression de la Story"`. Pour un vrai suivi, exposer une
`ProgressBar` de 0 à `_items.Count`, invisible mais **pas** `Collapsed` (`Opacity="0"`,
`IsHitTestVisible="False"`), et annoncer la légende, pas chaque tick de 50 ms : une
région live agressive sur la barre rendrait le lecteur d'écran bavard.

---

## P1 — barrières sérieuses

### P1-1 · Entrée et sortie du lecteur : focus initial sans nom, focus perdu à la fermeture

- **Fichier** : `src/Memo.App/StoryView.xaml.cs:35-43` (`Start`), `:392-399` (`Close`).
- **Constat** : `Start` appelle `Focus(FocusState.Programmatic)` sur le `UserControl`
  lui-même (`IsTabStop="True"`, `StoryView.xaml:6`). Ce contrôle n'a pas de nom et, par
  défaut, pas de visuel de focus (`UseSystemFocusVisuals` non défini). `Close` rappelle
  `Focus()` sur le même contrôle **après** que `MainWindow` l'a passé en `Collapsed`.
- **Impact** : à l'ouverture, le lecteur d'écran annonce un conteneur anonyme et aucun
  repère visuel n'indique où est le clavier. À la fermeture, le focus est déposé dans
  un élément invisible ; la tabulation suivante repart du début de la fenêtre. Le
  retour au souvenir qu'on vient de lire est perdu.
- **Correctif** : nommer le lecteur, focaliser le bouton de lecture (visible et nommé),
  et restaurer le focus sur la tuile d'origine (voir P0-3). Ajouter au besoin
  `UseSystemFocusVisuals="True"` et un `AutomationProperties.Name="Lecteur Story"`.

### P1-2 · Le menu contextuel des tuiles n'est pas câblé : édition de date, favori et exclusion inaccessibles

- **Fichiers** : `src/Memo.App/Pages/LibraryPage.xaml:88-120` (le `GridView` n'a ni
  `RightTapped` ni `ContextFlyout`) ; `LibraryPage.xaml.cs:168-197`
  (`OnGridRightTapped` n'est référencé nulle part).
- **Constat** : le handler qui construit le `MenuFlyout` (« Ouvrir en Story »,
  « Informations et date », favori, exclusion) est du code mort. La grille ouvre une
  Story au clic, mais aucune autre action n'est accessible sur un élément seul. La
  correction de date et de lieu (`ShowInfoAsync`, `:301-421`) n'a donc **aucun point
  d'entrée** dans l'interface.
- **Impact** : la tâche « corriger une date », critère de réussite du produit, est
  irréalisable à la souris comme au clavier. Les utilisateurs de lecteur d'écran n'ont
  pas non plus de menu contextuel (Maj+F10) puisque `ContextFlyout` est absent.
- **Correctif** : câbler l'événement et le clavier.

```xml
<GridView x:Name="Grid"
          RightTapped="OnGridRightTapped"
          KeyDown="OnGridKeyDown"
          IsItemClickEnabled="True" ...>
```

```csharp
using Microsoft.UI.Input;              // InputKeyboardSource
using Windows.System;
using Windows.UI.Core;

private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
{
    var shift = InputKeyboardSource
        .GetKeyStateForCurrentThread(VirtualKey.Shift)
        .HasFlag(CoreVirtualKeyStates.Down);

    if (e.Key == VirtualKey.Application || (e.Key == VirtualKey.F10 && shift))
    {
        var card = (Grid.SelectedItem ?? Grid.Items.FirstOrDefault()) as MemoryCard;
        if (card is not null)
        {
            ShowCardMenu(card);
            e.Handled = true;
        }
    }
}
```

Extraire la construction du flyout dans `ShowCardMenu(MemoryCard card)` et l'appeler
depuis le clic droit **et** le clavier. Ajouter aussi une action visible « Informations »
dans la barre de sélection pour les utilisateurs qui ne connaissent pas le clic droit.

### P1-2b · Ctrl+F et Suppr ne sont pas implémentés, les raccourcis ne sont pas annoncés

- **Fichiers** : aucun `KeyboardAccelerator` dans `src/Memo.App` (0 occurrence) ;
  `DESIGN.md:93` promet Espace, flèches, Échap, Ctrl+F et Suppr.
- **Constat** : Espace/flèches/Échap sont implémentés dans le lecteur
  (`StoryView.xaml.cs:401-430`). Ctrl+F (recherche) et Suppr (retirer d'un album avec
  confirmation) ne sont implémentés nulle part. Aucun `AccessKey` n'existe.
- **Impact** : les raccourcis documentés ne fonctionnent pas ; un utilisateur clavier
  doit passer par la souris ou par plusieurs tabulations pour rechercher.
- **Correctif** : un accélérateur global sur la coquille pour Ctrl+F, un sur la grille
  d'album pour Suppr.

```xml
<UserControl.KeyboardAccelerators>
    <KeyboardAccelerator Key="F" Modifiers="Control" Invoked="OnSearchAccelerator" />
</UserControl.KeyboardAccelerators>
```

`OnSearchAccelerator` navigue vers `SearchPage` et focalise `Query`. Pour Suppr,
vérifier `Grid.SelectedItems.Count > 0`, ouvrir la confirmation, puis appeler
`RemoveFromAlbumAsync`. Annoncer le raccourci dans l'infobulle et le `HelpText`.

### P1-3 · Aucune région live : compteurs, résultats, sélections et statuts sont muets

- **Fichiers** : `Pages/LibraryPage.xaml:21-24` (`CountLabel`), `:157-160`
  (`SelectionLabel`) ; `Pages/SearchPage.xaml:40-44` (`ResultCount`) et `:65-71`
  (`EmptyText`) ; `Pages/ImportsPage.xaml:119` (`ProgressText`) ; `Pages/SettingsPage.xaml:132-135`
  (`ReminderStatus`) et `:160-163` (`AnalysisStatus`) ; `StoryView.xaml:70-74`
  (`ExportStatus`).
- **Constat** : zéro `AutomationProperties.LiveSetting` dans le dépôt. Les textes
  changent visuellement mais aucun événement de changement n'est émis vers UIA.
- **Impact** : après une recherche, un filtre, une sélection ou une analyse, le
  lecteur d'écran ne signale rien. L'utilisateur doit quitter puis revenir sur la zone
  pour découvrir le résultat.
- **Correctif** : `AutomationProperties.LiveSetting="Polite"` sur les compteurs et
  statuts ; `"Assertive"` réservé aux erreurs. Pour les progressions, annoncer par
  phase ou toutes les secondes, jamais à chaque fichier.

```xml
<TextBlock x:Name="CountLabel" AutomationProperties.LiveSetting="Polite" ... />
<TextBlock x:Name="SelectionLabel" AutomationProperties.LiveSetting="Polite" ... />
```

### P1-4 · Les champs de filtre et de réglage n'ont pas de nom programmatique

- **Fichiers** : `Pages/LibraryPage.xaml:51-57` (recherche, placeholder seul), `:58-67`
  (`TypeFilter`), `:68-73` (`YearFilter`) ; `Pages/RecapsPage.xaml:46-54` (`KindBox`),
  `:55-60` (`PeriodBox`) ; `Pages/SearchPage.xaml:33-39` (`Query`), `:106-109`
  (`ReferenceBox`) ; `Pages/ImportsPage.xaml:32-35` (`SpaceBox`) ;
  `Pages/SettingsPage.xaml:47-51` (`ThemeBox`), `:74-83` (`PhotoDuration`), `:99-105`
  (`TimeZoneBox`), `:123` (`ReminderTime`).
- **Constat** : les libellés visuels existent parfois à côté (« Thème », « Fuseau de
  classement », « Durée d'une photo », « Espace source »), mais ne sont reliés ni par
  `Header` ni par `LabeledBy`. Un `ComboBox` sans en-tête est annoncé par son élément
  sélectionné (« Tout, zone de liste déroulante »), pas par sa fonction. Le champ de
  recherche ne garde que son placeholder, qui disparaît à la saisie.
- **Impact** : au lecteur d'écran, impossible de distinguer le filtre de type du filtre
  d'année, ou le thème du fuseau. Les formulaires de réglages deviennent des suites de
  listes anonymes.
- **Correctif** : préférer `Header` (rendu au-dessus, associé automatiquement) :

```xml
<ComboBox x:Name="TypeFilter" Header="Type de média" ... />
<AutoSuggestBox x:Name="SearchBox" Header="Rechercher" PlaceholderText="Lieu, date, texte OCR" ... />
```

Pour les grilles à libellé à gauche (Réglages), nommer le `TextBlock` et utiliser
`AutomationProperties.LabeledBy="{Binding ElementName=ThemeLabel}"`, ou passer le
libellé en `Header` et accepter la disposition verticale.

### P1-5 · Les champs des dialogues n'ont qu'un placeholder, sans label visible

- **Fichiers** : `Pages/AlbumsPage.xaml.cs:127-141` (nom d'album) ;
  `Pages/ImportsPage.xaml.cs:223-233` (nom d'espace) ;
  `Pages/SettingsPage.xaml.cs:344-357` (nom de bibliothèque) ;
  `Pages/LibraryPage.xaml.cs:264-291` (« Ajouter à un album » : `ComboBox` nue).
- **Constat** : les `TextBox` sont créés avec `PlaceholderText` seul ; le placeholder
  disparaît dès la première frappe et n'est pas un label persistant. La `ComboBox`
  d'album n'a ni `Header` ni `AutomationProperties.Name`.
- **Impact** : le champ perd son nom en cours de saisie ; le lecteur d'écran annonce
  « zone de texte » sans fonction. Dans « Ajouter à un album », la liste des albums est
  annoncée sans objet.
- **Correctif** : ajouter `Header = "Nom de l'album"` (ou un `TextBlock` + `LabeledBy`)
  et `Header = "Album de destination"` sur la `ComboBox`. Pour les erreurs, voir P2-13.

### P1-6 · Son et calque : état non exposé, opacité seule

- **Fichier** : `src/Memo.App/StoryView.xaml.cs:377-388`.
- **Constat** : `OnToggleMute` change `MuteButton.Opacity` à 0,55 et rien d'autre. Le
  glyphe, le nom et l'infobulle restent identiques. `OnToggleOverlay` ne change ni
  glyphe ni nom. Les deux boutons sont des `Button` simples, pas des `ToggleButton`.
- **Impact** : l'état « son coupé » n'est perceptible que par une variation d'opacité,
  donc par la vue seule, et n'est jamais annoncé. Un utilisateur malvoyant ou au
  lecteur d'écran ne sait pas si le son est actif.
- **Correctif** : `ToggleButton` + état coché, glyphe différent (`E767`/`E74F`), nom
  stable et `HelpText` mis à jour.

```csharp
MuteButton.IsChecked = _muted;
AutomationProperties.SetName(MuteButton, _muted ? "Son coupé" : "Son activé");
```

### P1-7 · Les filtres de la Bibliothèque débordent à 1024 px

- **Fichier** : `src/Memo.App/Pages/LibraryPage.xaml:43-86`.
- **Constat** : la rangée demande au minimum 300 (recherche) + 118 (type) + 132 (année)
  + ~116 (« Date inconnue ») + ~104 (« À compléter ») + 4 gouttières de 8 = **~802 px**.
  À 1024 px de fenêtre, avec la barre latérale ouverte (240) et les marges (64), il
  reste **720 px**. La colonne `*` ne peut pas comprimer un enfant à `Width="300"` :
  la recherche chevauche ou dépasse les filtres. À 1040×1800, même problème (736 px).
- **Impact** : à la largeur minimale annoncée par le DESIGN (`DESIGN.md:92`), les
  filtres sont coupés ou superposés ; les derniers boutons peuvent sortir de l'écran.
- **Correctif** : supprimer la largeur fixe, donner un `MinWidth` à la recherche et
  déplacer les deux filtres secondaires dans un menu à partir d'un seuil.

```xml
<AutoSuggestBox x:Name="SearchBox" MinWidth="220" HorizontalAlignment="Stretch" ... />
```

```xml
<VisualStateManager.VisualStateGroups>
  <VisualStateGroup>
    <VisualState x:Name="Wide">
      <VisualState.StateTriggers><AdaptiveTrigger MinWindowWidth="1280" /></VisualState.StateTriggers>
    </VisualState>
    <VisualState x:Name="Narrow">
      <VisualState.StateTriggers><AdaptiveTrigger MinWindowWidth="0" /></VisualState.StateTriggers>
      <VisualState.Setters>
        <Setter Target="UndatedToggle.Visibility" Value="Collapsed" />
        <Setter Target="MissingToggle.Visibility" Value="Collapsed" />
      </VisualState.Setters>
    </VisualState>
  </VisualStateGroup>
</VisualStateManager.VisualStateGroups>
```

Conserver les deux filtres dans un `MenuFlyout` « Filtres » en mode étroit, avec des
cases à cocher, plutôt que de les supprimer.

### P1-8 · La réduction des animations est un réglage mort

- **Fichiers** : `Pages/SettingsPage.xaml:53-58` et `SettingsPage.xaml.cs:110-117`
  (persistance de `ReduceMotion`) ; `ShellPage.xaml.cs:84` (transition inconditionnelle) ;
  aucune occurrence de `UISettings.AnimationsEnabled` dans `src/`.
- **Constat** : le commutateur est enregistré dans `AppSettings` mais aucun écran, aucune
  transition ni aucun contrôle ne le lit. Chaque navigation utilise
  `EntranceNavigationTransitionInfo`, y compris quand Windows ou l'application demandent
  moins de mouvement. Le texte du réglage promet pourtant que « le réglage Windows reste
  prioritaire ».
- **Impact** : promesse non tenue ; les personnes sensibles au mouvement ne peuvent pas
  désactiver la transition de page. Le risque est faible en amplitude (fondu/translation
  native), mais le réglage trompe.
- **Correctif** : centraliser la décision.

```csharp
private static bool ReducedMotion()
{
    var system = new Windows.UI.ViewManagement.UISettings();
    return !system.AnimationsEnabled || Services.AppState.Current.Settings.ReduceMotion;
}

// ShellPage.Navigate
ContentFrame.Navigate(type, null, ReducedMotion()
    ? new SuppressNavigationTransitionInfo()
    : new EntranceNavigationTransitionInfo());
```

S'abonner à `UISettings.AnimationsEnabledChanged` pour réévaluer sans redémarrer.
Aucun autre mouvement n'est à conditionner aujourd'hui (pas de storyboard, pas de
parallaxe, pas de clignotement) ; c'est un point positif à conserver.

### P1-9 · La carte est un arrêt de tabulation mort et ses points sont inaccessibles au clavier

- **Fichiers** : `src/Memo.App/Controls/MemoMap.cs:24-51` ; `Pages/MapPage.xaml:36-42`
  et `:55-68`.
- **Constat** : `MemoMap` force `IsTabStop = true`, ne gère que `PointerPressed` et ne
  dessine pas de visuel de focus. Il n'a pas de nom. Les points ne sont atteignables
  qu'à la souris (rayon de 16 px). La liste `Places` offre une alternative, mais elle
  n'est pas annoncée comme telle.
- **Impact** : au clavier, la carte est un arrêt qui ne fait rien et n'affiche rien.
  Un utilisateur de lecteur d'écran n'a aucune représentation des points, seulement la
  liste — ce qui est acceptable si la liste est nommée et si la carte est retirée de
  l'ordre de tabulation.
- **Correctif** : soit retirer `IsTabStop` et marquer la carte comme image décorative
  (`AutomationProperties.AccessibilityView="Raw"`) en nommant la liste « Souvenirs
  situés » ; soit implémenter un vrai parcours clavier (Tab entre points, Entrée pour
  ouvrir) avec un visuel de focus. La première option est proportionnée.

### P1-10 · Les progressions d'import, d'analyse et d'export ne sont pas annoncées

- **Fichiers** : `Pages/ImportsPage.xaml:113` (`ProgressBar`), `:119` (`ProgressText`) ;
  `Pages/SettingsPage.xaml:165` (`AnalysisProgress`), `:160-163` (`AnalysisStatus`) ;
  `StoryView.xaml:69` (`ExportProgress`), `:70-74` (`ExportStatus`).
- **Constat** : les `ProgressBar` n'ont pas de nom ; les textes adjacents ne sont pas
  des régions live. Une `ProgressBar` n'est pas focalisable : le lecteur d'écran ne
  reçoit la valeur que si on l'atteint en mode exploration, et rien ne l'informe d'une
  fin ou d'une annulation.
- **Impact** : un import long ou une analyse locale paraît bloqué ; l'utilisateur ne
  sait pas quand l'opération est terminée, annulée ou en erreur.
- **Correctif** : `AutomationProperties.Name` sur chaque barre, `LiveSetting="Polite"`
  sur le texte de statut, et une annonce unique en fin d'opération (le `InfoBar` existe
  déjà). Éviter une région live sur la valeur qui change à chaque fichier.

### P1-11 · Aucune structure de titres

- **Fichiers** : `Pages/LibraryPage.xaml:20`, `AlbumsPage.xaml:23`, `RecapsPage.xaml:33`,
  `SearchPage.xaml:15`, `MapPage.xaml:19`, `ImportsPage.xaml:13`, `SettingsPage.xaml:9`
  (titres de page) ; tous les `Widgets.SectionHeader` (`Ui/Widgets.cs:46-67`) et les
  `TextBlock` de section (`ImportsPage.xaml:26`, `:60`, `:133`, `:185` ;
  `SettingsPage.xaml:17`, `:40`, `:67`, `:92`, `:115`, `:145`, `:174`, `:187`).
- **Constat** : zéro `AutomationProperties.HeadingLevel`. Tous les titres sont des
  `TextBlock` de style, sans sémantique. Les sections ne sont pas non plus des
  `Landmark`.
- **Impact** : impossible de naviguer par titres dans le lecteur d'écran, la méthode
  la plus rapide pour parcourir une page longue (Réglages, Imports). Les pages sont des
  blocs de texte indifférenciés.
- **Correctif** : `AutomationProperties.HeadingLevel="Level1"` sur le titre de page,
  `Level2` sur les sections. Dans `Widgets.SectionHeader`, poser le niveau sur le
  `TextBlock` créé ligne 51.

### P1-12 · Le changement de page n'est ni annoncé ni focalisé

- **Fichiers** : `ShellPage.xaml.cs:66-91` (`Navigate`), `ShellPage.xaml:119-121`
  (`Frame`) ; aucune page ne définit de focus initial.
- **Constat** : `Frame.Navigate` ne déplace pas le focus et aucun `Navigated` ne le fait.
  Après un clic dans la navigation, le focus reste sur le `NavigationViewItem` ; après
  un lancement, il n'est posé nulle part. Le titre de page n'est pas focalisable.
- **Impact** : au lecteur d'écran, changer de page ne produit aucune annonce de contexte ;
  au clavier, la tabulation suivante repart de la navigation, pas du contenu. (À
  confirmer à l'exécution : certains lecteurs annoncent le changement de `Frame`.)
- **Correctif** : après `Navigate`, émettre une notification UIA sur le titre et poser
  le focus sur le premier contrôle utile de la page (recherche pour Bibliothèque,
  premier bouton sinon). Exemple :

```csharp
var peer = FrameworkElementAutomationPeer.FromElement(title)
           ?? FrameworkElementAutomationPeer.CreatePeerForElement(title);
peer?.RaiseNotificationEvent(
    AutomationNotificationKind.Other,
    AutomationNotificationProcessing.ImportantMostRecent,
    title.Text, "PageTitle");
```

---

## P2 — gêne réelle ou à confirmer

| ID | Constat | Fichier:ligne | Impact | Correctif |
|---|---|---|---|---|
| P2-1 | Badge « Démonstration » à **4,49:1** pour un texte de 11 px (seuil 4,5). | `ShellPage.xaml:74-85` | Échec marginal de 1.4.3 ; le texte est le plus petit de l'UI et hors échelle typographique (11 px). | Utiliser `#9A441A` (5,09:1) ou passer le texte en `MemoTextBrush` sur `AccentSoft` (13,5:1) ; aligner sur 12 px. |
| P2-2 | Compteur du lecteur `#B0A79A` posé directement sur la photo, sans fond. | `StoryView.xaml:44-50` | Sur une photo claire, **2,11:1** ; illisible. Sur les barres noires, 8,17:1. | Ajouter un fond `#B3000000` (comme les badges de tuile) ou déplacer le compteur dans la barre basse. |
| P2-3 | Piste de progression à 31 % de blanc sur `#0E0D0C`. | `StoryView.xaml.cs:55-59` | **2,78:1** contre le fond, sous le seuil non-textuel de 3:1 ; la partie non lue se distingue mal. | Passer l'alpha à ~45 % (`#74FFFFFF`) ou utiliser `#5F594F` sur fond clair / `#B0A79A` sur fond sombre. |
| P2-4 | Le visuel de focus est un seul contour accent de 2 px, sans anneau secondaire. | `Themes/Controls.xaml:12-16` | Sur une photo ou un fond brun, l'anneau accent peut disparaître (accent sur noir = 3,37:1, sur photo variable). | Rétablir un anneau secondaire clair/sombre (`FocusVisualSecondaryBrush` blanc en clair, noir en sombre) ou `UseSystemFocusVisuals` avec les ressources système par défaut. |
| P2-5 | Aucun liseré de sélection de 2 px sur les tuiles, contrairement au DESIGN. | `Controls/MemoryTile.xaml:1-133` ; `LibraryPage.xaml:112-119` | L'état sélectionné repose sur le chrome par défaut du `GridViewItem`, peu visible sur une photo ; la sélection multiple est difficile à vérifier. | Ajouter un `Border` de 2 px accent piloté par `IsSelected` (via `ItemContainerStyle` ou une propriété `IsSelected` sur `MemoryTile`). |
| P2-6 | Panneau d'export à largeur fixe 320 px, textes sans retour à la ligne. | `StoryView.xaml:63-75` | À 200 %, « Export terminé » et le chemin du fichier sont coupés ; le panneau ne s'élargit pas. | `MaxWidth="320"` au lieu de `Width`, `TextWrapping="Wrap"` sur `ExportTitle`/`ExportStatus`. |
| P2-7 | Rangée de trois boutons dans une colonne de ~400 px. | `RecapsPage.xaml:123-127` | À 1024 px la rangée est déjà juste ; à 200 %, « Modifier la sélection » et « Enregistrer » débordent de la carte. | `Grid` à trois colonnes `Auto` avec `ColumnSpacing`, ou passage vertical sous un `AdaptiveTrigger`. |
| P2-8 | Colonnes fixes 110/60 dans le dialogue d'édition des récapitulatifs, sans troncature sur les deux premières. | `RecapsPage.xaml:9-27` | À 200 %, la date (`LocalDate`) et le type (`Kind`) se chevauchent ou sont coupés. | `TextTrimming="CharacterEllipsis"` + largeurs `Auto`/`*`, ou `MinWidth` et retour à la ligne. |
| P2-9 | La `NavigationView` reste ouverte à 240 px à 1024 px. | `ShellPage.xaml:7-15` (`IsPaneOpen="True"`), `Themes/Controls.xaml:81-89` | Le contenu perd 240 px alors que le DESIGN demande le mode compact à 1024 px ; aggrave P1-7. | `VisualState` à 1200 px : `Nav.IsPaneOpen = False` (compact 48 px) ; ou `PaneDisplayMode` adaptatif. Le style `MemoNavigationViewStyle` n'est d'ailleurs pas appliqué. |
| P2-10 | Échec de chargement de miniature : la tuile devient vide, sans message. | `Controls/MemoryTile.xaml.cs:94-98` | L'utilisateur ne sait pas si l'image est absente, en cours ou corrompue ; le lecteur d'écran n'a rien à lire. | Afficher `NoPreview` avec « Aperçu indisponible » et un nom accessible ; journaliser le chemin en diagnostic. |
| P2-11 | Filtre sans résultat : le focus reste dans la grille vide, l'état vide n'est pas annoncé. | `LibraryPage.xaml.cs:99-103` ; `LibraryPage.xaml:122-139` | Au clavier, on tabule dans une grille sans élément ; au lecteur d'écran, le message « Aucun souvenir ne correspond » n'est jamais lu. | `LiveSetting="Polite"` sur le titre de l'état vide et déplacement du focus sur « Tout afficher » quand le résultat devient vide. |
| P2-12 | Bouton destructif non distinct : « Supprimer » est un bouton discret, de même poids que « Renommer ». | `AlbumsPage.xaml:99-101` ; `AlbumsPage.xaml.cs:109-117` | Risque d'erreur ; le DESIGN demande un bouton destructif distinct à gauche. `MemoDangerBrush` n'est utilisé nulle part. | Style danger (fond `#A33A2A`, texte blanc) pour l'action primaire du dialogue, ou bouton `MemoDangerButtonStyle` ; garder « Annuler » par défaut. |
| P2-13 | Aucune validation ni état désactivé dans les dialogues de nom. | `AlbumsPage.xaml.cs:78-81` et `:95-98` ; `ImportsPage.xaml.cs:233` ; `SettingsPage.xaml.cs:354-357` | Un nom vide ferme le dialogue sans explication ; l'utilisateur croit avoir créé quelque chose. | `IsPrimaryButtonEnabled = false` tant que le texte est vide ; message d'erreur sous le champ, relié par `HelpText`, avec `LiveSetting="Assertive"`. |
| P2-14 | L'`InfoBar` de Réglages est en bas d'un long `ScrollViewer`. | `SettingsPage.xaml:196-199` | Après « Ouvrir », « Créer » ou « Sauvegarder », le message est hors écran ; l'utilisateur ne voit pas l'erreur ou le succès. | Remonter l'`InfoBar` sous le titre (comme `ImportsPage.xaml:19-22`) ou la fixer en bas de fenêtre. |
| P2-15 | Les listes n'ont pas de nom. | `AlbumsPage.xaml:43-46` (`AlbumList`), `MapPage.xaml:55-59` (`Places`), `ImportsPage.xaml:88-91` (`SelectionList`), `:169-172` (`Diagnostics`), `:186-189` (`History`), `RecapsPage.xaml.cs:206-212` (liste du dialogue), `SearchPage.xaml:45-50` (`Results`). | Le lecteur d'écran annonce « liste » sans objet ; dans un dialogue, la liste des candidats n'est pas identifiable. | `AutomationProperties.Name` ou `Header` sur chaque liste (« Albums », « Souvenirs situés », « Archives sélectionnées », « Diagnostics », « Historique des imports », « Souvenirs de la période »). |
| P2-16 | Le compteur de sélection n'est pas annoncé et « Masquer » n'a pas de confirmation. | `LibraryPage.xaml.cs:148-152` ; `LibraryPage.xaml:165-167` | En mode sélection, le nombre d'éléments choisis n'est pas lu ; « Masquer » retire des suggestions sans confirmation (contournable par « Réintégrer »). | `LiveSetting="Polite"` sur `SelectionLabel` ; `ContentDialog` de confirmation pour l'exclusion en masse, avec le nombre. |
| P2-17 | Espace et les flèches du lecteur dépendent du contrôle qui a le focus. | `StoryView.xaml.cs:401-430` | Si le focus est sur « Fermer », Espace ferme au lieu de mettre en pause ; sur « Exporter », il lance un export. Le raccourci annoncé n'est pas stable. | Vérifier `e.OriginalSource` : ignorer Espace si une `Button` a le focus, ou remplacer le handler global par des `KeyboardAccelerator` avec `ScopeOwner` et des boutons qui exposent leurs propres raccourcis. |
| P2-18 | Le lecteur continue d'avancer quand la fenêtre est désactivée. | `StoryView.xaml.cs:140-175` | Après un Alt+Tab, on revient sur un autre souvenir ; perte de place. | Mettre en pause sur `Window.Activated`/`Deactivated` (ou sur `LostFocus` du lecteur) et reprendre au retour, sans changer `_paused`. |

---

## P3 — finition et vérifications

| ID | Constat | Fichier:ligne | Impact | Correctif |
|---|---|---|---|---|
| P3-1 | Texte désactivé à **2,25:1** en clair (3,07:1 en sombre). | Thème WinUI par défaut ; `Themes/Controls.xaml` | WCAG exempte les contrôles inactifs, mais le libellé devient difficile à lire. | Assumer l'exemption ou relever l'opacité du texte désactivé à ~45 %. |
| P3-2 | Bordure de bouton secondaire à **1,35:1** sur le fond (1,45:1 sur surface). | `Themes/Controls.xaml:26-32` ; `Colors.xaml:20` | La limite du bouton secondaire est presque invisible ; le libellé suffit à l'identifier, mais la cible l'est moins. | Border `#B9AE9E` (~1,9:1) ou fond `SurfaceAlt` pour détacher le bouton ; vérifier au contraste réel. |
| P3-3 | Tailles de police 11 px (badge) et 15 px (légende du lecteur) hors échelle 12/13/14/16/20/28. | `ShellPage.xaml:81` ; `StoryView.xaml:66`, `:91` | Incohérence avec `DESIGN.md:40` ; le 11 px aggrave P2-1. | Aligner sur 12 et 16. |
| P3-4 | Le compteur « 1 / 12 » et les libellés « souvenir(s) » sont lus littéralement. | `StoryView.xaml.cs:85` ; `LibraryPage.xaml.cs:99-101` ; `ImportsPage.xaml.cs:111` | « un slash douze », « souvenir parenthèse s » : lecture heurtée. | « 1 sur 12 » ; pluriel calculé (`1 souvenir` / `2 souvenirs`) au lieu de `(s)`. |
| P3-5 | Le type affiché dans l'éditeur de récapitulatif est l'enum brut (`Video`, `Photo`). | `RecapsPage.xaml:17-21` | Mélange français/anglais à la lecture. | Exposer un libellé localisé (`Vidéo`/`Photo`) dans le modèle de vue. |
| P3-6 | Les boutons du lecteur font 40×40 px. | `StoryView.xaml:103-198` | Au-dessus du minimum souris de 32 px, mais dense pour 7 actions ; à 200 %, le glyphe de 16 px devient 32 px dans 40 px. | Passer à 44×44 (cible confort) et vérifier à 225 %. |
| P3-7 | Badges de tuile à 12 px sur une tuile de 180×240. | `MemoryTile.xaml:55-121` | À 200 %, le badge de date (à gauche) et le badge de durée (à droite) peuvent se rencontrer au centre. | Tester ; au besoin masquer le badge de date quand `TypeBadge` est visible, ou réduire à l'icône. |
| P3-8 | `MemoNavigationViewStyle` défini mais jamais appliqué. | `Themes/Controls.xaml:81-89` ; `ShellPage.xaml:7-15` | Les réglages de panneau ne sont pas centralisés ; `SelectionFollowsFocus` n'est pas explicite. | Appliquer le style à `Nav` ou supprimer le doublon. |
| P3-9 | Le titre de fenêtre reste « Memo » et aucune page n'expose de `LandmarkType`. | `MainWindow.xaml:6` ; pages | Le contexte de fenêtre est minimal ; la navigation par régions est absente. | `AutomationProperties.Name` sur les pages, `LandmarkType="Main"` sur le contenu. |
| P3-10 | Le `Counter` est seul en haut à droite de l'image, hors de la barre basse ; la légende est en bas. | `StoryView.xaml:44-50` | Voir P2-2 ; le compteur peut chevaucher un sujet clair et, à 200 %, se rapprocher des commandes. | Déplacer le compteur près de la légende ou dans un badge opaque. |

---

## Tableau de contraste

Calculs WCAG 2.2, arrondis à deux décimales. Les badges posés sur photo utilisent le
pire cas d'une photo blanche sous un voile noir à 70 % (`#B3000000` → `#4C4C4C`). Le
compteur du lecteur, lui, n'a **aucun fond** : sa valeur 2,11:1 est mesurée sur une
photo claire `#F4F1EC`. Les couleurs sont celles de `Themes/Colors.xaml` ; les seuils
sont 4,5:1 pour le texte normal, 3:1 pour le grand texte (≥ 18,66 px gras ou 24 px) et
les composants non textuels.

| Paire | Usage | Ratio | Seuil | Verdict |
|---|---|---:|---:|---|
| `#1D1A16` / `#F4F1EC` | Texte principal / fond clair | 15,38 | 4,5 | OK |
| `#1D1A16` / `#FCFAF6` | Texte principal / surface | 16,63 | 4,5 | OK |
| `#5F594F` / `#F4F1EC` | Texte secondaire / fond | 6,15 | 4,5 | OK |
| `#5F594F` / `#FCFAF6` | Texte secondaire / surface | 6,65 | 4,5 | OK |
| `#5F594F` / `#EAE5DC` | Texte secondaire / surface alt | 5,53 | 4,5 | OK |
| `#A64B1E` / `#F4F1EC` | Accent texte / fond | 5,11 | 4,5 | OK |
| `#A64B1E` / `#FCFAF6` | Accent texte / surface | 5,52 | 4,5 | OK |
| `#FFFFFF` / `#A64B1E` | Sur accent / bouton primaire | 5,76 | 4,5 | OK |
| `#A64B1E` / `#F2E0D1` | Badge « Démonstration » (11 px) | **4,49** | 4,5 | **Échec marginal** |
| `#A33A2A` / `#FCFAF6` | Danger / surface | 6,30 | 4,5 | OK |
| `#2F6B4F` / `#FCFAF6` | Succès / surface | 6,04 | 4,5 | OK |
| `#FFFFFF` / `#4C4C4C` | Badge durée/date sur photo claire | 8,59 | 4,5 | OK |
| `#F6D9A8` / `#4C4C4C` | Étoile favori sur photo claire | 6,30 | 4,5 | OK |
| `#D8D1C5` / `#F4F1EC` | Bordure / fond clair | 1,35 | 3,0 | Échec (si limite de contrôle) |
| `#D8D1C5` / `#FCFAF6` | Bordure / surface | 1,45 | 3,0 | Échec (si limite de contrôle) |
| `#F4EFE7` / `#141210` | Texte / fond sombre | 16,33 | 4,5 | OK |
| `#B0A79A` / `#141210` | Secondaire / fond sombre | 7,87 | 4,5 | OK |
| `#B0A79A` / `#1C1917` | Secondaire / surface sombre | 7,36 | 4,5 | OK |
| `#E39A5C` / `#141210` | Accent / fond sombre | 8,05 | 4,5 | OK |
| `#241407` / `#E39A5C` | Sur accent / bouton sombre | 7,68 | 4,5 | OK |
| `#E39A5C` / `#3A2A1C` | Accent / accent doux sombre | 5,92 | 4,5 | OK |
| `#E07A67` / `#1C1917` | Danger / surface sombre | 5,95 | 4,5 | OK |
| `#7FB79B` / `#1C1917` | Succès / surface sombre | 7,62 | 4,5 | OK |
| `#37312B` / `#141210` | Bordure / fond sombre | 1,46 | 3,0 | Échec (si limite de contrôle) |
| `#B0A79A` / `#0E0D0C` | Légende lecteur / fond photo | 8,17 | 4,5 | OK |
| `#F4EFE7` / `#5A5958` | Remplissage progression / piste | 6,11 | 4,5 | OK |
| `#5A5958` / `#0E0D0C` | Piste progression / fond | **2,78** | 3,0 | **Échec marginal** |
| `#B0A79A` / `#F4F1EC` | Compteur lecteur / photo claire | **2,11** | 4,5 | **Échec** |
| `#A64B1E` / `#FFFFFF` | Focus accent / blanc | 5,76 | 3,0 | OK |
| `#E39A5C` / `#000000` | Focus accent sombre / noir | 9,04 | 3,0 | OK |
| `#ACA9A5` / `#FCFAF6` | Texte désactivé clair (36 %) | 2,25 | 4,5 (exempté) | À noter |
| `#6A6662` / `#1C1917` | Texte désactivé sombre (36 %) | 3,07 | 4,5 (exempté) | À noter |
| `#1D1A16` / `#F2E0D1` | Navigation sélectionnée, clair | 13,50 | 4,5 | OK |
| `#F4EFE7` / `#3A2A1C` | Navigation sélectionnée, sombre | 12,01 | 4,5 | OK |

Synthèse : aucun problème sur le texte principal, les boutons accent, les badges de
tuile, les états danger/succès et le thème sombre. Les trois écarts à corriger sont
P2-1 (badge), P2-2 (compteur sur photo) et P2-3 (piste). Les bordures à 1,35–1,46:1
ne sont pas un échec de texte ; elles deviennent un risque seulement si elles sont la
seule limite perceptible d'un contrôle (P3-2).

---

## Vérifications manuelles à faire

Ces vérifications ne peuvent pas être remplacées par l'audit statique. Les faire sur
une build récente, dans les deux thèmes, avec une bibliothèque de démonstration et une
bibliothèque réelle.

### Narrateur (Windows) — 30 à 45 min

1. **Lancement** : ouvrir Memo. Noter ce qui est annoncé (titre, page, focus). Attendu :
   « Memo, Accueil » puis le titre de page ou le premier contrôle. Actuellement :
   probablement rien de plus que le titre de fenêtre.
2. **Navigation** : Tab dans les sept entrées + Réglages. Vérifier « Accueil »,
   « Bibliothèque », etc., puis l'état sélectionné.
3. **Bibliothèque** : parcourir les tuiles aux flèches. Attendu : « 12 mars 2019,
   photo, Lausanne, calque disponible ». Actuellement : nom vide ou fragment.
4. **Lecteur** : ouvrir une Story. Vérifier le nom des huit boutons (P0-1), l'annonce du
   changement d'élément (P0-4), l'état du son (P1-6), la progression (P1-10), Échap et
   le retour du focus (P1-1).
5. **Tabulation dans le lecteur** : depuis « Fermer », appuyer sur Tab plusieurs fois.
   Vérifier qu'on ne sort pas vers la coquille (P0-3).
6. **Sélection** : « Sélectionner », cocher deux tuiles à Espace, vérifier l'annonce du
   compteur, puis « Favori », « Masquer », « Album » (P2-16).
7. **Dialogues** : « Informations et date » (si accessible), création d'album, nouvel
   espace, création de bibliothèque. Vérifier titre, label du champ, message d'erreur,
   Échap et retour du focus (P1-5, P2-13).
8. **Import** : lancer un import, vérifier l'annonce de progression, du bilan et des
   diagnostics (P1-10, P2-15).
9. **Recherche** : taper une requête, vérifier l'annonce du nombre de résultats
   (P1-3) et l'état vide (P2-11).
10. **Carte** : vérifier que la carte n'est pas un arrêt muet et que la liste « Souvenirs
    situés » est nommée (P1-9, P2-15).

### 200 % de taille de texte — 20 min

Dans Windows, Paramètres → Accessibilité → Taille du texte = 200 % (puis 225 %).
Redémarrer Memo.

1. **Bibliothèque** : la rangée recherche + 4 filtres doit rester utilisable sans
   chevauchement ni scroll horizontal (P1-7).
2. **Réglages** : les libellés à gauche (220 px) et les contrôles à droite ne doivent
   pas se couper ; les `ToggleSwitch` longs passent-ils à la ligne ?
3. **Récapitulatifs** : les trois boutons de la carte et les colonnes 110/60 du dialogue
   d'édition (P2-7, P2-8).
4. **Lecteur** : la barre de sept boutons, le panneau d'export 320 px (P2-6), le
   compteur et la légende.
5. **Dialogues** : nom d'album, informations, licences ; aucun texte tronqué sans
   moyen de le lire.
6. **Tuiles** : badges date/durée à 200 % (P3-7).

### 1024 px et portrait 1040×1800 — 20 min

Redimensionner la fenêtre à 1024×768, puis 1040×1800 (portrait).

1. **Navigation** : la barre latérale passe-t-elle en compact 48 px ? (P2-9)
2. **Bibliothèque** : 3 colonnes de tuiles, filtres complets, pas de recouvrement
   (P1-7).
3. **Albums** : la liste de 260 px + la grille restent lisibles ; les actions
   « Lire / Renommer / Supprimer » ne se coupent pas.
4. **Récapitulatifs** : carte de 300 px + texte + trois boutons (P2-7).
5. **Recherche** : la rangée référence + bouton + statut (280 px + ~200 px) tient-elle ?
6. **Carte** : la colonne de 300 px et la carte 2* restent visibles ; la liste défile.
7. **Aucun scroll horizontal de page** ; les `GridView` se reflowent.

### Réduction des animations — 10 min

1. Windows → Paramètres → Accessibilité → Effets visuels → Animations désactivées.
2. Naviguer entre les pages : la transition d'entrée doit disparaître (P1-8).
3. Activer aussi « Réduire les animations » dans Memo, remettre les animations Windows,
   revérifier : le réglage applicatif doit suffire.
4. Vérifier qu'aucun clignotement n'apparaît pendant l'import ou la lecture (aucun
   storyboard dans le code ; à confirmer visuellement).

### Contraste sur captures réelles — 15 min

1. Prendre une capture du badge « Démonstration » en clair (P2-1) et mesurer avec
   Accessibility Insights ou un pipette + calcul.
2. Capturer le lecteur sur une photo claire : compteur en haut à droite (P2-2).
3. Capturer la progression sur une photo sombre et claire (P2-3).
4. Vérifier le focus sur une tuile photo claire et une tuile photo sombre (P2-4, P2-5).

### Clavier seul — 20 min

Sans souris : lancer, naviguer, rechercher (Ctrl+F — actuellement absent), ouvrir une
Story, pause/espace, suivant/précédent, son (M), calque (O), Échap, revenir sur la
tuile ; créer un album, le renommer, le supprimer ; corriger une date (actuellement
impossible, P1-2) ; ajouter un média à un album (Suppr — absent) ; vérifier le menu
contextuel (Maj+F10 — absent).

---

## Annexe — fichiers inspectés

- XAML : `App.xaml`, `MainWindow.xaml`, `ShellPage.xaml`, `StoryView.xaml`,
  `Themes/Colors.xaml`, `Themes/Controls.xaml`, `Themes/Typography.xaml`,
  `Controls/MemoryTile.xaml`, `Pages/{Home,Library,Albums,Recaps,Search,Map,Imports,Settings}Page.xaml`.
- Code-behind : les 13 fichiers `*.xaml.cs` correspondants, plus `Controls/MediaStage.cs`,
  `Controls/MemoMap.cs`, `Ui/Widgets.cs`.
- Références : `DESIGN.md` (palette, états, focus, mouvement, 1024 px),
  `PRODUCT.md` (honnêteté des états, langue), `docs/specification.md` (lecture,
  progression, raccourcis, dates).

Constats chiffrés : 1 `AutomationProperties.Name`, 0 `LiveSetting`, 0 `HeadingLevel`,
0 `LabeledBy`, 0 `HelpText`, 0 `AccessKey`, 0 `KeyboardAccelerator`, 0 `TabIndex`,
2 `IsTabStop` (dont un mort), 7 boutons icône sans nom dans le lecteur, 5 grilles de
tuiles sans nom accessible.

## Suites données (30 octobre 2026, commit de finition)

- Noms accessibles ajoutés aux 7 commandes du lecteur Story, au bouton de reprise, à la zone de lecture et aux tuiles mémoire (date, type, lieu, favori, calque, média manquant).
- Le lecteur désactive la coquille pendant la lecture et restaure le focus à la fermeture.
- Menu contextuel de la bibliothèque réellement câblé (clic droit) et accélérateur Ctrl+F pour la recherche.
- Barre de filtres passée sur deux lignes pour éviter le chevauchement à 1024 px et en portrait.
- Compteur du lecteur sur fond sombre, piste de progression plus contrastée, badge de démonstration en accent plein.
- Annonce polie du compteur d'éléments et nom du groupe de progression.

Restent à confirmer : parcours Narrateur complet, texte à 200 %, palier 1024 px, piège de focus strict du lecteur et annonce individuelle des segments de progression. L'audit initial reste la référence des constats non corrigés.
