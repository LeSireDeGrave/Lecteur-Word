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
