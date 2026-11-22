# Couverture actuelle et besoins du corpus

Cette photographie décrit le socle indépendant après LAYOUT-001C et RENDER-001. Les relevés par document et les références de comparaison restent dans la qualification locale.

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

## Témoins de maintenance

Isoler les cas qui combinent styles conditionnels, dessins, notes, listes et champs avant de qualifier un document complet. Les paramètres DrawingML restent dans l’inventaire. Les déclarations OLE peuvent motiver un diagnostic ou un aperçu passif dans un profil dédié.
