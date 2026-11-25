# Lecteur Word — lancement sous Linux

Pour l’installation depuis un AppImage, un DEB, un RPM ou une archive, suivre le guide utilisateur. Le guide de fabrication décrit les dépendances système et les points à qualifier avant diffusion. [Guide français](guide-utilisateur.fr.md) · [English guide](user-guide.en.md) · [Préparation de la distribution](distribution-linux.md)

L’installation personnelle crée « Lecteur Word » dans les applications et un raccourci avec son icône sur le bureau XDG (ici `~/Bureau`). Le programme publié est copié sous `~/.local/share/lecteur-word/versions/` ; le lien `current` désigne la version active. Le lanceur reste au même endroit lors des mises à jour et ne dépend pas du répertoire de développement.

L’icône de fenêtre, la barre des tâches et l’en-tête utilisent le dessin fourni par le propriétaire. La fenêtre À propos affiche son logo. Les originaux dans `docs/maquettes/` sont préservés ; les ressources de l’application sont dans `src/LecteurWord/Assets/`. Le PNG d’icône de 256 × 256 est une image déjà présente dans le fichier ICO, extraite sans retouche ni redimensionnement. Le grand logo PNG et l’ICO sont copiés octet pour octet.

## Construire et installer une mise à jour

Depuis la racine du projet, publier dans un **nouveau dossier** (ne pas écraser une application en cours d’exécution) :

```bash
.local/dotnet/dotnet publish src/LecteurWord/LecteurWord.csproj \
  --no-restore -c Release -r linux-x64 --self-contained true \
  -p:PublishTrimmed=false -m:1 -o .local/publication-nouvelle
python3 tools/check-independent-distribution.py --directory .local/publication-nouvelle
python3 tools/install-linux.py --from .local/publication-nouvelle --install
```

Le script ne requiert pas de droits administrateur. Il copie les binaires dans une nouvelle version puis bascule le lien actif ; les versions antérieures et les fenêtres déjà ouvertes restent intactes. Il refuse d’écraser un lanceur différent ou une installation non gérée. Il actualise les caches de bureau disponibles, sans changer explicitement l’éditeur DOCX par défaut. Le type DOCX est déclaré pour permettre « Ouvrir avec Lecteur Word » ; le lanceur accepte un fichier local à la fois.

Après ENGINE-ABANDON-001, contrôler chaque nouvelle publication avec `check-independent-distribution.py` avant de préparer ou installer son lanceur. Ce contrôle inspecte le graphe de dépendances, les noms de fichiers et les signatures des assemblies propres. La revue de provenance et des licences reste distincte. Les anciennes publications ElixirOffice restent des archives de qualification.

## Suivre les publications locales depuis l’icône KDE

Cette option personnelle utilise le raccourci `lecteur-word.desktop` déjà épinglé. Pour l’activer, créer `.local/desktop-updates/enabled` et ajouter `--follow-updates .local/desktop-updates` à la commande d’installation d’une version vérifiée ci-dessus. Le fichier d’activation et les binaires restent dans `.local`.

Une publication autonome Release `linux-x64` vérifie alors la distribution dans une copie privée avant de la rendre disponible au prochain lancement. Une compilation simple ou une publication avec `-p:LecteurWordDesktopUpdates=false` ne déclenche pas cette étape. Le lanceur peut toujours démarrer la version installée si la copie locale est indisponible. La nouvelle publication s’applique à la prochaine ouverture de l’application. Une fenêtre déjà ouverte continue d’exécuter sa version.

Pour préparer les fichiers de lancement sans installer :

```bash
python3 tools/install-linux.py --from .local/publication-nouvelle \
  --prepare-dir .local/lanceur-a-verifier
```

`--data-home` et `--desktop-dir` permettent une installation personnelle dans d’autres dossiers. Les chemins d’exécutable contenant `=` ou `%` sont refusés dans ce profil de lanceur (le cas `%` a échoué avec GIO), sans créer une installation inutilisable. Les espaces, accents et caractères de citation testés sont correctement transmis sans shell intermédiaire.

Les clés de lancement suivent la [spécification Desktop Entry](https://specifications.freedesktop.org/desktop-entry/latest/exec-variables.html). `StartupWMClass` correspond à la [classe X11 de l’application Avalonia](https://api-docs.avaloniaui.net/docs/P_Avalonia_X11PlatformOptions_WmClass), afin de rattacher la fenêtre à son icône dans le bureau Linux.

## Désinstaller une installation utilisateur

Fermer le lecteur, puis lancer `python3 tools/install-linux.py --uninstall`. Le script retire uniquement les versions et les lanceurs de l’installation qu’il gère. Les options `--no-desktop-shortcut`, `--desktop-dir` et `--data-home` permettent d’adapter l’intégration à la session. Lors d’une désinstallation, reprendre `--data-home` s’il avait été précisé à l’installation. Les préférences, l’état de session et les extensions restent dans les répertoires XDG.
