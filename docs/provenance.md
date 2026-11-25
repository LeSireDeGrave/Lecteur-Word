# Provenance des sources et ressources

Cette page accompagne la préparation du premier commit. Elle distingue les fichiers reproductibles du produit des documents et résultats de recette locaux.

## Code et tests synthétiques

Les bibliothèques C#, la visionneuse, les outils du projet et les cas ciblés ont été écrits pour Lecteur Word. Aucun historique ou code d'OpusNova/ODG n'a été copié. Les développements originaux dont les droits appartiennent à l’auteur sont couverts par [MIT](../LICENSE).

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

À la demande du propriétaire, les fichiers fournis `docs/maquettes/LecteurWord.ico` et `Logo Lecteur Word avec livre ouvert.png` sont copiés dans `src/LecteurWord/Assets/`. ICO : SHA-256 `bcd904ad9d4012782bb2d657da215a747199ae1ea28f89d6b40ff0dee0fb4174`. Grand logo PNG : `b770a4910776c755d04c29e107c176abb01c0225498263f9009dabdcfe4ca401`. L’icône PNG 256 × 256 de l’application est extraite octet pour octet de la plus grande image PNG déjà incorporée à l’ICO ; aucun dessin ou redimensionnement ajouté. Les originaux et la maquette UX restent inchangés. D’après la déclaration de l’auteur, le logo et la maquette ont été créés pour Lecteur Word avec l’assistance de ChatGPT (OpenAI). La décision LICENSE-002 fixe [les conditions d’utilisation et le périmètre exact des fichiers](../BRANDING.md). Les dessins vectoriels des commandes, créés pour le projet, sont couverts par MIT.

## Fixtures LAYOUT-001B

`tests/Viewer.Targeted/StructureCases.cs` génère les témoins de listes, tabulations, tableaux et petites images PNG (Skia déjà présent). Le corpus ajouté par le propriétaire dans `exemples/` est copié uniquement vers la qualification locale ignorée ; aucun DOCX Internet n’est ajouté aux sources ou redistribué. Le témoin de tableau isolé copie la première sous-arborescence de tableau du Guide dans une fixture privée distincte avec un contexte de recette déclaré ; il ne prétend pas reproduire le document entier. Les originaux sont contrôlés par SHA-256.

## Fixtures LAYOUT-001C

`tests/Viewer.Targeted/CompatibilityCases.cs` génère les paragraphes, styles, tableaux et JPEG témoins. Le petit JPEG bicolore provient d’un bitmap synthétique créé en mémoire avec Skia déjà présent. Les en-têtes EXIF de test sont construits pour les huit orientations et les deux ordres d’octets TIFF ; les variantes hostiles sont dérivées de ces octets synthétiques. Aucun code OfficeCLI/ElixirOffice, image privée ou nouveau corpus tiers n’est copié dans ces sources. Les noms des documents, diagnostics, empreintes, images et mesures des onze copies réelles restent exclusivement dans la qualification locale ignorée.

## Reproduction RENDER-001

`tests/Viewer.Targeted/RenderCases.cs` construit une fixture DrawingML minimale pour distinguer les valeurs de géométrie du texte du dessin. Elle vérifie que les nombres du vrai texte et les contenus d’espaces de noms inconnus restent consultables, tandis que les paramètres techniques gardent leur inventaire et leur provenance. Le cas réel exige une copie explicitement désignée par `LECTEUR_WORD_QA_INPUT`. Les binaires, sources externes examinées, prototypes, copies privées, mesures et captures de comparaison restent sous `.local/qualification/render-001`, sans intégration de code externe au produit.

## Fixtures ENGINE-001

Statut historique : expérience abandonnée par ENGINE-ABANDON-001. Ses sources de test et de raccordement restent archivées, exclues du build actif ; les rapports et ressources privés sont conservés avec leurs provenances. Les enseignements fonctionnels servent uniquement à définir des cas et une implémentation C# indépendante fondée sur les spécifications publiques.

`tests/Viewer.Targeted/EngineCases.cs` construit ses DOCX synthétiques : corps, notes, en-tête/pied avec accents, caractères hors BMP et caractères privés, ainsi qu'une image PNG verte produite en mémoire avec Skia existant. Les vérifications portent sur le texte original, les positions dessinées, les pixels de l'image, les macros/plugins désactivés, les refus et l'annulation. Aucun document privé ni code de moteur tiers n'est copié dans ces fixtures. Le runtime, ses sources d'audit, notices, empreintes, mesures et captures restent dans la qualification locale ignorée. L'adaptateur du SDK est expérimental et sa diffusion reste soumise à la décision explicite prévue par ENGINE-001.

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

Le cas `publication-core` construit une zone de texte VML synthétique dont la forme est hors profil. Il rouvre les sorties ODT, EPUB et NOVA et vérifie la présence du texte en mode compatible, le refus en conservation intégrale et le refus d’un dessin sans alternative. Un second témoin synthétique mesure une variante paire sur un document d’une seule feuille. Il contrôle la provenance de la représentation, le cadre éditable ODT et le regroupement des libellés équivalents dans le flux EPUB et le manuscrit NOVA. Le cas `running-drawing-groups` contrôle aussi le transfert d’un champ de total de pages dans le flux d’en-tête ODT.

`PageDependencyCases.cs` construit la collection bibliographique, le magasin, les champs et les liaisons avec des valeurs synthétiques. Il contrôle le contenu éditorial, le raster et les refus de composition et d’export. Les sources bibliographiques du corpus privé, l’audit OPC et les fichiers produits restent dans la qualification locale ignorée.

## Témoins RENDER-QUALITY-001

`RenderQualityCases.cs` génère un DOCX synthétique avec des champs imbriqués, un signet, un lien de sommaire, un renvoi de page vide et trois fonds de cellule. Le cas modèle vérifie le texte visible, les résultats enregistrés et les pixels du raster. Le cas X11 utilise trois copies saisies explicitement dans la qualification locale et vérifie la lecture et les refus d’export. Les captures, les mesures, les copies et les références LibreOffice restent locales.

### LECTEUR-WORD-PAGINATION-TOC-002

`tests/Viewer.Targeted/PageTocCases.cs` construit ses propres DOCX synthétiques. Les témoins couvrent un sommaire multipage, les instructions réparties, les signets, la mesure du numéro, le changement de page causé par sa largeur, le cycle de pagination et les coordonnées en fenêtre headless. Les DOCX, inventaires, mesures, rasters et résultats de recette restent dans les sorties locales ignorées.

### LECTEUR-WORD-PAGINATION-HIDDEN-TC-003

`tests/Viewer.Targeted/HiddenTcCases.cs` génère le texte masqué, les styles, les marqueurs TC, les opérandes et les structures de tableau et de pied à partir de données synthétiques. Les tests de lecteur exercent la recherche et la copie de la projection visible. Les fichiers DOCX, inventaires détaillés, mesures et captures restent dans la qualification locale ignorée.

### LECTEUR-WORD-PAGINATION-STORIES-004

`tests/Viewer.Targeted/Story004Cases.cs` génère des interlignes et marques de paragraphe, une image unie avec Skia, des tableaux d’en-tête et de pied, des champs et des sections synthétiques. Les deux copies A4, les mesures de placement, les rasters et les captures de fenêtre restent dans la qualification locale ignorée.

### LECTEUR-WORD-PAGINATION-STAGE2-001

`tests/Viewer.Targeted/Stage2Cases.cs` génère son image unie avec Skia et construit des ancrages, des propriétés de mise en page et des zones de texte synthétiques. Le JPEG CMJN, son profil ICC, les copies réelles, le PDF de comparaison produit par Writer, les rasters et les mesures restent dans la qualification locale ignorée.

`tests/Viewer.Targeted/FlowEngineCases.cs` construit les témoins OOXML et leur image de test avec Skia. Le cas `docx-flow-engine` contrôle la conservation du texte, les intervalles UTF-16, les largeurs de composition, les fragments de rangée et de note et les renvois de page. Les paquets générés sont vérifiés avec OpenXmlValidator. Les DOCX générés, les copies privées, les rasters et les mesures restent locaux.

Les témoins de reprise couvrent aussi les contours synthétiques, les tableaux AutoFit sur deux largeurs, les coupures internes d’une note et les marques numériques. Le témoin intégré combine les colonnes, le dessin, la réserve de note, les tableaux et le champ de total dans un paquet synthétique validé par OpenXmlValidator.

Les cas de flux et de renvois construisent aussi une fusion verticale répartie sur plusieurs feuilles, une bordure de paragraphe, des seuils de crénage et un résultat PAGEREF stocké. Les valeurs, les lignes des cellules et les pixels testés proviennent de ces témoins synthétiques.

Les témoins de la même reprise ajoutent les totaux de section dans le corps et un pied hérité, les références de styles liées et les parents inapplicables, ainsi qu’une table contenant un trait d’union insécable. La vignette VML et l’objet OLE utilisent une image synthétique et un conteneur binaire vide produit par les fixtures. Les tests contrôlent les pixels, la sélection des occurrences et le refus d’une relation contradictoire. Les paquets et les résultats restent locaux.

Les témoins de la continuation normative construisent des enregistrements linéaires WMF et EMF et un objet OLE synthétique. Le test contrôle les trajectoires, les pixels, les relations et le refus d’un enregistrement hors profil. Les tests de flux exercent aussi les marges par rangée, les signets entre blocs et un refus de note rattaché à son appel. Les témoins de champs utilisent des renvois et caches fictifs. Les paquets et les mesures restent locaux.

Les cas `microsoft-strict` et `microsoft-compatibility` construisent un paquet Strict et des branches MCE synthétiques. Le témoin Strict couvre le corps, un en-tête, un taquet signé, un champ et une racine incompatible. Les cas MCE couvrent un conteneur déclaré par `ProcessContent`, la portée des préfixes, la sélection de branche et le refus d’une déclaration requise inconnue. Les fichiers générés restent dans les sorties de qualification locales.

`tests/Viewer.Targeted/WordFormattingCases.cs` produit six témoins ciblés. Les styles, branches MCE et entrées de thème sont synthétiques. Les attentes de couleur proviennent d’un exemple Microsoft et d’un calcul HSL sur une couleur témoin. Les tests vérifient les déclarations héritées et directes, leur provenance, le placement et le raster. Les fichiers générés et le rejeu du corpus restent locaux.

`tests/Viewer.Targeted/ManuscriptPaginationCases.cs` génère une note avec exposant et séparateurs, un tableau suivi d’une légende solidaire et des champs DOCPROPERTY avec leurs propriétés OPC. Le manuscrit réel, son audit et ses rasters restent dans la qualification locale ignorée.

`tests/Viewer.Targeted/PageTocRegenerationCases.cs` génère ses titres, styles, champs et sections. Il contrôle un sommaire sans cache sur une ou plusieurs feuilles, les numéros logiques et les liens en Page simple et Double page. Le cas `docx-v1-corpus` charge ses références par un manifeste privé. Les copies, leurs empreintes de recette et les comparaisons Word restent locales.

`tests/Viewer.Targeted/PageEffectiveStyleCases.cs` génère les deux parties de styles, les variants de compatibilité 14 et 15, les textes, les alternatives MCE et les cellules du tableau. Les attentes portent sur les sources OOXML, les mesures, les coupures et les rasters du lecteur. Les témoins et leurs empreintes appariables à des PDF Word restent dans la qualification locale.

`tests/Viewer.Targeted/PageParagraphConformanceCases.cs` associe des tabulations à un texte en capitales, une bordure de paragraphe, une trame et des modèles de notes synthétiques. Les tests Custom XML et WMF génèrent aussi leur propre sélecteur de galerie, leur champ et leur stylet passif. Les paquets, rasters et mesures restent locaux.

Les témoins de sommaire réparti sur plusieurs paragraphes, de propriétés de document dans le corps, de fonds de caractères et de coexistence des types de notes utilisent du XML synthétique. Le cas `docx-raster-admission` dérive ses entrées du petit PNG de test et construit les chunks témoins avec leur CRC. Les profils analysés dans le corpus réel, les mesures CPU et les rendus de qualification restent dans les sorties locales.

Le témoin `page-endnotes` construit deux sections, les notes, séparateurs et champs de total. Le cas VML utilise les formules du cadre de type 75 documenté par Microsoft et un métafichier synthétique. Le témoin de magasin XML utilise des valeurs fictives. Les fontes de substitution sont chargées localement avec leur identité. Les copies DOCX, polices du système et sorties de comparaison ne font pas partie des sources.

Le cas `word-style-observed-collections` construit deux collections synthétiques avec tailles et couleurs opposables et vérifie leur provenance sur trois profils. Il exerce aussi les replis des variantes d’Aptos et un saut manuel isolé. `table-page-widths` utilise un retrait et une marge directe inventés pour le témoin. Le couple Guide et PDF Word 365, le rapport d’origine et les mesures avant/après restent dans la qualification locale ignorée.

Les témoins de copies sans extension, de chaînes et de styles absents, de ligatures et d’ombrages utilisent des déclarations synthétiques. L’essai de composition avec les faces locales Carlito utilise une configuration Fontconfig privée au processus de recette. Les faces, sources DOCX/PDF appariées, comparaisons et rasters demeurent dans les sorties locales.

Les témoins de reprise DOCX V1 construisent le taquet marginal, le dessin flottant laissant un seul corridor, les marques à soulignement double et les tracés EMF remplis. Les enregistrements et pixels attendus sont synthétiques. Les métafichiers privés, le PDF Word apparié et les relevés des lignes et des métriques HarfBuzz restent dans la qualification locale.

Les témoins de continuation construisent un aperçu OLE dont les avances WMF et le commentaire de ligne de base sont synthétiques, ainsi qu’une zone flottante de deux paragraphes et d’un tableau. Le test de l’alias symbolique réemploie la face DejaVu modifiée dans le témoin de table cmap. Les images, données de caractères du corpus et comparaisons PDF restent dans les sorties locales.

Les cas `open-reliability` et `word-text-effects` construisent leurs documents avec des paragraphes, couleurs, styles et propriétés d’ombre synthétiques. Le premier contrôle l’ordre de publication et l’annulation avec un point de synchronisation déterministe. Le second compare les avances et le raster avec et sans effet. Les journaux de mémoire et le rejeu des documents privés restent dans la qualification locale.

Les témoins de note dans une cellule, de dessin polygonal VML, de débordement vertical et de bordures jointes utilisent des dimensions, textes, styles et couleurs synthétiques. Les tests vérifient les pixels, la réserve et les coordonnées de sélection. Le contrôle de remplacement utilise deux pages synthétiques pour éprouver la libération du compositeur précédent. Les DOCX privés, les fichiers de polices examinés et les relevés cmap restent locaux.

Les témoins d’enveloppe WMF, d’échelle horizontale et de zones flottantes génèrent des enregistrements, des positions et des paragraphes synthétiques. La vérification des relations SmartArt utilise une copie du corpus désignée par le manifeste local. Les relevés libwmf et HarfBuzz, les sous-ensembles de polices extraits du PDF Word et les images de contrôle restent locaux.

Les témoins de zones VML en en-tête génèrent leurs formes, couleurs, calques et ancres. Les essais de taille latine utilisent un témoin synthétique pour la provenance. Les variantes du DOCX de Stage 2, le PDF Writer, les relevés et les rasters restent dans la qualification locale.

## Plateforme et extensions de démonstration

Les tests `PlatformCases` construisent des états, paquets et incidents synthétiques. Les sources JSON des démonstrations, les modèles, les schémas du SDK et les catalogues intégrés sont des ressources originales du projet couvertes par MIT. Les dix-huit catalogues de contribution demandent encore une relecture native. Les paquets `.lwext` construits à partir de ces sources, rapports, instantanés XDG, captures et corpus de qualification restent locaux.

Le cas `theme-kde` ouvre un DOCX synthétique et des paquets de test déclaratifs. Il observe les couleurs des contrôles Avalonia dans une session KDE avec XWayland. Les captures et leur planche restent dans les sorties locales.

Le cas `language-layout` utilise les vingt catalogues intégrés et une extension de langue synthétique aux libellés longs. Il mesure l’encombrement des commandes dans les fenêtres compactes et contrôle les glyphes et le sens de lecture de l’interface. Le cas `platform-kde` exerce le sélecteur à partir d’événements clavier XWayland dans une session KDE isolée, avec des extensions synthétiques.

Les nouveaux catalogues intégrés dérivent des libellés français et anglais du projet. Une traduction initiale M2M100 a été produite localement, puis les libellés du sélecteur, les formes de compteurs et les paramètres de format ont été contrôlés. Le modèle et ses caches restent dans la qualification locale. La relecture linguistique des catalogues reste ouverte.
