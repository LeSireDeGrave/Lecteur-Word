# Pagination expérimentale — LAYOUT-001A

Profil historique `docx-paragraph-layout-v1`, étendu depuis par [LAYOUT-001B](composition-listes-tableaux-images.md) sous l’identifiant local `docx-flow-layout-v2`, dans le même compositeur. Le manifeste d’inspection et le contrat OpusNova restent inchangés ; `pagination: notTested` continue de désigner la fidélité Word.

## Périmètre qualifié

- Paragraphes directs du corps, runs et texte latin couvert par les faces sélectionnées, espaces conservés, sauts textuels et sauts de page manuels.
- Dimensions de page, orientation par dimensions et marges en twips convertis en points (20 twips = 1 point). Propriétés absentes : valeurs du profil de lecture A4 / marges 54 pt, signalées explicitement.
- Styles par défaut, chaînes `basedOn`, styles de paragraphes et caractères, surcharges directes ; gras/italique, souligné simple, taille et couleur RGB. Les propriétés essentielles non implémentées bloquent le profil.
- Le lot [FONTS-001](polices-et-typographie.md) ajoute les faces Linux demandées réellement chargées, les références latines de thème et les substitutions traçables, avec métriques propres à chaque face. Les TTF incorporées ne sont chargées qu’après contrôle du format et des droits techniques ; quatre faces DejaVu Serif libres restent disponibles comme repli.
- Alignements gauche, centre, droite ; retraits simples, première ligne/suspendu ; espacements avant/après ; interlignes proportionnels, exacts ou minimaux. Un interligne plus petit que la hauteur des glyphes est refusé.
- Coupure des paragraphes entre pages avec protection veuves/orphelines ; `keepLines`, `keepNext`, `pageBreakBefore`. Une contrainte impossible à tenir dans une page est refusée, pas relâchée silencieusement.
- Sections `nextPage`, avec formats différents, et `continuous` à géométrie identique. Le type décrit le placement de la section courante relativement à la précédente, conformément à la [définition Microsoft de SectionType](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.sectiontype?view=openxml-3.0.1). Les changements continus de géométrie et les départs pairs/impairs/colonne restent refusés.

## Composition et trace

Les lignes contiennent le paragraphe, la section, les fragments de runs avec provenance et offset UTF-16, les styles calculés et le saut textuel source. La somme des fragments et des sauts est comparée au texte source. Les pages ont leur propre géométrie ; les numéros représentent uniquement les pages réellement produites par le moteur.

Les métriques et le dessin utilisent les mêmes faces Skia. Les glyphes sont contrôlés contre les limites physiques de la page. L’image de page utilise une résolution adaptée au zoom/DPI, plafonnée à 3 pixels par point et 16 Mpx ; les miniatures viennent du même dessin avec un ratio correspondant à chaque page.

Budgets du profil : 50 000 nœuds d’inventaire, 500 000 caractères, 10 000 paragraphes, 50 000 lignes et 500 pages ; composition bornée à 15 s, 512 tailles/faces dans le cache de fontes. Annulation prise en charge. Un dépassement laisse la lecture simplifiée disponible dans son propre périmètre.

## Structures bloquantes et écarts

Les notes en bas de page/fin, en-têtes/pieds, champs, révisions, scripts et propriétés essentielles hors profil restent bloquants. L’extension LAYOUT-001B qualifie des listes, tabulations, tableaux à grille et images intégrées selon [sa matrice précise](composition-listes-tableaux-images.md). Les structures ou configurations restantes refusent le document paginé entier et conservent le mode simplifié.

La justification, les colonnes multiples, les règles typographiques Word complètes, les effets de thème hors polices latines et les comportements de compatibilité Word ne sont pas reproduits. Les substitutions de police, métriques, retours et coupures peuvent différer de Word. Aucun rendu Microsoft Word de référence n’a été fourni pour les fixtures ; aucune équivalence générale n’est annoncée.

## Viewer et exports

« Pagination expérimentale » reste visible. Précédent/suivant, zoom, Ctrl+9 pour ajuster à la largeur, miniatures réelles et bascule vers la lecture simplifiée utilisent le même résultat de composition. Les documents hors profil n’affichent aucune page fictive.

Les exporteurs PDF/ODT/EPUB ne sont pas étendus : ils gardent `plain-paragraphs-v1` et ses précontrôles. Le PDF simple suit sa pagination antérieure, distincte du nouvel aperçu ; cette distinction apparaît avant l’export. Une page visible dans ce nouveau profil ne qualifie pas son export.

## Fixtures et recette

Les cas ciblés sont `layout-geometry`, `layout-styles`, `layout-constraints`, `layout-sections`, `layout-gates`, `layout-gui` et `native-layout`. Ils sélectionnent un cas à la fois. `Composition-compatible.docx` est entièrement synthétique et reste dans la qualification locale ignorée par Git.

Le contrôle physique de 26 lignes donne 3 pages de 10/10/6 lignes, à 200 × 240 pt avec marges 20 pt. La fixture compatible stylée donne 5 pages : 3 portraits et 2 paysages, dont la page créée par le saut manuel. Le document réel `Guide_ouvrage_compose_TEST.docx` reste hors profil et conserve ses notes et son flux simplifié, sur copie contrôlée.
