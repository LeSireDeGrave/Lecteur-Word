# Polices et typographie — FONTS-001

Le compositeur paginé utilise les faces réellement chargées, sans modifier le profil des exporteurs. Les substitutions sont explicites ; aucune égalité de métriques ni de pagination avec Microsoft Word n’est annoncée.

## Matrice de couverture

| Élément | Lecture / conservation | Composition qualifiée |
|---|---|---|
| `fontTable.xml` lié au document | Noms, altName, famille, charset, pitch, relations d’incorporation et provenance | Classification utilisable pour choisir un repli ; disponibilité vérifiée sur Linux |
| Valeurs par défaut, styles de paragraphe/caractère, `basedOn`, surcharges | Héritage et provenance des sélections conservés | Familles, taille, gras, italique, souligné simple et couleur du profil existant |
| `rFonts` ascii / hAnsi | Références directes et de thème, avec attribut et source | Sélection selon les segments ASCII / latin étendu ; plusieurs faces au sein du même paragraphe |
| `rFonts` eastAsia / cs et variantes de thème | Conservées dans les slots, le rapport et le manifeste v1 | Écritures et sélections linguistiques complexes non qualifiées, refus explicite |
| Thème major/minor | Latin, EA, CS et déclarations complémentaires par script conservés | major/minor Ascii et HAnsi résolus vers la famille latine du thème ; thème absent/ambigu : repli signalé |
| bCs / iCs / szCs | Valeurs héritées conservées, distinctes des valeurs latines | Non appliquées au texte latin ; sélection forcée `cs` / `rtl` non qualifiée |
| Famille installée | Détection par le gestionnaire Skia Linux et vérification de la face | Face exploitable prioritaire ; famille, graisse, inclinaison et empreinte réellement chargées enregistrées |
| Famille ou glyphe absent | Demande et provenance conservées | Substitution couvrant le segment, avec motif et correspondance demandée → utilisée |
| TTF statique incorporée, brute ou obfusquée | Partie interne, hash, clé, nom, face, fsType et décision | Chargement mémoire temporaire si format, identité, droits techniques et couverture acceptés |
| TTC, CFF, WOFF, EOT, fontes couleur ou variables incorporées | Inventaire et refus explicite | Non qualifié |
| Crénage Word, ligatures OpenType, tracking, effets, scripts complexes | Propriétés brutes conservées ; propriétés essentielles hors profil bloquantes | Non qualifié ; aucune promesse de shaping avancé |

Les règles de priorité entre `rFonts` directs/thématiques et niveaux de styles sont appliquées par slot : un niveau ultérieur remplace la sélection antérieure du même slot ; au même niveau la référence de thème est prioritaire. Les demandes originales restent toutes dans l’inventaire. Si un slot n’a aucune valeur dans la cascade, le repli de lecture DejaVu Serif est signalé ; il n’emprunte pas silencieusement la valeur d’un autre slot. Les détails de sélection Word dépassant le profil sont décrits dans les [notes Microsoft sur rFonts](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/aef3c9a6-5d6c-434b-90b7-85e761fd8e62).

## Résolution, métriques et stabilité

Ordre : famille Linux demandée exploitable et couvrante ; face incorporée correspondante autorisée ; familles de repli disponibles selon classification serif/sans/mono ; repli de couverture Skia ; DejaVu Serif libre embarquée. Une police incapable de couvrir le segment ne produit pas de glyphes manquants silencieux. Sans solution le moteur refuse la pagination, tout en conservant la lecture simplifiée dans son périmètre.

Les tailles restent celles du document ; les replis n’affirment aucune compatibilité métrique. Le compositeur conserve un instantané mémoire de la face choisie et son index, et réutilise ces mêmes données pour mesurer puis dessiner. Le nom interne, la graisse et l’inclinaison sont vérifiés après rechargement ; aucun gras ou italique synthétique n’est inventé. Une instance variable/collection installée qui ne survit pas fidèlement à cet instantané est écartée et diagnostiquée par le repli.

Budgets supplémentaires : 64 recherches de faces, instantanés système de 8 MiB par face / 32 MiB au total, 50 000 usages et 15 secondes pour la résolution. Le budget de composition LAYOUT-001A reste distinct. Aucun téléchargement ni installation automatique. Les fontes du système restent sur la machine et ne sont pas copiées dans le paquet de livraison.

## Incorporation et droits

Le cœur garde au maximum 16 parties de fontes en quarantaine mémoire, 8 MiB par partie et 16 MiB au total, issues de l’instantané DOCX inspecté. Dépassement : déclaration inventoriée, données non exposées au lecteur typographique. Les cibles externes ne sont jamais chargées. Après contrôle du hash et désobfuscation, le lecteur vérifie les bornes et non-superpositions des tables SFNT, les tables métriques requises et les formats exclus avant de confier la face à Skia. Ce précontrôle n’est pas un validateur complet de fontes ni une sandbox de parseur natif.

Les notices de copyright, licence et URL de licence des enregistrements Unicode de la table `name` sont conservées ; les liens ne sont jamais chargés. Les indicateurs techniques `OS/2.fsType` sont lus : niveaux installable, aperçu/impression en lecture seule, ou modifiable ; les fontes restent ici temporaires et le DOCX en lecture seule. Licence restreinte, bitmap seul, niveaux contradictoires/bits inconnus et sous-ensemble déclaré incompatible avec l’interdiction de sous-ensemble sont refusés. Voir la [spécification OpenType OS/2](https://learn.microsoft.com/en-us/typography/opentype/spec/os2#fstype). Ces indicateurs ne constituent pas une vérification juridique exhaustive d’une licence ; le logiciel n’accorde aucun droit nouveau.

Aucune extraction sur disque, installation, incorporation dans un export ou redistribution de ces données. Les objets natifs sont libérés après mesure/dessin, les instantanés mémoire restent attachés au document et deviennent récupérables à sa fermeture/remplacement. Les indicateurs fsType et notices déclarées ne constituent pas une vérification juridique indépendante de la licence ou de l’autorisation de l’auteur du DOCX. Aucune redistribution n’est autorisée par cette analyse. L’inventaire et les rapports JSON excluent les octets des fontes. Le transport CLI continue de n’exposer que ses ressources PNG admises.

## Interface et contrat OpusNova

Propriétés → **Polices du document** : familles déclarées/référencées, disponibles sous Linux, absentes, faces sélectionnées, substitutions et fontes incorporées avec leur décision. Les conséquences possibles sur lignes/pages sont affichées. Les listes visuelles sont limitées à 100 entrées chacune ; le rapport conserve les détails. Le mode simplifié garde sa typographie de lecture, distincte des fontes du moteur paginé.

Le modèle `DocumentFonts` prépare les futurs consommateurs avec déclarations, thèmes, demandes, sources, décisions d’incorporation et usages résolus. Le contrat d’échange JSON v1 n’est pas modifié : ses nœuds, attributs, chaînes de styles, relations et hashes conservent déjà les déclarations Word. La résolution dépendante de Linux est un diagnostic du lecteur, pas une réécriture de la demande dans le contrat. `importReady` reste false. Aucun adaptateur ou fichier d’OpusNova n’est modifié.

PDF/ODT/EPUB3 restent au profil simple antérieur et ne réutilisent pas encore ces fontes : leur extension exige une qualification spécifique. Une nouvelle page visible ne vaut pas autorisation d’export.

## HarfBuzz et limites de fidélité

HarfBuzz a été examiné : il transforme les caractères en glyphes positionnés et tient compte des règles propres aux scripts et fonctionnalités de fontes ([manuel officiel](https://harfbuzz.github.io/shaping-concepts.html)). Il est déjà transitif à la GUI Avalonia, mais n’est pas ajouté au compositeur de ce lot. Le moteur conserve Skia et le profil latin simple ; scripts complexes, signes combinants hors profil et propriétés de shaping non implémentées restent refusés. Un prochain lot de shaping devra qualifier segmentation, positions, direction, coupures et liens aux sources ensemble.

Les différences de fonte/version, l’absence de shaping Word complet, le calcul des lignes et les règles de compatibilité peuvent modifier les pages. Aucun rendu Word de référence n’a été fourni ; la comparaison visuelle Word reste non testée.

## Qualification ciblée

Sélectionner explicitement un cas : `fonts-model`, `fonts-resolution`, `fonts-pagination`, `fonts-embedded`, `fonts-gui`, `fonts-real` (copie contrôlée explicitement fournie via `LECTEUR_WORD_QA_INPUT`) ou `native-fonts`. Le dernier utilise une vraie fenêtre X11 et ImageMagick `import` pour capturer l’affichage, sans la double transformation DPI des captures RenderTargetBitmap d’images imbriquées.

Les témoins sont synthétiques et restent locaux. Le test d’incorporation dérive temporairement la ressource libre DejaVu Serif : noms de famille modifiés et indicateurs fsType fabriqués pour tester les politiques, sans prétendre qu’ils décrivent les droits de la fonte originale. Les données dérivées ne sont pas distribuées. Les originaux et le contrat v1 sont contrôlés par empreintes et assertions.
