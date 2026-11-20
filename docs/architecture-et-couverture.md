# Architecture et audit — 20 novembre 2026

## Composants

| Composant | Responsabilité |
|---|---|
| `DocxCore` | Snapshot borné, précontrôle OPC/ZIP/XML, identification SDK, inventaire structuré et manifeste JSON v1. Aucune référence GUI. |
| `DocxCore.Cli` | Helper hors processus ; capacités, version, inspection et sortie atomique. |
| `DocxFlow` | Projection simplifiée continue issue de l’inventaire, profil simple de composition interne expérimentale et exporteurs stricts PDF/ODT/EPUB3. Dépend de Skia, pas d'Avalonia/GTK. |
| `LecteurWord` | Avalonia, lecture seule, opérations asynchrones, pages/miniatures réellement calculées, recherche limitée, diagnostics et précontrôle des exports. |
| Tests locaux | Fixtures synthétiques, invariants de conservation, contre-exemples, protocole Rust et recette de chaque format. Aucune suite complète exécutée. |

La pagination du manifeste d'inspection désigne la **qualification Word**, toujours `notTested`. Le nombre de pages affiché par le viewer vient de `Composition.Pages`, calculé par notre moteur expérimental. Ces deux états ne sont pas interchangeables.

## Choix SDK / OPC complémentaire

Le [SDK Microsoft](https://github.com/dotnet/Open-XML-SDK) fournit les types WordprocessingML, l'accès OPC et la vérification d'identité du document principal. Il ne fournit [ni rendu ni mise en page Word](https://learn.microsoft.com/en-us/office/open-xml/open-xml-sdk-design-considerations).

Un passage complémentaire sécurisé est nécessaire **avant** l'ouverture SDK : limites ZIP et XML, chemins, doublons, CRC, relations, parties orphelines, extensions inconnues et contenu non représenté. Un `XmlReader` conserve tous les éléments/attributs/valeurs en ordre et conserve les branches de compatibilité sans décider silencieusement laquelle afficher. Le SDK ouvre ensuite le snapshot en lecture seule, `AutoSave=false`, avec budget de caractères.

Éviter un deuxième DOM métier complet réduit la duplication : l'arbre d'inventaire est une liste ordonnée avec parent, nom XML développé, type logique et provenance. `textValue` porte les valeurs textuelles directes ; `w:t`, paragraphes et runs restent des nœuds séparés. Les espaces de formatage XML sont distincts du texte des runs. Aucun `InnerText` n'est utilisé comme projection exhaustive.

Le coût reste réel : snapshot + parties décompressées + inventaire + SDK peuvent coexister. Les budgets et mesures locales en rendent le coût visible. Le parsing complémentaire n'est pas un validateur complet des schémas OOXML. Un ZIP ouvrable n'est jamais déclaré « document valide ».

## Couverture prouvée / limites

« Inventaire OK » signifie que les fixtures synthétiques ont prouvé les comptes, l'ordre, attributs, valeurs et diagnostics ; il ne signifie pas interprétation exhaustive de Word.

| Fonction | Inventaire | Composition / GUI | Exports |
|---|---|---|---|
| Paragraphes, runs, Unicode, espaces | OK, assertions exactes | Flux Unicode ; profil paginé latin avec styles et géométrie de section qualifiés | Profil simple seulement |
| Arabe/CJK/emoji | OK | Texte conservé dans le flux Avalonia ; typographie Word non qualifiée. Composition Skia du profil simple toujours refusée | ODT/EPUB simples : caractères conservés ; PDF refusé |
| Styles directs, héritage, titres | OK, chaîne `basedOn`, cycles/manquants diagnostiqués | Gras/italique/souligné, taille, police et alignement élémentaires ; substitution possible, aucun style Word exhaustif | Refus si propriétés essentielles non mappées |
| Listes multiniveaux, redémarrages | OK, définitions et références conservées | Lecture simplifiée approximative conservée ; compteurs, niveaux et redémarrages qualifiés dans le profil paginé limité LAYOUT-001B | Refus |
| Tableaux, fusions, imbrication | OK, structure et propriétés conservées | Flux simplifié avec imbrication conservée ; pagination des grilles explicites, gridSpan/vMerge compatibles, marges, bordures et en-têtes répétés. Imbrications et lignes trop hautes refusées en pagination | Refus |
| Images, ancres, dimensions EMU, légendes/alt | OK, XML et hashes | PNG 8 bits non entrelacés validés et bornés ; placement DrawingML inline avec dimensions DOCX, repères simplifiés pour les autres formats. Flottants/habillages non qualifiés | Refus |
| Sections, marges, en-têtes/pieds | OK | Repères compacts ; en-têtes/pieds rattachés à leur section, héritage explicite et vue séparée. Variantes source conservées ; aucune affectation à des pages physiques | Refus des propriétés/dépendances hors profil |
| Signets, hyperliens internes/externes | OK, références et cibles conservées sans accès réseau | Navigation vers le bloc du signet ; lien interne navigable ; cible externe visible et inerte. Position intra-tableau non qualifiée | Refus |
| Notes de pied/fin | OK, appels et corps séparés | Correspondance vérifiée : appel numéroté, contenu contextuel et retour. Numérotation de lecture, sans identité Word ; ambiguïtés signalées | Refus |
| Champs / sommaires | OK, instruction et résultat distingués | Champs simples et séquences begin/separate/end reconnues ; valeur enregistrée rendue sans recalcul, instructions dans les détails. Champ inconnu ou sans valeur signalé. Sommaire GUI dérivé des titres | Refus |
| Commentaires, révisions, contrôles de contenu | OK, provenance et politiques explicites | Révisions affichées avec bornes/texte supprimé barré ; contrôles et appels repérés. Corps des commentaires dans l’inventaire, pas rendus | Refus |
| Équations, OLE, objets/dessins inconnus | OK comme inventaire/diagnostic ; binaires hashés | Repères et texte interprétable, aucune équation composée ni exécution OLE | Refus |
| Pagination ou fidélité Word | NT | Expérimental uniquement | Aucune identité Word annoncée |
| Impression, association de fichier, recette physique | NT / désactivée | Aucun changement d'association | Sans objet |

Le profil simplifié ne modifie ni `FlowReader`, ni `Composer`, ni les gardes d’export. Il n’est disponible qu’après une inspection complète acceptée ; un refus de sécurité reste un refus. Les diagnostics complets restent dans le manifeste et le rapport technique ; leur regroupement GUI par catégorie/composant/gravité/code ne les supprime pas.

`ReadablePresentation` ajoute des associations et une projection de lecture au document existant : blocs principaux, correspondances des notes, groupes de champs et sources d’en-têtes/pieds par section. L’arbre `ReadableDocument.Blocks`, son texte complet et toutes les provenances restent conservés. Les codes des champs ne sont jamais exécutés. L’analyse contextuelle des champs est bornée (32 niveaux, 2 048 groupes complexes par histoire) ; les champs hors profil gardent leurs repères et leur inventaire.

Le compositeur conserve l’ancien profil simple pour les exporteurs et ajoute [le profil paginé de paragraphes LAYOUT-001A](pagination-experimentale.md) au viewer : formats/marges, styles élémentaires, métriques réelles des [faces résolues par FONTS-001](polices-et-typographie.md), avec repli DejaVu, coupures, veuves/orphelines et paragraphes solidaires dans leur périmètre qualifié. Les sections compatibles sont composées ; [LAYOUT-001B](composition-listes-tableaux-images.md) étend ce moteur aux tableaux à grille, listes, tabulations et PNG inline. Les flotteurs, notes et champs restent bloquants, ainsi que les configurations de tableaux/images hors profil. Les scripts et glyphes non qualifiés conservent un refus explicite. Aucune identité Word n’est annoncée.

Les deux fixtures positives de tableaux imbriqués/fusions et d’images intégrées/ancrées passent en plus la validation des schémas du SDK (`OpenXmlValidator`). Ce résultat renforce les fixtures ; il ne qualifie ni leur rendu Word ni un validateur OOXML général dans le runtime.

Les exports refusent les tableaux, notes, liens, images, révisions, styles, métadonnées et parties auxiliaires non mappés. Leur profil accepté reste étroit : paragraphes/runs/texte et contrôles textuels admissibles sans autre contenu essentiel. Le PDF simple utilise l’ancienne composition, distincte du nouvel aperçu paginé, avec police embarquée ; la différence est annoncée avant export. Il n’est ni PDF/A ni PDF/UA. ODT est un paquet ODF 1.3 éditable ; EPUB3 est reflowable.

## Audit OpusNova en lecture seule

Sources consultées en lecture seule dans l’arbre canonique OpusNova, HEAD observé `86935c6f98e066b918d7091d628f1c3aaad07281` (différent du HEAD cité au cadrage). Aucun code n'a été copié ou modifié.

- `src/ui/workspace/publication.rs` : les convertisseurs d'import DOCX/ODT/etc. sont désactivés ; ouvrir `.nova` est une autre commande.
- `src/publication/docx.rs` et ses modules : export DOCX actif et précontrôles existants. Aucun exporteur existant n'a servi de base à ce lecteur.
- `src/document.rs` : texte Rope, identités stables, révisions et états de sauvegarde ; offsets en scalaires Unicode.
- `src/project.rs`, `src/nova.rs`, `src/schema.rs` : codec ZIP/JSON, métadonnées, documents, styles, blocs, ancres, ressources ; version native courante observée **26**. Le commentaire historique de `nova.rs` n'est pas pris comme version actuelle.
- `notes.rs`, `tables.rs`, `anchors.rs`, `text_styles.rs`, `rich_text.rs` : limites, placements, styles et historique qui imposeront un adaptateur dédié.
- `docs/OPUS-IO-PREFLIGHT-001.md` : catégories A/B/C/D/E, bilan exhaustif, consentement C, refus E, Undo/Redo et validation de réouverture.

Limites observées utiles au futur adaptateur : archive `.nova` 64 MiB ; texte 32 MiB ; 32 documents ; notes 1 024 / corps total 4 MiB ; tableaux 1 024 lignes, 32 colonnes, 8 192 cellules ; styles 256. Ce sont les limites du code consulté, pas des limites OOXML ni une promesse d'import.

Cet audit décrit le code observé à cette révision ; le futur adaptateur devra revalider le HEAD alors courant. Les états locaux, rapports et comparaisons de dépôt sont conservés hors Git. Aucun code ou historique d’OpusNova/ODG n’est copié dans ce produit.
