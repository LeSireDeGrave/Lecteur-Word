# Plateforme Lecteur Word

## Contrats applicatifs

| Service | Entrées et sorties | Intégration |
| --- | --- | --- |
| `SessionRecoveryService` | Instantané de lecture, opération en cours, rapport d’incident | Le viewer publie des instantanés sur le thread UI. Une opération à risque est enregistrée avant son lancement. |
| `GitHubSupportService` | Métadonnées du build, rapport structuré, saisie de l’utilisateur, destinations GitHub | Le même texte peut être prévisualisé, copié ou enregistré. L’ouverture du navigateur passe une URL sans texte de rapport. |
| `ExtensionManagerService` | Manifeste et paquet, état d’activation, réglage, requête documentaire versionnée | Le viewer traduit les contributions d’apparence en couleurs d’interface et transmet des compteurs au processus de lecture sur demande. |

Les services utilisent des fichiers JSON bornés sous les répertoires XDG. Le visionneur possède le cycle de vie et le moteur possède l’inspection et la composition. L’API documentaire des extensions reçoit un instantané de compteurs. La sélection d’un thème d’extension concerne l’interface. Le chargement DOCX conserve son chemin de lecture existant.

## Session, préférences et rapports

- `$XDG_STATE_HOME/lecteur-word/session.json` contient le dernier instantané cohérent et l’opération en cours. Le premier lecteur qui prend le verrou de session prend en charge la récupération. La barre d’état signale les démarrages où le journal est indisponible.
- Un instantané est comparé toutes les trois secondes et après chaque ouverture. Les changements sont enregistrés par remplacement atomique. La fermeture normale sauvegarde l’état courant. Le menu **Aide et récupération** propose la dernière lecture même après une fermeture normale.
- L’instantané contient le chemin local, l’ancre de lecture continue ou la page et le décalage paginés, les deux zooms, le mode choisi et les préférences des volets. La lecture continue n’est restaurée que si elle figurait dans l’instantané et si le document l’autorise. Un document interrompu lors de sa première ouverture est proposé à partir du début. Après deux reprises interrompues, une ouverture explicite avec confirmation permet de réessayer.
- Les incidents sont des fichiers privés sous `$XDG_STATE_HOME/lecteur-word/incidents/`. Le rapport inclut l’heure, le build, l’étape, le système, le type d’erreur et les cadres de pile lorsqu’ils sont disponibles. Un arrêt détecté au prochain démarrage utilise le marqueur et indique l’absence de pile. Le rapport peut être consulté, copié, enregistré sous un nom neuf ou repris dans un brouillon GitHub.
- Le thème intégré reste dans `$XDG_CONFIG_HOME/lecteur-word/settings.json`. Les chemins XDG non définis ou relatifs utilisent les emplacements standard sous le dossier personnel.

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

Un `.lwext` est un ZIP de 128 Kio maximum comprenant exactement `manifest.json` et `payload.json` à la racine, en UTF-8. Chaque entrée est limitée à 32 Kio après décompression. Le gestionnaire lit les entrées en mémoire.

| Champ du manifeste | Règle |
| --- | --- |
| `id` | Nom stable en segments minuscules séparés par des points. Chaque segment commence par une lettre et peut contenir des chiffres et des tirets. |
| `name`, `description`, `author`, `license` | Textes pour la fiche. Utiliser de préférence un identifiant SPDX pour la licence. |
| `version` | Trois composants numériques sans zéros en tête. |
| `compatibility` | `minimum` inclus et `maximumExclusive` exclu, dans le même format. |
| `apiVersion`, `features`, `permissions` | API 1 exige une contribution parmi les deux décrites ci-dessous et les permissions correspondantes. |
| `provenance` | `publisher` déclaré et `source` facultative en HTTPS. Ces renseignements identifient la provenance déclarée. |
| `payloadSha256` | SHA-256 des octets exacts de `payload.json`, sur 64 chiffres hexadécimaux. |

Pour compiler les deux paquets d’exemple sans reconstruire le lecteur, exécuter à la racine du dépôt :

```sh
mkdir -p .local/extensions
zip -j .local/extensions/demo-theme.lwext extensions/demo-theme/{manifest,payload}.json
zip -j .local/extensions/demo-reading.lwext extensions/demo-reading/{manifest,payload}.json
```

Le bouton puzzle ouvre le gestionnaire. Choisir **Installer un paquet local…**, contrôler sa fiche, puis activer l’extension. Chaque installation ou mise à jour valide la compatibilité et l’intégrité avant le remplacement atomique de la fiche privée sous `$XDG_DATA_HOME/lecteur-word/extensions/`. Une version nouvelle démarre désactivée. Les réglages sont repris lors d’une mise à jour si les fonctionnalités et permissions sont identiques.

### Apparence et lecture

- **Forêt** déclare `appearance.theme` et des permissions vides. Son contenu propose des palettes `default` et `alternate`, chacune avec `surface` claire et `accent` foncé au format `#RRGGBB`. Activer le thème applique la palette à l’interface. Le menu **Configurer** choisit la variante. Les pages du document conservent leur rendu. Un seul thème d’extension s’applique à la fois. Les commandes de thème intégrées désactivent le thème d’extension.
- **Repères de lecture** déclare `reading.metrics` avec `document.metrics.read`. Avec un document ouvert, **Calculer les statistiques** exécute ses expressions et affiche les résultats. **Configurer** choisit de zéro à trois décimales. Cette extension montre comment ajouter un outil de consultation fondé sur la structure de lecture.

Les expressions utilisent la notation postfixée. Les opérandes disponibles sont `bodyBlocks`, `paragraphs`, `headings`, `tables`, `notes`, `pages` et des constantes décimales entre −1 000 000 et 1 000 000. Les titres sont comptés à part des paragraphes. Un nombre de pages nul indique une pagination indisponible. Les opérateurs binaires `+`, `-`, `*`, `/`, `min` et `max` consomment deux valeurs et en rendent une. Chaque paquet fournit 1 à 12 indicateurs, avec 1 à 32 opérandes ou opérations par indicateur. Le langage ne dispose d’aucun accès aux fichiers, réseau, processus ou API Avalonia. L’API 1 accepte les contributions de thème et de statistiques. Les packs de langues, les commandes de navigation et les intégrations d’accessibilité demandent des points d’extension additionnels. Le protocole d’exécution ne charge aucun binaire tiers.

Un pack de langue pourra déclarer une locale et des traductions indexées par les clés stables de l’interface, avec repli sur la langue intégrée. Une contribution de navigation pourra demander un déplacement vers un identifiant de bloc fourni par la projection en lecture seule. Le lecteur vérifiera l’identifiant dans le document courant et effectuera le déplacement sur action de l’utilisateur. Les contributions d’accessibilité pourront proposer des réglages d’affichage ou de consultation déclarés au lecteur. Les trois contrats pourront reprendre la validation du manifeste, des permissions et de la compatibilité.

### Protocole du processus de lecture

Le lecteur lance son exécutable avec `--extension-worker-v1` et échange un objet JSON UTF-8 par flux, terminé par EOF. Le processus reçoit `apiVersion`, `requestId` (GUID sans tirets), `document` (les six compteurs entiers) et `contribution` (les expressions validées). Il renvoie la même version et le même identifiant, avec `results` (liste ordonnée d’objets `label` et `value`) ou `error` (`arithmetic`). Les flux d’entrée et de sortie sont bornés et le processus dispose de six secondes. Le processus n’a besoin ni de l’emplacement du document ni de son texte. Une erreur, un arrêt du processus ou un dépassement de délai entraîne un diagnostic avec identifiant et version, puis la mise en quarantaine de cette version. Un redémarrage après interruption permet de désactiver les extensions pour la session.

## Catalogue communautaire

Une fois le service choisi, `catalogUrl` peut pointer vers un index HTTPS au format suivant. Un manifeste complet apparaît à la place de chaque objet `manifest`.

```json
{
  "schema": 1,
  "entries": [
    {
      "manifest": {},
      "packageUrl": "https://support.example.org/paquets/exemple.lwext",
      "packageSha256": "<SHA-256 des octets du paquet ZIP>"
    }
  ]
}
```

Le lecteur vérifie l’index à la demande dans le gestionnaire. Il affiche la dernière version compatible de chaque identifiant. Les catalogues et les paquets sont lus sous budget, sans redirections. Avant installation, le lecteur compare le SHA-256 du ZIP ainsi que le manifeste exact avec la fiche revue par l’utilisateur. L’intégrité est relative au catalogue HTTPS choisi. La configuration livrée laisse le service distant à définir.

## Installation et versions

Le processus de lecture est un mode du même exécutable. Il suit donc la version lancée par le raccourci Linux et ne demande pas de composant runtime supplémentaire. Les préférences, l’état de session et les extensions se trouvent dans les répertoires XDG de l’utilisateur, indépendamment du répertoire de la version exécutée. Au démarrage le lecteur revalide les versions installées avant application. Une fiche devenue incompatible reste consultable et désinstallable depuis le gestionnaire.

## Rejeu ciblé

La recette de développement locale utilise des entrées synthétiques et un transport HTTP de test. La recette graphique utilise une fenêtre Avalonia X11 sous KDE. Il attend les deux paquets de démonstration sous `LECTEUR_WORD_PLATFORM_QA`, un pointeur et un clavier XTEST utilisables, ainsi que `xdotool` et `import` pour les captures. Sous XWayland, utiliser une session de recette dédiée capable d’accepter ces entrées. Les captures restent dans le répertoire de recette local.
