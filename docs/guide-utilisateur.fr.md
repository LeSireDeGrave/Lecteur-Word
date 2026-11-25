# Lecteur Word sous Linux

## Installer, mettre à jour et désinstaller

Les distributions de préparation V1 ciblent Linux x86-64 avec glibc. Le runtime .NET est fourni. Le système fournit les bibliothèques de base, ICU, OpenSSL, Fontconfig et, pour la fenêtre, les bibliothèques X11 et OpenGL/EGL. Une session Wayland utilise XWayland pour cette édition. Consulter le bilan de la livraison pour les environnements effectivement testés.

Dans le dossier de livraison, vérifier les fichiers avec `sha256sum -c SHA256SUMS`. Les empreintes vérifient l’intégrité par rapport à la liste reçue.

| Format | Installation ou lancement | Désinstallation |
| --- | --- | --- |
| DEB | `sudo apt install ./lecteur-word-….deb` | `sudo apt remove lecteur-word` |
| RPM | `sudo dnf install ./lecteur-word-….rpm` | `sudo dnf remove lecteur-word` |
| AppImage | `chmod u+x ./LecteurWord-….AppImage`, puis lancer le fichier | Fermer le lecteur puis supprimer le fichier |
| TAR.GZ | Extraire et exécuter `./app/LecteurWord` dans le dossier extrait | Fermer le lecteur puis supprimer le dossier extrait |

Remplacer les noms abrégés par les noms des fichiers reçus. Mettre à jour un DEB ou un RPM avec la même commande d’installation et le paquet plus récent. Pour l’AppImage, fermer le lecteur et remplacer le fichier. Sur un système sans FUSE utilisable, lancer l’AppImage avec `--appimage-extract-and-run`.

Pour intégrer une archive au menu des applications sans privilèges administrateur, exécuter `python3 install-linux.py --from ./app --install --no-desktop-shortcut` depuis le dossier extrait. Omettre la dernière option pour ajouter aussi un raccourci sur le bureau XDG. Sur une nouvelle archive, répéter la commande pour mettre à jour. Fermer le lecteur puis exécuter `python3 install-linux.py --uninstall` pour retirer l’installation gérée. Pour enregistrer une AppImage sans intégrateur, extraire le fichier avec `--appimage-extract`, puis utiliser `python3 squashfs-root/usr/share/lecteur-word/install-linux.py --from squashfs-root/usr/lib/lecteur-word --install --no-desktop-shortcut`.

Les installations DEB, RPM et TAR.GZ enregistrées proposent **Ouvrir avec Lecteur Word** pour les DOCX. L’association par défaut reste celle choisie dans le bureau. Les mises à jour et les désinstallations préservent les préférences, les extensions et le journal sous les répertoires XDG `lecteur-word`. Ces données privées peuvent être supprimées manuellement après fermeture si elles ne doivent pas être conservées.

## Ouvrir et parcourir un document

Ouvrir un DOCX depuis le menu contextuel du gestionnaire de fichiers, en le déposant sur le lecteur, avec le bouton dossier ou avec **Ctrl+O**. Le glisser-déposer accepte un DOCX local à la fois. Le lecteur consulte le fichier original en lecture seule. Les noms contenant des espaces et des accents sont acceptés.

La barre d’outils donne accès à la lecture continue et, quand la composition est disponible, à la page simple, la double page et l’ajustement à la largeur. Le volet de navigation regroupe miniatures, titres, signets, notes, éléments d’en-tête et de pied, et recherche (**Ctrl+F**). Les appels de notes vérifiés ouvrent leur contenu. Le volet **Éléments du document** permet de localiser les éléments et de consulter les détails conservés.

| Raccourci | Action |
| --- | --- |
| Tab / Maj+Tab | Parcourir les commandes |
| F6 / Maj+F6 | Passer à la zone suivante / précédente |
| F7 / F8 / F9 | Règle / éléments / navigation |
| Alt+← / Alt+→ | Page précédente / suivante en mode paginé |
| Ctrl+plus / Ctrl+moins | Modifier le zoom du document |
| Ctrl+0 / Ctrl+9 | Réinitialiser le zoom / ajuster à la largeur |

## Affichage, accessibilité et extensions

Les boutons de la barre d’outils sélectionnent Système, Clair, Chocolat et Crème, Bleu Microsoft ou Sombre. Les fenêtres internes et leurs menus suivent le thème dès la bascule. Les thèmes d’extension utilisent les mêmes ressources. Les sélecteurs natifs suivent le thème du bureau.

Le globe ouvre la liste des langues. Au clavier, atteindre le globe avec **Tab** puis appuyer sur **Entrée**. Après avoir saisi une recherche, utiliser **Flèche bas** pour atteindre le premier résultat, **Tab** pour parcourir les choix et **Entrée** pour confirmer. Chaque langue apparaît sous son propre nom et une coche marque la sélection actuelle. **Installer d’autres langues** ouvre la catégorie Langues du gestionnaire d’extensions. Le choix s’applique immédiatement. Les préférences d’accessibilité de l’en-tête règlent la taille de l’interface et les couleurs de lecture. Le zoom du document et les couleurs des exports gardent leurs propres réglages.

Le bouton puzzle ouvre le gestionnaire. Indiquer le chemin d’un fichier `.lwext`, le déposer ou choisir **Parcourir**, puis vérifier sa fiche, l’installer et l’activer. Les nouvelles versions sont installées désactivées. Le gestionnaire montre les permissions, les dépendances et les réglages disponibles. Une extension mise en quarantaine peut être réactivée après confirmation. Le catalogue distant dépend de la configuration approuvée.

En lecture continue, les commandes et le texte sont exposés par le pont d’accessibilité Linux AT-SPI lorsque la session l’active. Le rendu de la vue paginée est une image, accompagnée de commandes et de noms de page accessibles.

Le bouton **A soulignée**, près des thèmes, règle la **Couleur du texte de lecture**. Le mode automatique adapte le contraste au fond actif. Les couleurs originales, blanc, noir, crème et une teinte personnalisée sont disponibles. Le choix est mémorisé et s’applique en lecture continue, en page simple et en double page. Les illustrations et les couleurs des exports gardent leur rendu propre.

## Exporter

**DOCX → PDF / ODT / EPUB 3 / NOVA**

Ouvrir **Exporter**, choisir un format et un fichier de destination, puis lire le précontrôle. Les réserves décrivent les adaptations, les conversions et les limites de conservation. Le lecteur demande une confirmation pour remplacer un fichier existant.

Le mode compatible est sélectionné à chaque ouverture de la fenêtre. **Options avancées** donne accès à **Exiger la conservation intégrale**, ainsi qu’aux détails de conservation et à leur copie.

| Format | Usage |
| --- | --- |
| PDF | Partager et imprimer. Choisir la plage de pages et le réglage d’image. |
| ODT | Modifier librement dans un traitement de texte compatible OpenDocument. |
| EPUB 3 | Lire sur une liseuse et publier. Une couverture peut être ajoutée. |
| NOVA | Écrire dans OpusNova. Le projet suit le schéma NOVA v27. |

L’édition et la pagination Microsoft Word dépendent notamment des polices et des fonctions du document. Le lecteur propose les vues et les exports que son profil permet et indique les adaptations ou refus. Les **Propriétés du document** présentent les polices demandées, réellement utilisées, absentes ou substituées. L’outil de substitutions permet d’examiner certains glyphes historiques. Une substitution ou une version différente d’une police peut modifier les lignes et les pages.

## Reprise de session et signalement

Après une interruption, le lecteur peut proposer la dernière position de lecture. Depuis l’aide, choisir **Reprendre la dernière lecture** pour retrouver une session enregistrée. Le rapport d’incident est consultable et peut être copié ou enregistré localement. Avant d’envoyer un signalement, relire le brouillon et choisir les informations à partager. La fenêtre **À propos** présente la version, les quatre conversions et la licence MIT. Déplier **Informations techniques** pour consulter le commit, le moteur et les niveaux de compatibilité. **Copier les informations techniques** fournit la référence complète, même avec la section fermée.

## Licences

Les développements originaux sont sous licence MIT. Les distributions contiennent le texte `LICENSE` et les notices propres aux polices, dépendances et composants du lanceur. La fenêtre **À propos** donne accès au **Texte intégral de la licence MIT** et à **Composants tiers et notices légales**.

This product includes DNG technology under license by Adobe Systems Incorporated.

## Convertisseur autonome

Extraire séparément l’archive CLI. Conserver ensemble l’exécutable et les bibliothèques de son dossier. Le CLI se lance sans serveur graphique.

```sh
./lecteur-word-cli capabilities
./lecteur-word-cli convert --contract-version 1 --format nova --nova-version 27 \
  --source 'Chapitre révisé.docx' --output 'Chapitre révisé.nova'
```

Les formats CLI sont `pdf`, `odt`, `epub3` et `nova`. Les événements JSON décrivent la progression, le précontrôle et le résultat. Le retour 0 indique une sortie publiée. Les retours 64, 65, 70, 74 et 130 signalent respectivement un usage incorrect, un refus de contenu ou de profil, une erreur interne, une erreur d’accès/écriture et une annulation. Utiliser `--overwrite` pour autoriser un remplacement et **Ctrl+C** pour annuler. Une application tierce peut configurer le chemin absolu de l’exécutable puis interroger `capabilities` avant de l’appeler.
