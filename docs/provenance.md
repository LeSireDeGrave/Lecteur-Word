# Provenance des sources et ressources

Cette page accompagne la préparation du premier commit. Elle distingue les fichiers reproductibles du produit des documents et résultats de recette locaux.

## Code et tests synthétiques

Les bibliothèques C#, la visionneuse, les outils du projet et les cas ciblés ont été écrits pour Lecteur Word. Aucun historique ou code d'OpusNova/ODG n'a été copié. La licence de ce nouveau code reste à décider par le propriétaire ; préparer un index ne vaut pas autorisation de commit ou de diffusion.

Les générateurs `tests/DocxCore.Targeted/Fixtures.cs` et les cas C#/Rust créent des documents synthétiques : textes de test, XML OOXML, petit PNG de test et structures volontairement hostiles ou hors couverture. Ils ne contiennent ni manuscrit, ni document `.nova`, ni export personnel. Les exemples XML décrivent les formats testés et les assertions portent sur l'inventaire, la conservation, les refus et le périmètre réellement qualifié.

La revue de provenance du raccordement GitHub distingue ces **sources de tests**, nécessaires à la reproductibilité, de leurs sorties. Les DOCX/PDF/ODT/EPUB générés, captures, journaux, mesures, archives et rapports restent exclus de l'index. Les scripts historiques de clôture de session restent locaux.

## Police de composition

`src/DocxFlow/Fonts/DejaVuSerif.ttf` est une ressource source nécessaire à la construction, et non un binaire d'application généré. Elle est conservée sans modification, avec sa [notice](../src/DocxFlow/Fonts/LICENSE-DejaVu.txt).

- Police : DejaVu Serif 2.37.
- SHA-256 : `a52c2a69dfc93d0b511b98c1019a57eb3d6a62d328bdf1387f00851b02e3a315`.
- Licence : Bitstream Vera et modifications DejaVu dans le domaine public, avec la notice d'origine conservée.

LAYOUT-001A ajoute trois faces sources de la même famille DejaVu Serif, copiées sans modification depuis `/usr/share/fonts/truetype/dejavu`, avec la même notice conservée :

| Face | SHA-256 |
|---|---|
| `DejaVuSerif-Bold.ttf` | `3f704f75238454f68a8202e5098cb0687887480dd63d3dc20b34bdbdfa9d9f3f` |
| `DejaVuSerif-Italic.ttf` | `730469e7a8dce387a760160f05e62ddf021870bd277505bbc5b250e909edeabf` |
| `DejaVuSerif-BoldItalic.ttf` | `f2c39b5964cb27d94bd4c02504cc4471f8c80571abb8df7923c51d13afdca989` |

Les packages NuGet/Rust sont référencés par leurs versions et lockfiles. Aucun cache de packages ni source de bibliothèque décompressée n'est inclus. Les textes juridiques publics embarqués dans À propos sont recensés dans [la provenance des notices](../src/LecteurWord/Notices/PROVENANCE.md) ; ils ne choisissent pas la licence du projet.

Les nouveaux cas `ReadingCases`, `ReadingGuiCase` et `AboutCases` créent des fixtures synthétiques minimales pour isoler les comportements. Certaines combinaisons XML sont volontairement inhabituelles : leurs assertions vérifient la conservation/les repères, pas la conformité complète des schémas Word. La recette réelle et ses captures restent dans les répertoires locaux ignorés ; aucun DOCX réel n’est intégré aux sources. Le dossier `exemples/` est exclu en totalité, sans exception de fichier.

## Maquette UX demandée par le propriétaire

Le propriétaire a explicitement demandé l'intégration de cette référence au dépôt privé et son affichage dans le README :

`docs/maquettes/lecteur-word-maquette-clair-sombre.png`

- Référence approuvée le 20 novembre 2026, fournie dans les ressources du projet.
- Dimensions : **1672 × 941 pixels**.
- SHA-256 : `079276d8a88699c393622cd1500c1b753c6dc47cbcc03d75d00f4948105aa177`.
- Octets conservés sans conversion, nettoyage de métadonnées ou redessin.

Le PNG contient notamment un bloc `caBX` de provenance ; il est conservé avec l'image. La revue locale n'a pas relevé de chemin personnel ou de clé privée dans ce bloc. Sa signature de provenance n'a pas été authentifiée cryptographiquement : l'intégrité comparée à la référence du propriétaire repose ici sur le SHA-256 attendu.

Cette inclusion est une exception explicite à l'exclusion des images privées et captures. Les autres images générées ne sont pas publiées automatiquement. La maquette reste un objectif de design, sans preuve de fonctionnalités ou de conformité Word.

## Qualification des polices — FONTS-001

`tests/Viewer.Targeted/FontCases.cs` ajoute des témoins entièrement synthétiques. Les familles installées sont utilisées localement, sans copie dans le dépôt ni le livrable. Le témoin incorporé dérive uniquement DejaVu Serif libre en mémoire/dans les sorties de test ignorées : famille renommée « LW TestFace », indicateurs fsType simulés pour les cas positifs/négatifs, sommes de contrôle recalculées. Ces indicateurs sont des données de test, pas une nouvelle licence ni une modification de la ressource source. Aucun fichier de police propriétaire n’est ajouté. Les packages et notices restent inchangés.

## Identité visuelle de l’application

À la demande du propriétaire, les fichiers fournis `docs/maquettes/LecteurWord.ico` et `Logo Lecteur Word avec livre ouvert.png` sont copiés dans `src/LecteurWord/Assets/`. ICO : SHA-256 `bcd904ad9d4012782bb2d657da215a747199ae1ea28f89d6b40ff0dee0fb4174`. Grand logo PNG : `b770a4910776c755d04c29e107c176abb01c0225498263f9009dabdcfe4ca401`. L’icône PNG 256 × 256 de l’application est extraite octet pour octet de la plus grande image PNG déjà incorporée à l’ICO ; aucun dessin ou redimensionnement ajouté. Les originaux et la maquette UX restent inchangés. L’autorisation présente concerne l’intégration demandée ; aucune licence nouvelle du projet n’est choisie.

## Fixtures LAYOUT-001B

`tests/Viewer.Targeted/StructureCases.cs` génère les témoins de listes, tabulations, tableaux et petites images PNG (Skia déjà présent). Le corpus ajouté par le propriétaire dans `exemples/` est copié uniquement vers la qualification locale ignorée ; aucun DOCX Internet n’est ajouté aux sources ou redistribué. Le témoin de tableau isolé copie la première sous-arborescence de tableau du Guide dans une fixture privée distincte avec un contexte de recette déclaré ; il ne prétend pas reproduire le document entier. Les originaux sont contrôlés par SHA-256.

## Fixtures LAYOUT-001C

`tests/Viewer.Targeted/CompatibilityCases.cs` génère les paragraphes, styles, tableaux et JPEG témoins. Le petit JPEG bicolore provient d’un bitmap synthétique créé en mémoire avec Skia déjà présent. Les en-têtes EXIF de test sont construits pour les huit orientations et les deux ordres d’octets TIFF ; les variantes hostiles sont dérivées de ces octets synthétiques. Aucun code OfficeCLI/ElixirOffice, image privée ou nouveau corpus tiers n’est copié dans ces sources. Les noms des documents, diagnostics, empreintes, images et mesures des onze copies réelles restent exclusivement dans la qualification locale ignorée.

## Reproduction RENDER-001

`tests/Viewer.Targeted/RenderCases.cs` construit une fixture DrawingML minimale pour distinguer les valeurs de géométrie du texte du dessin. Elle vérifie que les nombres du vrai texte et les contenus d’espaces de noms inconnus restent consultables, tandis que les paramètres techniques gardent leur inventaire et leur provenance. Le cas réel exige une copie explicitement désignée par `LECTEUR_WORD_QA_INPUT`. Les binaires, sources externes examinées, prototypes, copies privées, mesures et captures de comparaison restent sous `.local/qualification/render-001`, sans intégration de code externe au produit.

## Vérification ENGINE-ABANDON-001

`tests/Viewer.Targeted/IndependentEngineCases.cs` génère un DOCX minimal propre au lecteur. Il force les anciens réglages expérimentaux pour vérifier leur absence d'effet, contrôle l'absence d'API et de référence d'assembly au travailleur écarté, puis vérifie une page composée par le moteur C# et l'intégrité de l'original. Aucun code de composition d'un moteur tiers n'est utilisé dans ce test.

Le cas distinct `independent-corpus` exige deux chemins explicites : manifeste privé des onze copies figées et matrice de référence C# qualifiée. Il contrôle les empreintes, l'inspection, la lecture disponible et la pagination C# de référence, sans découvrir ou ajouter de nouveaux documents personnels présents dans `exemples/`.

## Modèle et témoins SEMANTIC-001

`SemanticCases.cs` construit des DOCX synthétiques pour vérifier les sources UTF-16, les références OPC, la sélection d’une branche de compatibilité, l’héritage, les fusions et le résultat de champs imbriqués. `SemanticCorpusCases.cs` exige le manifeste privé des onze copies figées et rapproche les identités, propriétés et textes du modèle avec l’inventaire DocxCore. L’implémentation s’appuie sur les spécifications publiques OOXML citées dans le dossier de modèle, et les résultats restent locaux. [Contrat sémantique et sources](modele-semantique.md).

## Témoins TEXT-001

`TypographyCases.cs` génère des DOCX synthétiques pour la cascade, les langues et scripts, les contrôles, le shaping, les faces et les limites. Le PNG de test est dessiné localement par Skia à partir des glyphes positionnés. `TypographyCorpusCases.cs` s’appuie sur le manifeste privé des onze copies et conserve localement les matrices, mesures, captures et empreintes. Le nouveau code dérive des spécifications publiques OOXML et des API documentées d’ICU, HarfBuzzSharp et SkiaSharp. [Contrat typographique et dépendances](typographie-text-001.md).

## Témoins UX-OPUS-001

`OpusUxCases.cs` génère les témoins de règle, de marques source, de sommaire, de copie, de catégories et de localisation. La recette réelle désigne uniquement le manifeste privé des onze copies. Les captures avant/après, les matrices et les journaux restent dans la qualification locale ignorée. Les contrôles et géométries d’icônes sont implémentés en Avalonia à partir des principes visuels de la capture fournie par le propriétaire. Le code, les actifs et les documents d’OpusNova restent dans leur projet.

La reprise UX-OPUS-001 complète les mêmes témoins avec une cellule placée, une cellule en lecture continue et un caractère privé synthétique hors BMP. Les tests lient les marques aux identifiants et plages UTF‑16 de l’inspection. Les anciennes captures natives servent de comparaison pour le build installé à l’identique. Les nouveaux résultats restent dans le dossier privé de qualification.

## Témoins UX-THEME-002

`ThemeCases.cs` crée un DOCX synthétique avec un run coloré et des fichiers JSON synthétiques de préférences. Les tests comparent le raster, vérifient les valeurs Unicode et les états d’interface et calculent les contrastes à partir des couleurs résolues. Les redémarrages de recette utilisent un dossier XDG privé. Les captures natives du corpus figé et les préférences de test restent locales. Le dessin de l’icône de palette est propre à Lecteur Word. [Méthode de contraste du W3C](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html).

## Témoins PAGE-001A

`LineCases.cs` génère des DOCX synthétiques pour contrôler les plages OOXML, les coupures Unicode et HarfBuzz, le bidi de ligne, les taquets, les champs stockés, la justification, les glyphes dessinés, l’annulation et les budgets. `LineCorpusCases.cs` utilise les onze copies existantes à partir du manifeste privé explicite. Les mesures, profils de mémoire, rasters et résultats restent dans la qualification locale ignorée. L’implémentation s’appuie sur les données TEXT-001 et les API publiques existantes. [Contrat de composition des lignes](composition-lignes-page-001a.md).

## Témoins PAGE-001B

`PageCases.cs` génère les témoins de sections, contraintes verticales, cellules fusionnées, répétitions et images inline. Les PNG et JPEG d’entrée proviennent de bitmaps synthétiques construits avec Skia déjà présent. `PageCorpusCases.cs` compare le profil natif et le profil C# qualifié sur les onze copies du manifeste privé. Les chronométrages, relevés de mémoire, rasters et captures Avalonia restent dans la qualification locale ignorée. [Composition des pages](composition-pages-page-001b.md).

## Témoins PAGE-001C

`TablePageCases.cs` génère des tableaux synthétiques pour exercer les minima, les préférences, les pourcentages, le débordement, la cascade, la grille, l’annulation et une image inline créée avec Skia. `TableAuditCases.cs` conserve avant/après, dans la qualification locale ignorée, les largeurs, sources, styles, refus et mesures des onze copies contrôlées. Les documents du corpus et leurs captures restent locaux. [Profil de composition des tableaux](composition-tableaux-page-001c.md).

## Témoins PAGE-001D

`PageListCases.cs` génère les définitions de listes et les chaînes de paragraphes synthétiques. Il contrôle les compteurs, les sources des niveaux, le raster des marques, les coupures imposées et la solidarité avec un tableau. `TableAuditCases.cs` relève aussi les libellés et leur mesure dans les régions analysées après un blocage de pagination. Les manifestes du corpus, les captures et les résultats restent dans la qualification locale ignorée. [Règle OOXML keepNext](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.keepnext?view=openxml-3.0.1) · [Règles Word de redémarrage](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/140a2cd9-2f26-456d-9760-ae6ecef4e8b5).

## Témoins FONTS-LEGACY-001

`LegacySymbolCases.cs` exerce une table `cmap` symbolique synthétique dans une dérivation mémoire de DejaVu Serif. Le corpus étend l’audit des niveaux de liste aux déclarations `w:sym` et aux caractères privés du texte. Les correspondances de rendu et la politique de faces sont documentées avec le profil typographique. [Symboles historiques Word](polices-et-typographie.md#symboles-historiques-word).

## Témoins PAGE-STORIES-001

`PageStoryCases.cs` construit les parties OPC et les histoires synthétiques. Leur image est générée avec Skia. Les témoins vérifient la cascade de styles, les variantes, la numérotation, les champs stockés, les marges, les quotas et la navigation headless. Les comparaisons des onze copies, les localisations OOXML et les rasters du Guide restent dans la qualification locale ignorée. [Contrat des histoires de page](composition-pages-page-001b.md#histoires-de-page).

## Témoin PAGE-STYLES-002

`PageStyleCases.cs` génère les deux collections, le thème, les réglages et l’en-tête synthétiques. Il vérifie l’origine des déclarations, leur effet sur la mesure, le placement et le dessin et les refus PDF, ODT et EPUB avant écriture. La matrice préalable, les copies et les mesures restent dans la qualification locale ignorée. [Contrat des collections de styles](composition-pages-page-001b.md#collections-de-styles).

Le cas ciblé `exchange-quality` crée un DOCX synthétique de deux paragraphes et produit trois exports du profil texte à partir de ce témoin. Les fichiers produits et les résultats des validateurs restent dans la qualification locale ignorée. Le témoin à deux corps dans `DocxCore.Targeted` génère un paquet OOXML syntaxiquement lisible dont la structure de document est ambiguë.

`PageDependencyCases.cs` construit la collection bibliographique, le magasin, les champs et les liaisons avec des valeurs synthétiques. Il contrôle le contenu éditorial, le raster et les refus de composition et d’export. Les sources bibliographiques du corpus privé, l’audit OPC et les fichiers produits restent dans la qualification locale ignorée.

## Témoins RENDER-QUALITY-001

`RenderQualityCases.cs` génère un DOCX synthétique avec des champs imbriqués, un signet, un lien de sommaire, un renvoi de page vide et trois fonds de cellule. Le cas modèle vérifie le texte visible, les résultats enregistrés et les pixels du raster. Le cas X11 utilise trois copies saisies explicitement dans la qualification locale et vérifie la lecture et les refus d’export. Les captures, les mesures, les copies et les références LibreOffice restent locales.

## Plateforme et extensions de démonstration

Les sources JSON des démonstrations sont originales au projet et accompagnées d’une licence MIT. Les paquets construits à partir de ces sources, rapports, captures et corpus de qualification restent locaux.
