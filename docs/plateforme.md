# Plateforme Lecteur Word

## Contrats applicatifs

| Service | Entrées et sorties | Intégration |
| --- | --- | --- |
| `SessionRecoveryService` | Instantané de lecture, opération en cours, rapport d’incident | Le viewer publie des instantanés sur le thread UI. Une opération à risque est enregistrée avant son lancement. |
| `GitHubSupportService` | Métadonnées du build, rapport structuré, saisie de l’utilisateur, destinations GitHub | Le même texte peut être prévisualisé, copié ou enregistré. L’ouverture du navigateur passe une URL sans texte de rapport. |
| `ExtensionManagerService` | Manifeste, paquet, état d’activation, dépendances, réglages | Le lecteur applique des contributions déclaratives. Le processus de statistiques reçoit des compteurs sur demande. |

Les services utilisent des fichiers JSON bornés sous les répertoires XDG. Le visionneur possède le cycle de vie et le moteur possède l’inspection et la composition. L’API documentaire des extensions reçoit un instantané de compteurs. La sélection d’un thème d’extension concerne l’interface. Le chargement DOCX conserve son chemin de lecture existant.

## Session, préférences et rapports

- `$XDG_STATE_HOME/lecteur-word/session.json` contient le dernier instantané cohérent et l’opération en cours. Le premier lecteur qui prend le verrou de session prend en charge la récupération. La barre d’état signale les démarrages où le journal est indisponible.
- Un instantané est comparé toutes les trois secondes et après chaque ouverture. Les changements sont enregistrés par remplacement atomique. La fermeture normale sauvegarde l’état courant. Le menu **Aide et récupération** propose la dernière lecture même après une fermeture normale.
- L’instantané contient le chemin local, l’ancre de lecture continue ou la page et le décalage paginés, les deux zooms, le mode choisi et les préférences des volets. La lecture continue n’est restaurée que si elle figurait dans l’instantané et si le document l’autorise. Un document interrompu lors de sa première ouverture est proposé à partir du début. Après deux reprises interrompues, une ouverture explicite avec confirmation permet de réessayer.
- Les incidents sont des fichiers privés sous `$XDG_STATE_HOME/lecteur-word/incidents/`. Le rapport inclut l’heure, le build, l’étape, le système, le type d’erreur et les cadres de pile lorsqu’ils sont disponibles. Un arrêt détecté au prochain démarrage utilise le marqueur et indique l’absence de pile. Le rapport peut être consulté, copié, enregistré sous un nom neuf ou repris dans un brouillon GitHub.
- Le thème intégré reste dans `$XDG_CONFIG_HOME/lecteur-word/settings.json`. La langue est mémorisée dans `language.json`, la taille et les couleurs de l’interface dans `accessibility.json`. Les chemins XDG non définis ou relatifs utilisent les emplacements standard sous le dossier personnel.

## Support GitHub

Aucun dépôt public n’est présélectionné. Lorsque les destinations sont approuvées, renseigner `$XDG_CONFIG_HOME/lecteur-word/community.json` avec les noms `propriétaire/dépôt`. Une valeur `null` laisse la fonction distante indisponible. La destination des versions utilise celle du support si `releasesRepository` est omis.

```json
{
  "supportRepository": null,
  "releasesRepository": null,
  "catalogUrl": null
}
```

Dans le dépôt public de support désigné, les trois modèles de `.github/ISSUE_TEMPLATE/` couvrent les bogues, les améliorations et les extensions. Le formulaire ouvre GitHub avec le nom du modèle. L’utilisateur choisit ensuite ce qu’il colle et soumet. Le brouillon contient les informations publiques du build, le texte saisi et, sur demande, le diagnostic structuré. Le chemin du DOCX, le texte du document, les messages arbitraires d’exception et la pile locale brute ne font pas partie du brouillon. [Documentation GitHub sur la configuration des formulaires](https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests/configuring-issue-templates-for-your-repository)

## Paquet d’extension API 1

Un `.lwext` est un ZIP de 128 Kio maximum comprenant exactement `manifest.json` et `payload.json` à la racine, en UTF-8. Chaque entrée est limitée à 32 Kio après décompression. Pour une contribution linguistique, `payload.json` peut atteindre 256 Kio. Le gestionnaire lit les entrées en mémoire.

| Champ du manifeste | Règle |
| --- | --- |
| `id` | Nom stable en segments minuscules séparés par des points. Chaque segment commence par une lettre et peut contenir des chiffres et des tirets. |
| `name`, `description`, `author`, `license` | Textes pour la fiche. Utiliser de préférence un identifiant SPDX pour la licence. |
| `version` | Trois composants numériques sans zéros en tête. |
| `compatibility` | `minimum` inclus et `maximumExclusive` exclu, dans le même format. |
| `apiVersion`, `features`, `permissions` | L’API 1 accepte une contribution parmi les cinq décrites ci-dessous et exige ses permissions correspondantes. |
| `provenance` | `publisher` déclaré et `source` facultative en HTTPS. Ces renseignements identifient la provenance déclarée. |
| `payloadSha256` | SHA-256 des octets exacts de `payload.json`, sur 64 chiffres hexadécimaux. |
| `dependencies` | Tableau facultatif de huit prérequis au maximum. Chaque entrée contient `id`, `minimum` et `maximumExclusive`. |

Pour empaqueter les exemples, exécuter à la racine du dépôt.

```sh
mkdir -p .local/extensions
zip -j .local/extensions/demo-theme.lwext extensions/demo-theme/{manifest,payload}.json
zip -j .local/extensions/demo-reading.lwext extensions/demo-reading/{manifest,payload}.json
zip -j .local/extensions/demo-navigation.lwext extensions/demo-navigation/{manifest,payload}.json
zip -j .local/extensions/demo-accessibility.lwext extensions/demo-accessibility/{manifest,payload}.json
```

Le bouton puzzle ouvre le gestionnaire. Saisir ou coller le chemin absolu d’un paquet, utiliser **Parcourir…** ou déposer un unique fichier `.lwext` dans la fenêtre. Les trois actions ouvrent la même fiche de confirmation. L’entrée du chemin fonctionne également sous KDE virtuel lorsque le portail de fichiers ne rend pas la sélection. Chaque installation ou mise à jour valide la compatibilité et l’intégrité avant le remplacement atomique de la fiche privée sous `$XDG_DATA_HOME/lecteur-word/extensions/`. Une version nouvelle démarre désactivée. Les réglages sont repris lors d’une mise à jour si les fonctionnalités et permissions sont identiques. Activer les prérequis avant les extensions qui en dépendent. Si l’un devient incompatible, est désactivé, désinstallé ou mis en quarantaine, ses dépendants cessent de s’appliquer. Les cycles sont refusés à l’activation.

### Apparence et lecture

- **Forêt** déclare `appearance.theme` et des permissions vides. Son contenu propose des palettes `default` et `alternate`, chacune avec `surface` claire et `accent` foncé au format `#RRGGBB`. Activer le thème applique la palette à l’interface. Le menu **Configurer** choisit la variante. Les pages du document conservent leur rendu. Un seul thème d’extension s’applique à la fois. Les commandes de thème intégrées désactivent le thème d’extension.
- **Repères de lecture** déclare `reading.metrics` avec `document.metrics.read`. Avec un document ouvert, **Calculer les statistiques** exécute ses expressions et affiche les résultats. **Configurer** choisit de zéro à trois décimales. Cette extension montre comment ajouter un outil de consultation fondé sur la structure de lecture.

Les expressions utilisent la notation postfixée. Les opérandes disponibles sont `bodyBlocks`, `paragraphs`, `headings`, `tables`, `notes`, `pages` et des constantes décimales entre −1 000 000 et 1 000 000. Les titres sont comptés à part des paragraphes. Un nombre de pages nul indique une pagination indisponible. Les opérateurs binaires `+`, `-`, `*`, `/`, `min` et `max` consomment deux valeurs et en rendent une. Chaque paquet fournit 1 à 12 indicateurs, avec 1 à 32 opérandes ou opérations par indicateur. Le langage se limite à ces calculs déclaratifs. Le protocole d’exécution charge uniquement le processus fourni avec Lecteur Word.

### Langues

Le globe ouvre une liste défilante et filtrable sous l’icône. La liste affiche la langue du système, les catalogues intégrés et les packs d’extension installés et actifs. Le changement est immédiat. L’action **Installer d’autres langues** ouvre l’onglet de découverte du gestionnaire avec le filtre Langues. Une langue système prise en charge conserve sa locale régionale. Le français sert de repli pour une langue système sans équivalent intégré. Un pack communautaire incomplet reprend le texte français intégré pour une locale française, le texte anglais pour les autres locales.

Le français et l’anglais fournissent les libellés de référence. Les dix-huit catalogues suivants sont intégrés et attendent chacun une relecture par des locuteurs natifs.

| Catalogues | Langues à relire |
| --- | --- |
| DE, ES, IT, PT-PT, PT-BR, NL | Allemand, espagnol, italien, portugais du Portugal, portugais du Brésil, néerlandais |
| PL, CS, SV, DA, FI, UK, RU, TR | Polonais, tchèque, suédois, danois, finnois, ukrainien, russe, turc |
| JA, KO, ZH-CN, AR | Japonais, coréen, chinois simplifié, arabe |

La relecture porte notamment sur la terminologie, les diagnostics, les chaînes longues et les variantes régionales. Les vingt langues font l’objet d’un contrôle technique des paramètres et des libellés dans les fenêtres d’export et d’extensions en taille compacte. La recette vérifie les glyphes de l’interface en JA, KO, ZH-CN et AR avec les polices système du poste. L’arabe applique une disposition de droite à gauche. Le moteur DOCX utilise les propriétés du document et son propre profil de qualification.

Pour obtenir les textes sources pour les contributeurs, lancer `python3 tools/export-ui-messages.py`. Déclarer `appearance.translation` avec `permissions: []` dans le manifeste. Le fichier `payload.json` contient `culture` (locale reconnue par .NET, par exemple `fr-CA`), `name` (nom affiché dans la liste du globe) et `messages` (objet clé–texte, 2 400 entrées au plus, 1 600 caractères par texte). Une clé `ui.<empreinte>` utilise les 24 premiers chiffres hexadécimaux du SHA-256 UTF-8 de la paire français, saut de ligne, anglais. Elle reste identique tant que le sens et le libellé sources sont conservés. Le catalogue indique aussi les clés de formats et les codes `diagnostic.<code en minuscules>`.

Pour contrôler les clés et les paramètres des catalogues intégrés, lancer `python3 tools/export-ui-messages.py --check-built-in`.

Conserver les paramètres `{0}` ou `{0:N0}` des formats. Les formats numériques `N`, `P` et `F` acceptent zéro à trois décimales. Les compteurs utilisent les règles cardinales entières des locales intégrées. Les suffixes disponibles sont `zero`, `one`, `two`, `few`, `many` et `other` selon la locale. Après modification du fichier, recalculer `payloadSha256` avec `sha256sum payload.json`, mettre à jour le manifeste, empaqueter, installer, activer puis sélectionner la langue. Les codes, emplacements XML et rapports JSON du moteur continuent d’utiliser leur contrat technique. [Règles cardinales Unicode CLDR](https://www.unicode.org/cldr/charts/49/supplemental/language_plural_rules.html)

### Navigation et accessibilité

`reading.navigation` demande `document.navigation`. Le contenu donne une liste `commands` de dix objets au maximum. Chaque objet fournit `action`, `french`, `english`, `places` (`toolbar`, `sidebar`, ou les deux) et un `shortcut` facultatif de 1 à 9 (`Ctrl+Maj+chiffre`). Les actions disponibles sont `headings`, `sections`, `bookmarks`, `pages`, `marks`, `mark-position`, `history`, `back` et `forward`. L’exemple **Navigation de lecture** couvre ces neuf actions. Le lecteur résout les destinations dans son index courant. Les 50 derniers repères explicites sont associés au SHA-256 du document et restent privés. L’historique des déplacements est borné à 50 positions dans chaque direction pour le document courant.

`reading.accessibility` utilise `permissions: []`. Le contenu fournit `display` (`document`, `warm`, `dark` ou `high-contrast`) et `interfacePercent` (100, 115 ou 130). L’exemple **Lecture agrandie** active le contraste élevé à 115 %. Les réglages d’affichage sont appliqués par le lecteur et ne changent pas la composition exportée. Ces extensions déclarent des commandes et des réglages. Elles ne chargent aucun code, ni fichier distant, et n’obtiennent ni le texte du document ni son chemin.

| Raccourci | Action |
| --- | --- |
| Tab / Maj+Tab | Commande suivante / précédente |
| F6 / Maj+F6 | Zone de la fenêtre suivante / précédente |
| Entrée / Espace | Action ou sélection de la commande ciblée |
| F7, F8, F9 | Règle, éléments du document, navigation |
| Ctrl+O, Ctrl+F | Ouvrir un DOCX, rechercher |
| Alt+←, Alt+→ | Page précédente / suivante en vue paginée |
| Ctrl+plus, Ctrl+moins, Ctrl+0, Ctrl+9 | Agrandir, réduire, réinitialiser, ajuster à la largeur |
| Échap | Fermer le dialogue ou annuler l’opération en cours |

Sous Linux, les commandes Avalonia exposent leurs noms et rôles via les pairs d’automatisation. La lecture continue propose des contrôles de texte et des éléments de navigation accessibles. La vue paginée expose ses pages et leurs commandes, avec un rendu raster. L’utilisation d’un lecteur d’écran dépend également du pont AT-SPI d’Avalonia, de la session Linux et du lecteur d’écran.

### Protocole du processus de lecture

Le lecteur lance son exécutable avec `--extension-worker-v1` et échange un objet JSON UTF-8 par flux, terminé par EOF. Le processus reçoit `apiVersion`, `requestId` (GUID sans tirets), `document` (les six compteurs entiers) et `contribution` (les expressions validées). Il renvoie la même version et le même identifiant, avec `results` (liste ordonnée d’objets `label` et `value`) ou `error` (`arithmetic`). Les flux d’entrée et de sortie sont bornés et le processus dispose de six secondes. Son environnement est vidé et le port de diagnostic .NET est désactivé. Sous Linux x86-64, le noyau applique un filtre seccomp à tous ses fils avant la lecture du flux. Le filtre interdit l’ouverture de fichiers, les sockets et la création de processus. Si le noyau refuse le filtre, le calcul est indisponible. Une erreur de calcul, un arrêt du processus ou un dépassement de délai entraîne un diagnostic avec identifiant et version, puis la mise en quarantaine de cette version. Un redémarrage après interruption permet de désactiver les extensions pour la session.

## Catalogue communautaire

Le bouton puzzle se trouve immédiatement à droite de la planète. Le gestionnaire propose les onglets **Installées**, **Découvrir** et **Mises à jour**, une recherche commune et cinq filtres de catégorie. **Catalogue…** configure une adresse HTTPS approuvée et le mode hors connexion. En mode connecté, les demandes partent lors d’une actualisation ou d’un téléchargement demandé par l’utilisateur. En mode hors connexion, l’index vérifié et les archives téléchargées sont relus depuis le cache XDG. Chaque archive est à nouveau vérifiée avant installation. [Kit de création et de validation](../extensions/sdk/README.fr.md) · [English kit](../extensions/sdk/README.en.md)

Une fois le service choisi, `catalogUrl` peut pointer vers un index HTTPS au format suivant. Un manifeste complet apparaît à la place de chaque objet `manifest`.

```json
{
  "schema": 1,
  "entries": [
    {
      "manifest": {},
      "packageUrl": "<URL HTTPS approuvée du paquet>",
      "packageSha256": "<SHA-256 des octets du paquet ZIP>"
    }
  ]
}
```

Le lecteur vérifie l’index à la demande dans le gestionnaire. Il affiche la dernière version compatible de chaque identifiant. Les catalogues et les paquets sont lus sous budget, sans redirections. Avant installation, le lecteur compare le SHA-256 du ZIP ainsi que le manifeste exact avec la fiche revue par l’utilisateur. L’intégrité est relative au catalogue HTTPS choisi. La configuration livrée laisse le service distant à définir.

## Installation et versions

Le processus de lecture est un mode du même exécutable. Il suit donc la version lancée par le raccourci Linux et ne demande pas de composant runtime supplémentaire. Les préférences, l’état de session et les extensions se trouvent dans les répertoires XDG de l’utilisateur, indépendamment du répertoire de la version exécutée. Au démarrage le lecteur revalide les versions installées avant application. Une fiche devenue incompatible reste consultable et désinstallable depuis le gestionnaire.

## Rejeu ciblé

La recette de développement locale utilise des entrées synthétiques et un transport HTTP de test. La recette graphique sous KDE utilise une fenêtre Avalonia X11. Le cas ciblé `platform-kde` attend les paquets `demo-theme.lwext`, `demo-reading.lwext` et `demo-navigation.lwext` sous `LECTEUR_WORD_PLATFORM_QA`, ainsi que XTEST (`libXtst.so.6`), `xdotool` et `import`. Il vérifie la recherche et la sélection de langue au clavier puis le passage entre zones avec F6 et Maj+F6. Sous XWayland, utiliser une session de recette dédiée capable d’accepter ces entrées. Les captures restent dans le répertoire de recette local.
