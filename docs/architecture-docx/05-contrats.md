# Contrats d’interface et états

Ces signatures décrivent les frontières à implémenter dans DocxFlow. Elles peuvent être internal au départ. Aucun contrat JSON public ni changement de version du manifeste v1 n’est présumé.

```text
BuildModel(InspectedSnapshot, SemanticProfile, CancellationToken)
    -> ModelResult(DocumentModel?, CoverageLedger, Diagnostics)
ResolveFonts(DocumentModel, FontPolicy, CancellationToken)
    -> FontCatalogSnapshot
Shape(LogicalRun, FontCatalogSnapshot, ShapeContext, CancellationToken)
    -> ShapedRun(Glyphs, Clusters, Metrics, Resolutions)
ComposeAsync(DocumentModel, LayoutSettings, FontCatalogSnapshot, ResourceBudget, CancellationToken)
    -> stream LayoutEvent(Batch | Invalidated | Completed | Failed)
Paint(PageSnapshot, ResourceLease, Viewport, Scale, CancellationToken)
    -> RasterLease
Find(LayoutSession, SearchQuery, CancellationToken)
    -> SearchHit[]
HitTest(PageSnapshot, PagePoint, HitTolerance) -> Hit?
ResolveSelection(LayoutSession, LogicalRange[]) -> Selection
```

## Propriété et identité

`InspectedSnapshot` désigne une inspection acceptée avec son identité d’entrée. Les ressources sont lues via handles bornés qui pointent vers l’instantané. Aucune réouverture de chemin utilisateur pour compléter le modèle. Les types publics exposent des valeurs et collections figées avec propriété claire des tampons. Les handles de ressources et surfaces natives sont détenus par la session ou son bail, jamais modifiables via un snapshot. La fermeture révoque les nouveaux baux et attend ou annule les travaux en cours. Un worker optionnel de décodage consomme un sous-ensemble borné de ce contrat et valide ses sorties.

`SessionId` identifie l’ouverture. `GenerationId` croît par session à chaque recalcul invalidant. Chaque événement porte ces deux identifiants. L’identité de calcul couvre empreinte du document, versions de profil/moteur, options de présentation/révisions/champs et catalogue des faces résolues. Les pages, index, métriques et diagnostics visibles proviennent d’une génération compatible, adoptée d’un seul geste sur le thread UI. Un événement tardif d’une ancienne session ou génération est ignoré. Un ordinal physique peut désigner une autre page après recalcul. Les signets restent des ancres sémantiques. Pour transférer le viewport entre générations, localiser l’ancre dans le nouvel index.

| Événement | Précondition et effet |
|---|---|
| `Batch` | Publie des pages figées, leur sous-index et les diagnostics liés dans une même enveloppe. La suite des pages connues forme un préfixe. Les positions des histoires non placées restent inconnues. Un lot peut exposer un état provisoire explicite |
| `Invalidated` | Retire l’ancienne génération. Le producteur peut réutiliser des valeurs de préfixe dont les dépendances restent identiques, en republiant leurs identités dans la nouvelle génération |
| `Completed` | Terminal pour la génération. Fige couverture, verdict et total physique éventuel. Les positions finales et la table de correspondance vers les histoires partagent cette identité |
| `Failed` | Terminal pour le travail. Offre diagnostic, éventuel dernier instantané validé et décision de repli. Une annulation reste distincte d’une erreur et ne déclenche pas un résultat présenté comme achevé |

## PageSnapshot

| Champ | Contrat |
|---|---|
| Identité et régions | Session, génération, ordinal physique 1-based, libellé imprimé, section(s), provenance des réglages, dimensions, corps/colonnes/en-têtes/pieds/notes. Statut stable ou provisoire |
| Géométrie | Repère page haut gauche, axes vers droite et bas. Coordonnées stockées en EMU signés sur 64 bits, 12 700 EMU/pt et 635 EMU/twip. Opérations d’étendue contrôlées, matrices affines finies et inversibles si interactives. Arrondi défini une fois par profil lors de la conversion, sur positions cumulées. Conversion pixels, arrondi écran et clip appliqués au rendu |
| Ordres | Index d’ordre de lecture et ordre de peinture distincts. Clips hiérarchiques et z-order déterministe. Une scène peut référencer plusieurs histoires sans les fusionner |
| Objets | Identifiant sémantique + occurrence visuelle, type, région, boîtes logiques et d’encre, clip, transformation, position et motif de dégradation, sources, ressources et texte alternatif éventuel |
| Texte/glyphes | Run visuel, FaceId(hash+index+axes), IDs de glyphes, avances/décalages/baseline, quadrilatères, clusters et spans UTF-16 demi-ouverts. Plusieurs spans par cluster, plusieurs glyphes par cluster, zéro glyphe pour un contrôle explicite. Distinction texte source, résultat de champ, libellé généré, répétition et marqueur |
| Liens/notes/signets | Zone visible éventuellement discrète et fragmentée, clé vers le modèle, cible interne logique et justificatif. Cibles externes inertes. Notes conservent clé, numéro effectif, corps/continuations et lien de retour |
| Diagnostics | Code stable, sévérité, étape, effet, cause source, objets/pages concernés, éventuelle approximation ou omission et possibilité de consultation. Localisation non démontrée exprimée par null, sans estimation non démontrée |

Les tableaux de ressources et références sont validés avant adoption. Les boîtes vides sont distinctes d’une boîte incertaine. Un débordement qualifié, un objet volontairement hors marge et un rognage involontaire sont des faits différents. Toute géométrie négative ou hors région doit être expliquée par la transformation, la règle de clip et le statut correspondant.

## Interaction

La recherche indexe les histoires actives et expose la portée choisie pour notes, en-têtes, révisions et contenu consultable. Une entrée répétée peut correspondre à plusieurs occurrences, avec un seul résultat logique et plusieurs zones. L’index garde un mapping réversible NFC/casse vers les ranges originaux. Le résultat porte la génération et une liste de quads facultative. Une recherche sans position de page renvoie la source et le chemin de consultation. Le hit-testing inverse la transformation de l’objet, vérifie le clip puis sélectionne le cluster ou le lien selon l’ordre visuel. La sélection s’aligne sur les limites de graphème ou points de caret certifiés par la face. L’ordre de copie reste logique, avec une politique explicite pour les champs générés et les séparateurs. Un surlignage multiligne, bidi ou tourné est un ensemble de quads. Le rendu et l’interaction consomment les mêmes positions déjà calculées.

## Verdict de mise en page

| État | Admission | Conséquence |
|---|---|---|
| Complète pour le profil | Inspection acceptée. Tous les éléments actifs essentiels et leurs contraintes sont couverts. Aucune lacune essentielle ou inconnue, budget respecté, géométrie et références cohérentes. Les substitutions admises restent déclarées | Pages finales, navigation, recherche et sélection selon capacités qualifiées. La qualification Word se rapporte séparément au couple et au profil testés |
| Partielle | Inspection acceptée. Des pages ou une portion stable ont une géométrie démontrée. Les écarts restants sont bornés, visibles, rattachés à leurs sources et accessibles via consultation ou repère. Aucun impact global incertain revendiqué comme stable | Afficher limites et plage couverte, conserver le flux simplifié. Le total est absent s’il n’est pas établi. Aucune extension implicite des exports |
| Secours | Inspection acceptée. Géométrie indéterminée, dépendances globales ouvertes, cycle/budget ou couverture insuffisante | Aucun numéro de page inventé. Flux simplifié sous ses propres quotas, sources et inventaire consultables |
| Inspection/lecture refusée | Précontrôle non satisfait ou flux lui-même indisponible | Diagnostic de refus et éléments d’inventaire valides disponibles, pas de composition ou décodeur de contournement |

L’état provisoire est orthogonal au verdict. `Completed` fixe le verdict et le total éventuel. Une invalidation retire les pages et poignées de sélection concernées avant l’affichage d’une autre génération. Tout segment source actif entre dans le registre de couverture en tant que représenté, consultable hors page, inertie documentée ou lacune. Une propriété ignorée dont l’impact est inconnu produit un diagnostic bloquant pour le verdict complet. Compter et vérifier toutes les occurrences et sources, y compris texte rogné, parties liées absentes, objets au-dessus du clip et omissions explicites. Les annulations préservent le document antérieur ou l’état de lecture de repli cohérent.

## Règle de qualité des échanges

Tout parcours d’import ou d’export PDF, EPUB, ODT ou DOCX exige une qualification du profil et de ses propriétés utiles. Le contrôle porte sur le fichier et sur la conservation de l’information. Lorsque la conversion met en évidence une perte, une interprétation ambiguë ou une fonctionnalité mal prise en charge, traiter la cause en amont ou refuser le parcours avec un diagnostic précis. Vérifier les témoins produits par réouverture ou validation indépendante.

| Format | Entrée actuelle | Sortie actuelle |
|---|---|---|
| DOCX | Inspection et lecture bornées. L’import applicatif fidèle reste à qualifier | Écriture hors profil |
| PDF | Import hors profil | Export de paragraphes simples après validation de la composition |
| EPUB3 | Import hors profil | Export reflowable de paragraphes simples |
| ODT 1.3 | Import hors profil | Export de paragraphes simples |
