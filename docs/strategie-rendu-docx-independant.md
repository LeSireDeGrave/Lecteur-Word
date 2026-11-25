# Rendu DOCX indépendant — décision après ENGINE-ABANDON-001

ElixirOffice est définitivement écarté. Les travaux ENGINE-002 s'arrêtent au relevé initial ; aucune optimisation de ce moteur n'est poursuivie. L'application active conserve DocxCore, DocxFlow et Avalonia. Les rapports, sources expérimentales et ressources privées historiques restent conservés et identifiés ; ils ne font pas partie du lecteur à construire ou à distribuer.

## Règle de provenance

Les constats de comportement des audits peuvent définir des exigences, des mesures et des cas de reproduction. Les fonctions correspondantes seront écrites en C#/.NET à partir des spécifications publiques, avec une provenance indiquant la clause du format, le témoin synthétique et la propriété vérifiée. Aucun code ElixirOffice, Euro-Office ou ONLYOFFICE ne sera copié, traduit, porté ou directement adapté. Le pont JavaScript de l'expérience abandonnée ne devient pas un nouveau moteur sous un autre nom. Leurs binaires, scripts, images, dictionnaires et polices sont exclus du produit.

Les acquis indépendants LAYOUT-001C et RENDER-001 restent la base : géométrie et styles, justification, tabulations, AutoFit borné, tableaux, JPEG contrôlé, lecture simplifiée, conservation de l'inventaire et distinction entre paramètres DrawingML et texte consultable. Cette décision n'élargit pas leur qualification.

## Architecture recommandée

1. **DocxCore reste l'entrée de confiance.** Inspection OPC/ZIP et XML bornée, sans DTD/résolveur, original en lecture seule, aucune ressource externe ou macro exécutée. Il conserve les parties, attributs, champs, révisions et provenances même lorsqu'une présentation n'est pas encore possible.
2. **Modèle documentaire C# séparé de la présentation.** Résolution des styles, thèmes, listes, unités, sections et relations ; texte Unicode original, offsets et références de source stables. Aucune valeur de champ n'est inventée. Les contenus des formes et des notes restent distincts du flux principal.
3. **Composition C# par étapes explicites.** Mesure du texte, coupures, lignes, cellules, zones flottantes, sections et réservations de notes. Le modèle de pages conserve les contraintes qui ont produit chaque position. Les traitements non pris en charge gardent un diagnostic et une lecture simplifiée ; ils ne deviennent pas des pages faussement complètes.
4. **Un seul modèle pour afficher et interagir.** Page, objets et segments de texte partagent leurs identifiants. La rasterisation Skia existante, les miniatures, la recherche, le pointage et les futurs surlignages consomment ce modèle. Une transformation affine et son inverse doivent relier toute boîte tournée à ses positions de sélection ; les origines de dessin seules ne suffisent pas.
5. **Publication progressive seulement avec cohérence démontrée.** Séparer calcul des pages et cache raster ; une première page ne peut être annoncée stable tant qu'une note, une section ou un champ en aval peut la déplacer. Les résultats provisoires portent une génération et sont invalidés ensemble. Aucun code de cette architecture future n'est livré par le présent retrait.

SkiaSharp est déjà utilisé ; son graphe comprend les dépendances de mise en forme de texte de l'interface. Cela ne prouve pas que DocxFlow gère les écritures complexes. Une future liaison explicite de mise en forme (par exemple HarfBuzz) devra conserver les clusters Unicode et être qualifiée avec ses propres versions et notices. Aucun nouveau paquet n'est ajouté ici.

## Priorités proposées

| Étape | Travail indépendant | Preuve requise avant élargissement |
|---|---|---|
| 1 | Correspondance texte/source/page, détection des glyphes absents, polices à provenance propre | Accents composés/décomposés, hors BMP, ligatures et texte privé conservés ; aucun remplacement arbitraire de caractère |
| 2 | Notes et sections, en-têtes/pieds, pagination des tableaux | Réservations de notes, changements de section, fusions et répétitions vérifiés avec témoins synthétiques et diagnostics |
| 3 | Images ancrées, zones de texte, habillage, rotations | Boîtes, collisions, recadrage et transformations inverses ; titre flottant complet et absence de perte textuelle |
| 4 | Champs et sommaire, sélection sur la page | Valeur enregistrée distincte de la valeur recalculée ; recherche, sélection et surlignage cohérents avec la génération des pages |

Chaque étape doit commencer par une cause démontrée sur le DOCX ou un témoin synthétique propre, puis tests ciblés et voisins, build et comparaison du corpus. Les 128 pages de l'ancien candidat restent une preuve historique de ce candidat ; elles ne sont ni un oracle Word ni un objectif obligatoire de pagination. La fidélité Word exige des références issues du même fichier, avec polices et version identifiées. Le présent retrait n'autorise pas à annoncer la compatibilité générale des DOCX complexes.

## Alternatives à évaluer, sans intégration dans ce lot

| Solution | Licence du composant principal | Intérêt et limites pour ce projet | Proposition |
|---|---|---|---|
| Moteur C# actuel, Open XML SDK et SkiaSharp | Code propre : MIT ; SDK et wrapper SkiaSharp : MIT, notices tierces distinctes | Chemin déjà exécuté, interface maîtrisée, budget réduit ; composition complexe encore à développer | Base recommandée |
| OfficeCLI | Apache-2.0, fichier NOTICE à conserver | Comparateur indépendant déjà audité en RENDER-001. Le rendu HTML observé exige navigateur et ressources/polices ; erreurs de flottants et pagination différente. Le seul binaire n'est pas le coût distribué complet | Premier candidat permissif à évaluer séparément, sans l'adopter ici |
| docx4j avec export-FO / moteur FO | Apache-2.0 pour docx4j ; graphe exact, moteur FO et polices à examiner | Chaîne locale Java → XSL-FO → pages/PDF ; dépendances JVM, rendu PDF et couche de correspondance vers le modèle du lecteur à prévoir. Aucune mesure ni recette du corpus dans ce lot | Comparateur secondaire, pas un remplacement qualifié |
| docx-preview / docxjs | Apache-2.0 ; JSZip et graphe à auditer séparément | Conversion HTML ; la documentation indique des limites de pagination automatique et de champs. Nécessite un environnement HTML et ne fournit pas la géométrie unifiée attendue | Priorité faible pour une vraie composition paginée |

Les constats OfficeCLI sont ceux de la version 1.0.163 et de la révision `fecece3ee6054f419d2e8ecca899d7498142d417` examinées en RENDER-001. Ils ne sont pas une qualification des versions futures. Les autres options n'ont pas été installées ni exécutées ici ; les descriptions techniques et licences principales sont vérifiées dans leurs sources officielles.

## Notices documentaires et interface

Une licence permissive ne signifie pas absence de notices. Pour OfficeCLI, conserver LICENSE et les attributions applicables de NOTICE, signaler les modifications des fichiers repris et auditer les dépendances réellement distribuées. La section 4(d) d'Apache-2.0 prévoit plusieurs emplacements pour les attributions : fichier NOTICE livré, sources/documentation livrées, ou affichage habituel des notices. Le texte standard n'impose donc pas à lui seul un bandeau promotionnel permanent dans l'interface. Son article 6 n'accorde pas de droits généraux sur les marques. Ces constats ne valident pas automatiquement tous les fichiers ou droits d'un paquet donné.

Aucune identité ou promotion ElixirOffice n'est ajoutée à l'application. Les notices obligatoires de Skia, HarfBuzz, .NET, Avalonia/Inter, Open XML SDK et DejaVu déjà présentes restent conservées. Les notices des composants abandonnés restent avec les archives qui les contiennent ; elles ne sont ni effacées ni réattribuées au moteur C#.

Avant toute nouvelle incorporation : version et empreintes figées, licence de chaque composant et de ses ressources, origine indépendante du code, liste des obligations dans les sources/documentation/interface, graphe complet distribuable et tests hors ligne. La licence propre au produit reste à choisir par le propriétaire. Le présent travail ne la modifie pas et ne prononce pas une validation juridique finale de redistribution.

## Sources publiques de travail

- [ECMA-376 : parties du format OOXML et OPC](https://ecma-international.org/publications-and-standards/standards/ecma-376/).
- [Microsoft MS-OE376 : informations d'implémentation et écarts au standard](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oe376).
- [Open XML SDK : licence MIT](https://github.com/dotnet/Open-XML-SDK/blob/main/LICENSE).
- [HarfBuzz : portée de la mise en forme du texte](https://harfbuzz.github.io/).
- [OfficeCLI : LICENSE](https://github.com/iOfficeAI/OfficeCLI/blob/main/LICENSE) et [NOTICE](https://github.com/iOfficeAI/OfficeCLI/blob/main/NOTICE).
- [Apache-2.0, sections 4 et 6](https://www.apache.org/licenses/LICENSE-2.0).
- [docx4j : chaîne export-FO et dépendances](https://github.com/plutext/docx4j/blob/VERSION_17_3_7/docs/Docx4j_GettingStarted.md).
- [docx-preview : licence](https://github.com/VolodymyrBaydalka/docxjs/blob/master/LICENSE) et [limites de rendu documentées](https://github.com/VolodymyrBaydalka/docxjs#breaks).

**Décision de travail : poursuivre le moteur C# indépendant et préparer une évaluation séparée d'OfficeCLI. Aucun moteur alternatif n'est adopté par ce document. STOP après qualification du retrait.**
