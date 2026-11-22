# Lecteur Word — lancement sous Linux

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

Après ENGINE-ABANDON-001, contrôler chaque nouvelle publication avec `check-independent-distribution.py` avant de préparer ou installer son lanceur. Ce contrôle inspecte le graphe de dépendances, les noms de fichiers et les signatures des assemblies propres ; il ne remplace pas la revue de provenance ni des licences. Les anciennes publications ElixirOffice conservées dans la qualification privée sont des archives, pas des candidats à installer. Le retrait ne modifie pas l'installation utilisateur existante ; une installation reste une opération distincte à autoriser.

Pour préparer les fichiers de lancement sans installer :

```bash
python3 tools/install-linux.py --from .local/publication-nouvelle \
  --prepare-dir .local/lanceur-a-verifier
```

`--data-home` et `--desktop-dir` permettent une installation personnelle dans d’autres dossiers. Les chemins d’exécutable contenant `=` ou `%` sont refusés dans ce profil de lanceur (le cas `%` a échoué avec GIO), sans créer une installation inutilisable. Les espaces, accents et caractères de citation testés sont correctement transmis sans shell intermédiaire.

Les clés de lancement suivent la [spécification Desktop Entry](https://specifications.freedesktop.org/desktop-entry/latest/exec-variables.html). `StartupWMClass` correspond à la [classe X11 de l’application Avalonia](https://api-docs.avaloniaui.net/docs/P_Avalonia_X11PlatformOptions_WmClass), afin de rattacher la fenêtre à son icône dans le bureau Linux.

## Périmètre vérifié

Publication autonome Linux x64 et lancement réel sous KDE/X11, icône native, validation du fichier desktop, transmission d’un nom de fichier avec espaces/accents, protection d’un lanceur existant, interface et À propos dans leurs tests ciblés. Aucun paquet DEB/RPM/AppImage ni autre plateforme n’est qualifié par cette installation. Les documents, rapports, caches et binaires restent exclus de Git ; seuls les dessins fournis et les sources d’intégration sont des ressources du projet.
