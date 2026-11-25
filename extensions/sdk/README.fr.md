# Kit communautaire Lecteur Word · API 1

Le lecteur fournit la commande `LecteurWord --extension-tool-v1`. Elle fonctionne en terminal, avec le même validateur que le gestionnaire d’extensions, sans ouvrir de fenêtre. Remplacer `LecteurWord` par le chemin de l’exécutable. Une AppImage peut transmettre les mêmes arguments à son lanceur.

| Commande | Résultat |
| --- | --- |
| `init <catégorie> <id> <dossier-neuf>` | Crée un manifeste et une contribution modifiables |
| `pack <manifest.json> <payload.json> <sortie.lwext>` | Calcule l’empreinte, vérifie la contribution et écrit le paquet |
| `validate <paquet.lwext>` | Vérifie le paquet et indique s’il est compatible avec ce lecteur |
| `catalog <adresse-HTTPS-des-paquets/> <dossier-des-paquets> <index.json>` | Construit l’index local à partir des paquets et de leurs SHA-256 |
| `validate-catalog <index.json>` | Vérifie l’index local sans accéder au réseau |

La sortie est un objet JSON portant `protocol: "lecteur-word-extension-tool"`, `apiVersion: 1` et `result`. Les retours sont 0 pour la réussite, 64 pour l’usage incorrect, 65 pour les données invalides et 74 pour une erreur d’accès ou d’écriture. Les commandes préservent une destination existante. Le contrat du convertisseur DOCX est distinct.

## Licence et contributions

Le SDK, ses schémas, ses modèles et ses exemples sont sous [MIT](../LICENSE). Renseignez l’identifiant SPDX et la provenance des fichiers que vous publiez. Pour une contribution au projet, indiquez les composants repris et confirmez que vous pouvez proposer vos changements sous MIT. Les traductions intégrées suivent la licence du projet.

## Créer et vérifier

```sh
LecteurWord --extension-tool-v1 init navigation org.auteur.navigation mon-extension
# Compléter le nom, la description, l’auteur, le responsable et la licence.
mkdir paquets
LecteurWord --extension-tool-v1 pack mon-extension/manifest.json mon-extension/payload.json paquets/navigation-1.0.0.lwext
LecteurWord --extension-tool-v1 validate paquets/navigation-1.0.0.lwext
```

Un paquet `.lwext` est un ZIP UTF-8 contenant exactement `manifest.json` et `payload.json` à la racine. Les limites sont 128 Kio pour l’archive, 32 Kio pour le manifeste et la plupart des contributions, 256 Kio pour une traduction décompressée. Le schéma décrit la structure. Le validateur du lecteur contrôle aussi les empreintes, les intervalles, le contraste, les expressions et les actions autorisées. [Schéma du manifeste](schemas/manifest-v1.schema.json) · [Schéma du catalogue](schemas/catalog-v1.schema.json)

La version du paquet et les bornes de compatibilité utilisent `majeur.mineur.correctif`. La borne minimale est incluse et la maximale exclue. Une version applicative candidate précède la version finale correspondante. Une dépendance facultative indique un identifiant et un intervalle pour une extension installée et active. Les mises à jour doivent augmenter la version du paquet. Réexaminer la compatibilité à chaque modification des permissions et dépendances.

## Contributions et permissions

| Modèle `init` | Fonction | Déclaration de permission | Exemple |
| --- | --- | --- | --- |
| `theme` | `appearance.theme` | `[]` | [Forêt](../demo-theme/) |
| `language` | `appearance.translation` | `[]` | [Français canadien](../demo-language/) |
| `statistics` | `reading.metrics` | `["document.metrics.read"]` | [Statistiques de lecture](../demo-reading/) |
| `navigation` | `reading.navigation` | `["document.navigation"]` | [Navigation de lecture](../demo-navigation/) |
| `accessibility` | `reading.accessibility` | `[]` | [Lecture agrandie](../demo-accessibility/) |

Les cinq formats de contribution sont déclaratifs. Les palettes, traductions et réglages sont appliqués par le lecteur. Le kit empaqueté fournit les textes de départ bilingues dans `messages-v1.json`. Le processus de statistiques reçoit des compteurs avec un délai borné. Sur Linux x86-64, seccomp interdit ses ouvertures de fichiers et de sockets avant qu’il lise la demande. Les actions de navigation passent par les commandes et l’index du lecteur. Le paquet ne charge ni code natif ni script. Les contributions n’obtiennent ni le nom ni le texte du DOCX. Une version en erreur peut être mise en quarantaine et réactivée explicitement dans le gestionnaire. [API et isolation](../../docs/plateforme.md)

## Relire un catalogue intégré

Rechercher le message français ou anglais dans `messages-v1.json`. Depuis les sources, on peut le régénérer avec `python3 tools/export-ui-messages.py`. Modifier uniquement la valeur de sa clé dans `src/LecteurWord/Languages/<locale>.json` et conserver les paramètres tels que `{0:N0}`. Lancer `python3 tools/export-ui-messages.py --check-built-in` avant de proposer la correction.

Pour essayer la correction dans un lecteur installé, créer un projet de langue avec `--extension-tool-v1 init language`, puis copier le catalogue corrigé vers son `payload.json`. Donner au pack un nom et un identifiant distinctifs. Les commandes `pack` et `validate` préparent et vérifient le paquet. L’installer, l’activer et choisir son nom depuis le globe. Vérifier les libellés dans leur fenêtre, à taille réduite, et indiquer la locale et les clés corrigées dans la contribution. [Catalogues à relire](../../docs/plateforme.md#langues)

## Préparer une publication locale

1. Tester le paquet dans le gestionnaire, depuis le bouton puzzle à droite de la planète. Confirmer l’installation puis activer le paquet depuis sa fiche.
2. Mettre le paquet dans le dossier servi par votre propre serveur HTTPS de recette. Fournir l’adresse de base de ce dossier à `catalog`, puis servir l’index généré. Une version téléchargée doit correspondre au manifeste complet et au SHA-256 de l’index.
3. Dans **Catalogue…**, entrer l’adresse de l’index de confiance, puis **Actualiser le catalogue**. Vérifier la fiche dans **Découvrir** et télécharger ou installer.
4. Construire une version supérieure sous un nouveau nom, régénérer l’index dans un fichier neuf, le vérifier et remplacer l’index servi. Vérifier **Mises à jour**. Le paquet mis à jour attend une nouvelle activation.
5. Vérifier **Hors connexion** après téléchargement. L’index vérifié et les archives en cache restent exploitables. Une archive absente ne provoque aucune requête dans ce mode.

Le catalogue utilise HTTPS avec validation normale du certificat, sans redirections. L’intégrité compare les empreintes, le manifeste et les données du paquet. Elle dépend du catalogue approuvé. Le cache garde les 32 dernières archives par adresse d’index. Le lecteur ne fournit aucune adresse publique par défaut. Conserver les licences et la provenance, fournir les deux JSON, une description bilingue quand elle est disponible et les étapes d’essai reproductibles. Utiliser le canal de contribution désigné par le mainteneur lorsque celui-ci est communiqué.
