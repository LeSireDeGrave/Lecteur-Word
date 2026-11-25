# Préparation de la distribution Linux

## Fabrication locale

La distribution utilise les deux publications autonomes Release `linux-x64` de Lecteur Word. Le lecteur et le CLI exposent leur identité respective avec `LecteurWord --build-info` et `lecteur-word-cli capabilities`. Les deux affichent la version du moteur, le commit, l’état des changements locaux, le runtime et les versions des contrats. Le format public de la première version reste à décider.

```sh
.local/dotnet/dotnet publish src/LecteurWord/LecteurWord.csproj -c Release -m:1 \
  -r linux-x64 --self-contained -p:DebugType=none \
  -p:LecteurWordDesktopUpdates=false -o .local/release/viewer
.local/dotnet/dotnet publish src/LecteurWord.Cli/LecteurWord.Cli.csproj -c Release -m:1 \
  -r linux-x64 --self-contained -p:DebugType=none -o .local/release/cli
python3 tools/package-linux.py --viewer .local/release/viewer --cli .local/release/cli \
  --output .local/release/livraison --appimagetool /chemin/appimagetool \
  --appimage-runtime /chemin/runtime-x86_64 --appimage-runtime-sha256 EMPREINTE \
  --appimage-notices /chemin/notices-appimage --appimage-sources /chemin/sources-appimage
```

La commande requiert Python 3.11 ou ultérieur, `dpkg-deb`, `rpmbuild`, `desktop-file-validate`, un appimagetool autonome et le runtime AppImage Type 2 x86-64. Préparer les sources et les notices du runtime dans deux répertoires. Le répertoire de notices contient les licences et `SOURCE.txt` avec le commit, l’empreinte et la provenance du runtime. Le répertoire source contient le code de ce runtime, les sources tierces requises et un fichier `SHA256SUMS`. Vérifier les deux binaires AppImage avec les empreintes officielles du moment ou utiliser les binaires archivés du build qualifié. Le script vérifie l’empreinte du runtime et les sources déclarées avant fabrication. [Dépôt officiel du runtime AppImage](https://github.com/AppImage/type2-runtime) · [appimagetool](https://github.com/AppImage/appimagetool)

L’option `--revision` incrémente la révision de paquets de préparation locale, indépendamment de la version applicative. Les fichiers `RELEASE.json` et `SHA256SUMS` identifient les paquets et leurs empreintes. Chacun inclut une identité de compilation, les dépendances de sa publication et leurs notices. Les cinq distributions incluent la licence MIT du code original, les conditions de BRANDING.md et les notices des composants tiers. L’AppImage est accompagnée séparément des sources et de la recette de relien de son lanceur statique. Les paquets de préparation restent dans `.local`.

Pour la clôture de PRE-RELEASE-001, utiliser `--revision 2` avec les deux compilations autonomes courantes et un dossier de livraison neuf. La livraison contient l’AppImage, le DEB, le RPM, l’archive graphique et l’archive du CLI. L’archive des sources du runtime AppImage accompagne ces cinq distributions. Vérifier `sha256sum -c SHA256SUMS` depuis la livraison et comparer l’identité ainsi que le contenu des exécutables extraits aux deux compilations courantes. Une publication ultérieure repart des sources validées et régénère les cinq formats dans un nouveau dossier. La révision locale de clôture conserve `0.1.0`.

## Préparer une version candidate

La compilation ordinaire conserve `0.1.0`. Une fois le numéro validé, ajouter `-p:LecteurWordReleaseCandidate=true` aux **deux** commandes de publication ci-dessus, en choisissant des dossiers de sortie neufs. Les deux exécutables exposent alors `1.0.0-rc.1` dans leur identité. Le moteur conserve aussi ce suffixe. Les formats DEB et RPM ordonnent cette version avant la version finale grâce à la forme `1.0.0~rc.1`. Les archives et l’AppImage conservent le suffixe SemVer. Le contrat CLI reste à 1, l’API d’extensions à 1 et le profil NOVA à 27. Les bornes de compatibilité des manifestes restent des versions stables, avec une borne maximale exclue.

Le DEB et le RPM installent le lecteur sous `/usr/lib/lecteur-word`, son lanceur sous `/usr/bin`, ainsi que son fichier Desktop et son icône hicolor. Le runtime .NET est fourni. Les gestionnaires de paquets résolvent les bibliothèques natives de la distribution. L’AppImage et les archives dépendent de la présence de ces bibliothèques côté système. Le CLI requiert les bibliothèques système de composition et fonctionne sans serveur graphique ni Avalonia. [Dépendances Linux du runtime .NET](https://learn.microsoft.com/dotnet/core/install/linux)

Les quatre distributions graphiques intègrent les vingt langues et le gestionnaire d’extensions. Le rendu des écritures de l’interface utilise aussi les polices disponibles sur le système. La recette JA, KO, ZH-CN et AR a utilisé le repli sur les polices installées du poste, dont Noto CJK et Noto Sans Arabic. L’archive CLI fournit le même moteur de conversion et les réglages de conservation du contrat v1.

## Étude Flatpak

Un Flatpak du lecteur pourrait utiliser un runtime Freedesktop compatible, intégrer le même binaire Lecteur Word et enregistrer son fichier Desktop et son icône avec l’identifiant de l’application retenu. Il faut qualifier l’accès aux documents ouverts par le portail, aux emplacements de sauvegarde, aux couvertures EPUB et aux paquets d’extensions locaux. La publication atomique exige de vérifier séparément les destinations qui arrivent par le portail. Le chemin d’un fichier fourni depuis le gestionnaire de fichiers doit être transmis via l’intégration Flatpak prévue pour les arguments de fichiers.

Les préférences, le journal et les extensions utiliseraient les emplacements XDG de l’application sandboxée. Les polices accessibles au runtime doivent être vérifiées, car elles peuvent modifier la pagination. Prévoir X11 de secours pour ce lecteur et qualifier le mode Wayland disponible, le pont AT-SPI ainsi que le portail du bureau. Le catalogue réseau et les autres intégrations externes influent sur les permissions à déclarer. Privilégier les portails pour éviter une autorisation globale sur le dossier personnel. [Permissions Flatpak et portails](https://docs.flatpak.org/en/latest/sandbox-permissions.html)

Le CLI livré séparément reste le point d’intégration pour OpusNova et les applications tierces. Un Flatpak ne fournirait pas le même exécutable directement accessible depuis leur `PATH`. La préparation V1 conserve les formats décrits dans le guide et traite Flatpak comme une piste distincte à qualifier.
