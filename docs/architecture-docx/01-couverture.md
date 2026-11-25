# Couverture actuelle et besoins du corpus

Le relevé croise le code, les inventaires DocxCore et diagnostics C# RENDER-001, puis les résultats ENGINE-ABANDON-001. Les onze empreintes concordent. L’inspection et la lecture sont disponibles pour les onze copies. Les dix refus de composition conservent le flux simplifié. La référence Word de ces cas reste à constituer.

| Domaine | Existe dans le lecteur actif | À améliorer | À construire |
|---|---|---|---|
| Entrée, OPC/ZIP/XML | Snapshot borné en lecture seule, inventaire ordonné, relations et hashes, SDK Open XML, CLI et manifeste v1 | Typage et indexation des dépendances par partie sans reprendre les octets | Contrat sémantique interne et bilan de représentation |
| Styles et paragraphes | Cascade bornée, titres, style direct, retraits, justification, espacement contextuel et marque de paragraphe LAYOUT-001C | Résolution par propriété et provenance, conditions de tableaux, thèmes et transformations | Cascade complète du profil déclaré, réglages de compatibilité par version |
| Caractères et polices | Unicode conservé, repli/empreinte par face, fontes incorporées statiques sous contrôle, profil latin Skia | Recherche avec retour sûr aux offsets, choix de face par cluster | Shaping, bidi, scripts complexes, positionnement, sélection multi-glyphes |
| Listes et tabulations | Compteurs multiniveaux bornés, libellés, taquets du profil, tabulations gauches enveloppées | Redémarrages, liens styles/listes et conflits largeur/taquets | Alignement droite/décimal/centré selon contenu, points de suite, symboles documentés |
| Sections et pages | Dimensions, marges, sauts et sections compatibles, veuves/orphelines, keepLines/keepNext | Gestion des régions et propagation des contraintes | Sections continues et colonnes selon profil, pages paires/impaires, en-têtes/pieds et numérotation effective |
| Tableaux | Grilles, fusions compatibles, en-têtes répétés, marges/bordures, AutoFit LAYOUT-001C, lecture simplifiée imbriquée | Styles conditionnels, conflits de largeurs, hauteurs et ruptures | Tables imbriquées composées, lignes scindables et fusions réparties selon contraintes |
| Notes | Corps/appel/retour en flux, vérification des références | Numérotation et rattachement à la bonne histoire, séparateurs | Réservation sur page, report, continuation, fins de section/document |
| Champs et sommaires | Instructions et valeurs enregistrées distinguées, vue des titres et repères | Portée locale des signets, champs imbriqués et états dirty/locked | PAGE, NUMPAGES, SECTIONPAGES et PAGEREF bornés, puis TOC selon options prises en charge |
| Images et formes | PNG borné, JPEG baseline/EXIF qualifié en composition LAYOUT-001C, inline DrawingML selon profil | Transformations, traitement de propriétés inertes et conflit dimensions | Ancrage, habillage, zones de texte, formes/VML ciblés, superposition/rognage tracés |
| Alternatives et contenus actifs | Parties et branches inventoriées, géométrie DrawingML exclue du texte de secours par QName | Rattacher les repères au contenu et aux branches sœurs | Sélection MC déterministe, projection passive par profil et preuve d’équivalence d’aperçu |
| Signets, liens et recherche | Liens internes vers blocs, externes inertes, recherche de texte, extrait borné par graphèmes | Index sémantique multihistoire et sources exactes | Rectangles multiples, position dans cellule, pointage des zones et navigation interpages |
| Révisions, commentaires, contrôles | Annotations et repères, révisions conservées, commentaires inventoriés | Politiques explicites de projection, content controls imbriqués | Composition et sélection conscientes des révisions, ancrages de commentaires et rendu qualifié des variantes |
| Objets spécialisés | Formes, relations et objets non interprétés inventoriés, repères explicites | Typage et mesure d’impact des objets | Sous-profils isolés OMML, graphiques, diagrammes et aperçus passifs |
| Vue, exports et sécurité | Avalonia, zoom et miniatures paginés, flux virtualisé, diagnostics, export PDF/ODT/EPUB étroit et atomique | Coûts d’ouverture, index et cache visible, isolation des ressources natives | Pages immuables et rendu au viewport, qualification ciblée par format le jour d’une extension |

Sources existantes de référence. [Architecture et couverture](../architecture-et-couverture.md) · [Compatibilité LAYOUT-001C](../compatibilite-pagination.md) · [Polices](../polices-et-typographie.md) · [Budgets et contrôles](../securite.md).

## Onze copies réelles

Les chiffres ci-dessous comptent des déclarations XML dans l’inventaire, y compris branches alternatives et parties non choisies. Une occurrence XML peut partager un objet logique ou constituer une variante. Les notes sont comptées par appels. Les nombres d’instructions sont des éléments XML et peuvent fractionner un champ. Les blocages sont les **codes distincts bloquants** des diagnostics C# RENDER-001. Ils peuvent compter plusieurs manifestations d’un même manque. Le relevé privé contient tous les QNames, compteurs et blocages avec leur provenance. Les observations des anciens moteurs servent de témoins de risque.

| ID et repère | Pages C# / codes bloquants | Structures observées et chantier prioritaire |
|---|---:|---|
| 00 · annexe de collecte | 0 / 13 | 6 tables, 3 ancres, 7 alternatives, formes et zones de texte, en-tête/pied et instruction de champ. Résoudre la grille, l’habillage et le choix DrawingML/VML |
| 01 · lettre | 1 / 0 | 1 section, 3 liens externes inertes. Préserver la page calculée et tracer les substitutions. Témoin de non-régression |
| 02 · recette document | 0 / 6 | 2 sections, 2 appels de pied et 4 de fin, 12 scalaires hors BMP dans les nœuds textuels comptés. Relier et composer les notes avec Unicode |
| 03 · recette ouvrage | 0 / 6 | 2 sections, 2 appels de pied et 4 de fin, 12 scalaires hors BMP. Couvrir les variantes de placement et la numérotation |
| 04 · guide utilisateur | 0 / 12 | 33 tables, en-tête/pied, listes, styles de tables conditionnels et conflits de largeurs. Étendre l’AutoFit par preuves |
| 05 · Stage 2 | 0 / 20 | 10 sections, 16 tables, 21 ancres, 20 alternatives et 14 déclarations OLE. Sommaire avec taquet droit 7 923 twips, TOC et 19 PAGEREF d’après RENDER-001. Conserver l’aperçu passif disponible et diagnostiquer tout contenu actif ou absent |
| 06 · bilan CE1 | 0 / 6 | 5 tables, 3 dessins inline. Étendre propriétés d’image et de ligne, puis contrôler leur effet sur les coupures |
| 07 · CDC | 0 / 17 | 4 tables, une ancre, un appel de pied, symboles et 5 caractères privés. Identifier la face et le chemin de glyphe. Le constat historique des cinq .notdef ne justifie pas de remapper U+F0E8 |
| 08 · fiche tableau/graphique | 0 / 9 | 3 sections, 3 tables, 3 ancres et 3 alternatives, un appel de pied. Vérifier boîte du titre, dimensions d’image et transformation en coordonnées de page |
| 09 · statuts | 0 / 7 | 2 sections, 5 références d’en-tête et 4 de pied, 2 ancres, 5 contrôles de contenu. Couvrir rotations, champs et empilement des régions |
| 10 · guide composé | 0 / 6 | 3 sections, 6 appels de pied et 6 de fin, une table, styles manquants signalés, champs et histoires héritées. Témoin des réservations et transitions de notes |

Stage 2 est le témoin final d’intégration des dépendances. L’alignement du sommaire, le calcul des notes et la place des dessins forment des problèmes distincts qu’il faut isoler dans des témoins synthétiques avant le cas complet. Les zéros techniques DrawingML écartés du texte de secours restent dans l’inventaire. La présence de déclarations OLE mesure un besoin de représentation passive et d’information, avec exécution interdite.
