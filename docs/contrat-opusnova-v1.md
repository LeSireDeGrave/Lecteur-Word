# Contrat d'échange local — version 1

## Flux et responsabilités

`DOCX → helper C# → inventaire typé + manifeste + blobs bornés → adaptateur Rust futur → précontrôle exhaustif → aperçu → confirmation → nouveau candidat .nova → validation → ouverture`

Le lecteur GUI n'est jamais une dépendance fonctionnelle de l'import. Le helper `docx-core` utilise seulement le cœur d'inspection, sans Avalonia ni Skia. Son coût disque/mémoire est mesuré séparément du viewer. Ni ce lot ni le consommateur Rust de recette n'écrivent de `.nova` ou ne modifient OpusNova.

## Transport, négociation et erreurs

1. L'adaptateur demande `docx-core capabilities`. JSON stdout : protocole `lecteur-word-inspect`, `schemaVersions:[1]`, `engineVersion:"0.1.0"`, `importReady:false`, `exports:[]`.
2. Il choisit une version commune et un dossier temporaire privé. L'exécutable provient du paquet de confiance, jamais du DOCX. Le lancement utilise un tableau d'arguments, sans shell, serveur ou connexion réseau.
3. `docx-core inspect --input <copie/source consentie> --output <nouveau dossier> --schema-version 1 [--options <JSON local de confiance>]`.
4. La sortie finale est un répertoire publié par renommage, contenant `manifest.json` et éventuellement `assets/sha256-<64 hex>.bin`. Un dossier/fichier déjà existant est refusé. Le consommateur attend la fin du processus avant de lire.
5. Les budgets sont ceux demandés/acceptés et figurent dans `budgets`. Le consommateur impose aussi ses limites à la taille du manifeste, nombre de nœuds, ressources, profondeur, durée et RSS du helper. Le prototype de contrat Rust limite le manifeste à 128 MiB et les nœuds à 150 000.

| Code | Sens |
|---|---|
| 0 | Commande de capacités réussie |
| 2 | Inventaire achevé ; qualification partielle ; jamais autorisation d'import |
| 3 | Format/paquet refusé, manifeste refusé si sa publication était possible |
| 64 | Arguments/options invalides |
| 65 | Version non négociable |
| 70 | Erreur interne non masquée |
| 74 | E/S, droits ou destination refusés |
| 75 | Budget d'inspection ou de sortie dépassé |
| 130 | Annulation coopérative |

En cas de budget de sortie très petit, l'erreur est fournie sur stderr/code 75 sans répertoire final. L'absence de sortie finale n'est jamais un succès. SIGINT déclenche l'annulation ; une terminaison forcée peut laisser uniquement un staging privé à récupérer par le propriétaire du dossier temporaire.

## Modèle et invariants

Le schéma formel est [manifest-v1.schema.json](manifest-v1.schema.json). Un exemple complet produit par le CLI est conservé localement dans `.local/qualification/demo/manifest.json`.

- `inspectionComplete` : toutes les entrées ont été lues/comptabilisées, XML parsé et identité principale contrôlée. Cela ne signifie pas interprétation sémantique complète. `status`, couverture par partie/nœud et diagnostics restent obligatoires.
- Couvertures : `read` = lu/représenté au niveau décrit ; `inventoriedOnly` = structure inventoriée sans sémantique qualifiée ; `notRepresented` = non représenté ; `refused` = refusé ; `notTested` = non testé.
- `nodes` : ordre déterministe des parties (URI ordinal) puis parcours préfixe XML. Chaque nœud possède un ID `n…`, un parent, un nom XML `{namespace}local`, un type logique, les attributs triés et une provenance `partUri + path`.
- `path` est un chemin logique à positions 1-based incluant les nœuds textuels ; ce n'est pas un XPath ni un chemin de fichier. Ne jamais le traiter comme un offset de rendu.
- Les blocs/spans sont conservés sous forme de paragraphes/runs/texte et parents ; les tableaux/cellules, notes/appels, ancres, listes, signets et liens gardent leurs identifiants OOXML dans les attributs. Le contrat ne les aplatit pas en HTML ou en PDF.
- `revision` distingue `unchanged`, `inserted`, `deleted`. `fieldRole` distingue `none`, `instruction`, `result`. Aucune politique accepter/rejeter/figer n'est implicite.
- `styles` donne la chaîne de provenance d'héritage, pas une cascade Word entièrement résolue. Les propriétés brutes restent dans l'arbre et les limites sont diagnostiquées.
- `relationships` conserve la partie source, l'ID, le type, la cible originale, le mode et la partie interne normalisée. Les cibles externes ne sont jamais chargées.
- `media` associe une URI OPC à un ID SHA-256 de longueur fixe. Un même hash peut être référencé par plusieurs parties ; le blob est dédupliqué. Les références XML passent par les relations puis l'URI de média, jamais par un nom de fichier injecté.
- Les blobs exposés sont des données PNG bornées, signées et hashées, stockées `.bin`, sans décodage ni garantie de validité d'image complète. Le futur décodeur doit revalider signature/dimensions/hash et ses propres budgets. Les autres médias sont inventoriés/quarantainés et ne sont pas exposés comme images sûres à rendre.
- `sourceSha256` permet d'attacher l'aperçu et les décisions à l'instantané réellement lu. Le JSON est déterministe à source/options/version égales, sans horodatage ni nom absolu du fichier source.

Extrait illustratif, non manifeste complet :

```json
{"schemaVersion":1,"status":"inventoriedOnly","inspectionComplete":true,"importReady":false,"pagination":"notTested"}
```

Le consommateur Rust de recette valide version, blocage d'import, identités, parents et ressources sur un manifeste C# réel. Il refuse les variantes de version, identité de ressource et parent invalides. Ce n'est pas l'adaptateur de production ni une validation de toutes les sémantiques OOXML.

## Précontrôle A–E à implémenter dans OpusNova

| Classe | Politique future |
|---|---|
| A | Équivalence source/cible prouvée par correspondance et réouverture |
| B | Adaptation sans perte sémantique, avec traçabilité |
| C | Différence fonctionnelle expliquée, accord explicite lié au format, source, périmètre et décision |
| D | Incohérence à examiner, aucune « correction » automatique |
| E | Représentation impossible/non qualifiée, refus bloquant |

**Actuellement `importReady` est toujours false** : aucune correspondance OpusNova n'est qualifiée. Tous les éléments hors capacité démontrée de l'adaptateur futur restent E, même si un export simple du viewer est disponible.

Chaque diagnostic devra fournir élément, localisation source, fonction, conséquence, solution et gravité. Accord requis notamment pour aplatir l'héritage de styles, renuméroter des listes, substituer une police, figer un champ/sommaire, accepter/rejeter une révision, déplacer un flottant, changer la portée d'une note, linéariser/fusionner un tableau ou supprimer un lien/objet. Une perte non autorisée reste bloquante.

Le futur adaptateur doit convertir les positions .NET en scalaires Unicode (`EnumerateRunes`), sans réutiliser des offsets UTF-16 pour les Rope/GTK d'OpusNova ; conserver un graphe de correspondance et ne pas normaliser le texte silencieusement. Il doit préserver les identités/collisions, préparer un candidat isolé, valider ses limites natives, ouvrir/sauvegarder/réouvrir et appliquer une transaction compatible Undo/Redo seulement après confirmation. L'original DOCX et les projets existants restent intacts.

## Compatibilité et arbitrages

Une version majeure différente est refusée avant inspection. Les extensions purement informatives peuvent être ignorées seulement si elles n'altèrent pas la lecture des champs connus. Un état, type ou contenu nouveau non compris devient E ; pas d'ignorance silencieuse. Les changements de sens exigent un nouveau `schemaVersion` et de nouvelles fixtures interop.

L'emballage du helper .NET devra être une décision explicite dans OpusNova : taille autonome, RSS et temps mesurés, mise à jour de sécurité, licences/notices et isolation. Native AOT est non testé ici. Si le coût est inacceptable, arbitrer l'architecture avant d'imposer une dépendance ; aucun fork de moteur Rust ni exporteur DOCX inversé n'est proposé implicitement.

## Déclarations de polices — FONTS-001

Le schéma v1 reste inchangé. `nodes` conserve les `font` / `rFonts` de fontTable, styles, valeurs par défaut et runs, les attributs ascii/hAnsi/eastAsia/cs et de thème avec leur provenance ; les nœuds DrawingML conservent le schéma de fontes du thème. `relationships` relie fontTable aux parties incorporées ; `parts` conserve type, taille et SHA-256. Les substitutions Linux du viewer ne remplacent jamais les demandes Word dans ce transport déterministe. Le modèle applicatif `DocumentFonts`, préparé pour les futurs exporteurs, ajoute une analyse informative et les correspondances de rendu sans modifier le wire v1.

Les octets de fontes incorporées restent en quarantaine mémoire du processus ; ni `manifest.json` ni les ressources CLI ne les redistribuent. Ils ne sont pas des `SafeMedia`. Un futur consommateur voulant utiliser une fonte incorporée devra négocier un transport et ses politiques de droits séparément ; il ne peut pas déduire cette autorisation du présent inventaire. Aucun import ni export typographique avancé n’est qualifié par ce lot.
