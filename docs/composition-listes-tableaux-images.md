# Composition des listes, tableaux et images — LAYOUT-001B

Profil local `docx-flow-layout-v2`, extension du compositeur de paragraphes existant. Les métriques et instantanés des faces FONTS-001 restent communs à la mesure et au dessin. Le manifeste JSON v1, le contrat OpusNova, la lecture simplifiée et les exporteurs restent distincts et inchangés dans leurs responsabilités.

## Matrice de couverture

| Structure | Interprétation | Composition qualifiée / limites |
|---|---|---|
| Listes multiniveaux | numId / abstractNum / ilvl, styles hérités, niveaux 0–8, départs et startOverride | Compteurs décimaux, alphabétiques, romains 1–3999, puces explicites • · - – ; alignement du libellé gauche/centre/droite |
| Redémarrages | lvlRestart et instances distinctes | Compteurs par instance, reprise des sous-niveaux à l’apparition des niveaux parents ; valeur 0 conservée comme « ne jamais redémarrer » |
| Libellés de listes | Source de définition et texte calculé conservés | Fragments signalés `Generated`, exclus du texte source ; suffixe tabulation, paragraphe aligné à gauche, pas de chevauchement inventé |
| Tabulations | Arrêts hérités ou supprimés, positions en points | Gauche, droite, centre et décimale simple point/virgule ; défaut document/settings ou 36 pt de lecture ; valeur source `\t` conservée |
| Limites des tabulations | Positions interprétées | Points de suite, mélange point/virgule ambigu, recouvrement et retour automatique d’une ligne tabulée refusés |
| Grille et largeur de tableau | tblGrid explicite, largeurs positives, dxa / pourcentages entiers OOXML | 1–32 colonnes, largeur proportionnelle calculée, alignements gauche/centre/droite, retrait gauche compatible |
| Hauteur et marges | Hauteurs auto / minimales / exactes, marges de tableau et cellule | Métriques des paragraphes réutilisées ; interlignes et retours dans les cellules ; refus si hauteur exacte coupe le contenu |
| Fusions horizontales | gridSpan | Largeur de la somme des colonnes, provenance conservée ; hMerge historique non qualifié |
| Fusions verticales | vMerge restart / continue | Mêmes colonnes, continuations vides et propriétés compatibles ; groupes fusionnés conservés entiers dans une page |
| Bordures / fond / alignements | Bordures simples, nil/none, RGB/auto, trame claire/solide, vAlign | Haut/centre/bas, fonds et bordures dessinés ; conflits de bordures non nuls différents refusés |
| Tableaux sur plusieurs pages | Lignes et groupes fusionnés | Répartition entre lignes, jamais découpe implicite d’une ligne trop haute |
| En-têtes répétés | Préfixe contigu tblHeader | Répétition réellement dessinée, lignes/cellules marquées `IsRepeated` ; aucun doublon confondu avec du texte source |
| Images intégrées | DrawingML wp:inline, relations internes, tailles EMU | PNG 8 bits non entrelacé dans le profil SafePng existant ; boîte insérée au milieu du texte, largeur et hauteur DOCX conservées |
| Proportions | Dimensions source / pixels | Une déformation déjà demandée par les dimensions du DOCX est signalée ; aucune réduction automatique pour faire tenir une image |
| Ressource d’image | Type, relation, SHA-256, structure PNG et budgets | Image absente, invalide, remplacée sous un mauvais hash ou trop grande : refus de pagination explicite |
| Hors profil | Inventaire et lecture simplifiée conservés | Tableaux imbriqués, styles/AutoFit complexes, partage interne d’une ligne, flottants/habillages/croppings/effets, JPEG hors profil baseline, SVG/EMF, notes de page, champs et scripts non qualifiés |

Les tabulations `num` du niveau de liste sont reconnues lorsqu’elles correspondent au retrait du texte ; les configurations différentes restent refusées. Les suffixes espace/nothing sont conservés mais non qualifiés, ainsi que la solidarité keepNext interne aux cellules. La solidarité d’un paragraphe avec le début d’un tableau est prise en compte ; une contrainte impossible est refusée.

Les valeurs de repli des profils antérieurs restent explicites lorsqu’elles étaient déjà diagnostiquées. Depuis [LAYOUT-001C](compatibilite-pagination.md), une grille sans `tblLayout fixed` passe par l’AutoFit simple mesuré ; les styles généraux et JPEG baseline disposent d’un profil propre. Les limites historiques de cette matrice sont complétées par cette extension, sans qualification de l’algorithme AutoFit Word complet. Les propriétés essentielles non interprétées refusent le document paginé entier ; le flux simplifié conserve alors les repères et diagnostics.

## Pages et traçabilité

Les paragraphes et tableaux sont traités dans l’ordre du corps documentaire et des sections. Un tableau possède un repère de bloc à sa position source ; ses paragraphes de cellules restent identifiés séparément. Les mêmes lignes/fragments servent au rendu des pages, aux miniatures et à la navigation. Les cellules dessinées gardent ID, source, dimensions, bordures, fusions et état de répétition. Images et arrêts de tabulation gardent aussi leurs positions et leur origine dans le rapport technique.

Une fusion verticale ne traverse pas silencieusement une page. Un préfixe d’en-têtes reste contigu et accompagne le premier groupe du corps. Si une ligne, un groupe fusionné, un en-tête avec sa première ligne ou une contrainte de paragraphe ne tient pas dans une page, le moteur refuse la composition, avec zéro page qualifiée et accès au mode simplifié.

La conservation du texte se vérifie en concaténant les lignes non répétées par paragraphe, avec leurs sauts source. Les libellés calculés de numérotation sont distincts des caractères du document. Les images sans texte ne disparaissent pas : leur boîte et leur référence font partie de la page.

## Images et sécurité

Le lecteur réutilise les octets du cliché DOCX inspecté ; aucune seconde ouverture réseau et aucune exécution d’objet incorporé. Le hash est revérifié, puis SafePng valide structure, CRC, dimensions, filtres et flux décompressé. Les métadonnées PNG non nécessaires sont écartées dans une copie de rendu, en gardant l’original et son empreinte.

Limites d’images : 64 ressources distinctes et 24 millions de pixels cumulés ; chaque PNG reste dans les budgets existants (8 Mio compressés, 8 millions de pixels, dimensions ≤8192, inflation ≤32 Mio). Le dessin ne charge qu’une copie de bitmap par ressource utilisée dans la page et la libère après dessin. Les limites de raster de page (16 Mpx), 500 pages, 50 000 lignes et 15 s de composition restent actives. Tableaux : 128 au maximum, 2 048 lignes par tableau, 32 colonnes, en plus du budget global de nœuds et de paragraphes.

Les extensions DrawingML de compression `useLocalDpi` connues sont conservées et signalées ; elles ne remplacent pas les dimensions EMU explicites. L’élément `a:ext` d’une liste d’extensions est distingué de l’élément de dimensions d’une transformation : il n’est pas interprété comme une taille absente. Les autres extensions restent non qualifiées.

## Qualification

Chaque cas se sélectionne explicitement : `structure-lists`, `structure-tabs`, `structure-tables`, `structure-merges`, `structure-table-pages`, `structure-images`, `structure-mixed`, `structure-gates`, `structure-corpus`, `structure-real-table`, `structure-gui`, `native-structures`.

Le corpus dans `exemples/` est lu sur des copies locales ; fichiers et SHA-256 sont conservés. Le Guide complet peut rester hors profil même si son tableau est reconnu : cela ne vaut pas autorisation de supprimer notes, champs ou histoires de section pour obtenir des pages. Le cas `structure-real-table` est un **témoin d’isolation explicite** : sous-arborescence du premier tableau copiée dans une nouvelle fixture, avec géométrie et contexte de recette distincts. Il ne représente pas la pagination du Guide original.

Le témoin combiné synthétique calcule 3 pages ; le tableau long répète son en-tête sur 3 pages. Les mesures, captures Linux, résultats du corpus et la liste exacte des changements restent dans la qualification locale ignorée par Git. Aucun rendu Word de référence fourni : les écarts de métriques, d’AutoFit, de sauts et de placement ne sont pas mesurés contre Word ; aucune fidélité générale n’est annoncée. PDF/ODT/EPUB3 ne sont pas étendus par ce profil.

## Sources techniques

- [Microsoft : niveaux et redémarrages de numérotation](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.levelrestart?view=openxml-3.0.1).
- [Microsoft : fusions verticales](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.verticalmerge?view=openxml-3.0.1).
- [Microsoft : tableaux et grilles](https://learn.microsoft.com/en-us/office/open-xml/word/how-to-insert-a-table-into-a-word-processing-document).
- [Microsoft : image DrawingML intégrée](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.drawing.wordprocessing.inline?view=openxml-3.0.1).
- [Microsoft : extension useLocalDpi](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-odrawxml/c05c287f-f63e-4fa9-8163-e3e106b4105f).
