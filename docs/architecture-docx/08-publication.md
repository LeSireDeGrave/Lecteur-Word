# Publication PDF, ODT, EPUB et NOVA

La publication utilise l’instantané DOCX inspecté, le modèle sémantique, la typographie et les pages natives. Le lecteur conserve le bail de composition pendant le dialogue et l’écriture. Le précontrôle indique les adaptations et les refus avec leur provenance. L’écrivain relit sa sortie temporaire avant de la renommer dans le dossier choisi. L’annulation, les erreurs et les dépassements de quota préservent la destination. Writer, EPUBCheck, MuPDF, Poppler et OpusNova interviennent dans la recette externe.

## Politiques de conservation

La fenêtre d’export propose le mode compatible par défaut. Une conversion équivalente garde le signalement de sa représentation. Une réserve indique une conséquence sur le contenu, le rendu, l’éditabilité ou l’actualisation. La fenêtre résume ces conséquences et permet d’afficher leur emplacement dans le DOCX. La conservation intégrale traite toute réserve comme un motif de refus. Les contrôles d’intégrité, de visibilité, de taille et de destination s’appliquent aux deux modes. L’API historique d’export en dossier garde sa politique intégrale et son profil d’admission.

ODT, EPUB et NOVA préparent une représentation unique en mémoire pour l’écriture atomique et la comparaison après réouverture. Les fichiers temporaires sont voisins de la destination. Le PDF utilise toujours son peintre de feuilles et la provenance des glyphes est ajoutée sur demande explicite.

## Conservation par format

| Élément | PDF natif | ODT 1.3 | EPUB 3 | NOVA 27 |
|---|---|---|---|---|
| Métadonnées | Titre, auteur et propriétés reconnues | Dublin Core et dates | Titre, auteur, langue et identifiant | Titre, auteur et dates |
| Titres et paragraphes | Texte et signets hiérarchiques | Titres, paragraphes, propriétés effectives et styles nommés | Titres XHTML et navigation hiérarchique | Paragraphes, titres et arborescence déclarée par les styles source |
| Caractères et polices | Sous-ensembles incorporés selon les droits et le profil, texte extractible | Styles et faces déclarées | CSS et faces déclarées | Plages scalaires et styles natifs |
| Listes | Marqueurs composés | Niveaux et numérotation qualifiés, libellés éditables en adaptation signalée | Listes structurées et libellés, styles CSS | Listes au motif et au retrait compatibles |
| Sections et coupures | Feuilles composées, document entier ou plage | Styles de page, marges et orientation. Certaines transitions sont converties en saut de page | Chapitres redistribuables. Les coupures papier sont adaptées au flux | Format de page commun et sauts avant paragraphe. Les écarts de géométrie sont signalés |
| Tableaux | Grille et bordures composées | Tables éditables, fusions, bordures, fonds | Tables XHTML, fusions, CSS | Blocs natifs et fusions. Ajustement des largeurs, marges et bordures selon les limites d’OpusNova. Repli éditable en texte si la grille est incompatible |
| Images | Images et transparences composées | PNG et JPEG admis, description disponible | PNG, JPEG et SVG admis, couverture source ou choisie | PNG et JPEG admis dans un paragraphe isolé, ressource et alternative |
| Vecteurs et SmartArt | Traits et formes du peintre natif | SVG ou image locale. Certains cadres conservent leur zone de texte | SVG ou image locale au point d’ancrage dans le flux | PNG transparent produit à partir de l’objet et de ses transformations, avec la description disponible |
| Graphiques et équations | Représentation disponible dans le peintre, aperçu passif qualifié | Figure autonome lorsque la représentation passive est comprise | Figure autonome lorsque la représentation passive est comprise | Ressource raster locale lorsque la représentation passive est comprise |
| Histoires auxiliaires | Placement, champs et dessins composés | Variantes reliées aux styles de page, texte et certains cadres éditables | Contenu utile inclus une fois, folios papier écartés | Contenus utiles repris une fois en annexes éditables, folios papier écartés |
| Notes | Appels et corps composés | Notes éditables, appels et corps | Appels, corps et liens de retour | Appels et corps natifs, numérotation OpusNova |
| Liens et signets | Annotations et destinations qualifiées | Hyperliens, signets par plage et références | Liens entre chapitres et vers les notes | Liens et signets du manuscrit |
| Sommaires et champs | Résultats du moteur | Numéros et totaux de page, renvois, sommaires et champs de propriété personnalisée texte actualisables dans le profil reconnu | Navigation XHTML et résultats utiles qualifiés | Renvois compatibles. Résultats calculés ou enregistrés repris avec mention de l’actualisation perdue |
| Commentaires et révisions | État de lecture et diagnostic d’historique | Annotations natives du corps dans le profil établi. État de lecture avec réserve sur les révisions | État de lecture avec réserve sur l’historique | État de lecture avec réserve sur l’historique |
| Glyphes historiques | Marque visible ou choix utilisateur selon la composition, provenance sur demande explicite | Code original et face déclarée selon le profil | Code original et face déclarée selon le profil | Code original dans les plages scalaires selon le profil |

La fidélité PDF est mesurée par rapport aux feuilles natives de Lecteur Word. Les options d’image couvrent l’encodage sans perte et deux niveaux JPEG pour les images opaques. L’option de provenance ajoute uniquement les enregistrements des glyphes concernés et leur origine dans le paquet source. Elle est désactivée à chaque ouverture du dialogue. [Métadonnées du moteur PDF Skia](https://api.skia.org/structSkPDF_1_1Metadata.html)

Les trois formats structurés réutilisent les objets composés. Les chemins SVG sont produits à partir des formes et métafichiers reconnus, y compris les traits, remplissages et clips. Le repli PNG est transparent, borné et limité à l’emprise de l’objet. Une zone de texte convertie en image garde les libellés disponibles dans sa description. Un objet sans représentation vérifiée bloque l’export. Le diagnostic localise l’objet et indique si une description est disponible.

Les variantes mesurées sont accessibles à l’export, y compris quand le choix des premières pages, des pages paires ou des sections ne produit aucune occurrence sur une feuille. L’ODT conserve les cadres compatibles dans les styles de page. Les coordonnées et dimensions proviennent de l’ancrage résolu, les groupes gardent leur ordre de peinture et le texte est écrit avec les styles et les champs ODF disponibles. Quand le paragraphe d’un cadre contient un champ dynamique de pagination, il peut être placé dans le flux de l’en-tête ou du pied pour rester actualisable dans Writer. Les transformations hors profil peuvent utiliser une image limitée à l’objet composé.

L’EPUB place le texte des cadres à l’ancre de lecture et garde leurs blocs, leurs styles pris en charge et les éléments graphiques utiles. Les en-têtes et pieds répétés de même rôle, avec un contenu et des styles vérifiés équivalents, sont repris une fois. Les folios propres à la pagination papier sont traités par la politique de lecture redistribuable.

Le format NOVA 27 propose des blocs natifs de tableau et d’image et des zones répétées de texte simple. Les zones répétées d’OpusNova utilisent une configuration de page commune et des variantes à l’échelle du livre. Les cadres, groupes et variantes par section du DOCX sont repris en paragraphes natifs éditables, en tableaux ou en images intégrées selon leur contenu. Une image située dans une note ou une cellule est réunie aux illustrations du manuscrit avec un repère à son emplacement d’origine.

Le précontrôle compare les histoires, paragraphes porteurs de texte, appels de note, tableaux et dessins actifs aux représentations enregistrées par l’écrivain. Il distingue les représentations natives, les conversions compatibles, les adaptations de présentation et les contenus résiduels localisés. La comparaison s’appuie sur les identités du modèle source. La réouverture du paquet vérifie les entrées produites, et la recette indépendante contrôle le comportement du fichier dans les lecteurs.

L’ODT utilise les styles de page, un document de styles et un corps éditable. L’EPUB assemble un paquet OPF, les chapitres XHTML, les feuilles de style et les ressources intégrées. Les ancres sont résolues après le découpage, y compris pour le retour d’une note vers son appel. Le texte alternatif déclaré est repris. L’incorporation de polices EPUB, les habillages complexes et certaines transitions physiques restent à qualifier. [OpenDocument 1.3](https://docs.oasis-open.org/office/OpenDocument/v1.3/os/part3-schema/OpenDocument-v1.3-os-part3-schema.html) · [EPUB 3.3](https://www.w3.org/TR/epub-33/)

## Contrat DOCX vers NOVA

Le précontrôle vérifie les limites du contrat v27 avant écriture. Une paire de dates incompatible avec l’ordre création puis modification provoque un refus localisé. Un lien externe hors contrat garde son libellé éditable avec une réserve. Un champ qui attend une valeur visible et dont le résultat n’a pu être établi bloque l’export. Les résultats de champ conservés comme texte gardent une réserve sur l’actualisation.

Les dates déclarées exigent un horodatage explicite avec fuseau. Les liens relatifs compatibles exploitent la base HTTP(S) déclarée dans les propriétés du DOCX. Un renvoi de page décimal vers un signet qualifié du manuscrit peut devenir un champ éditorial natif avec un libellé et une pagination OpusNova. Les références ordinaires utilisent le registre v1. Les champs éditoriaux emploient le registre v2 du même paquet NOVA 27. Les rectangles et polygones VML autonomes à géométrie explicite rejoignent le profil graphique existant.

La qualification NOVA utilise le décodeur et l’encodeur du dépôt OpusNova 27 en lecture seule. Elle compare le projet décodé et ses propriétés après sauvegarde, ainsi que les identités, plages scalaires et ressources. La recette GTK utilise des copies fictives dans un profil isolé. La fenêtre Exporter de Lecteur Word donne accès au fichier et à son dossier après la vérification de l’écriture atomique.

La version native 27 a été vérifiée dans le dépôt canonique OpusNova et sur GitHub à la révision `8633f8fb457396c9015304228694afc72ee7ded6`. Le manifeste relie le document actif, la bibliothèque de styles, la géométrie du projet, les ressources et les enregistrements auxiliaires. [Dépôt OpusNova](https://github.com/LeSireDeGrave/OpusNova/tree/8633f8fb457396c9015304228694afc72ee7ded6)

| Contrat NOVA | Source dans DocxFlow |
|---|---|
| `documents/{id}.txt`, `styles/{id}.json` v9 | Ordre des blocs, paragraphes, caractères et plages scalaires Unicode |
| `anchors/{id}.json` v3, `manuscript` v3 | Titres réels déclarés Partie, Chapitre ou Scène et leur position dans le manuscrit |
| `blocks/{id}.json` | Tableaux et images aux ancrages natifs, marqués par U+FFFC |
| `resources/{id}.bin` | PNG ou JPEG placé, identifié par son SHA-256 |
| `notes.json` v1 | Appels, types et corps de notes |
| `org.opusnova.references` v1 | Plages de liens et signets du manuscrit |
| `org.opusnova.page_styles` | Format et marges communs aux sections admises |

Les identifiants sont dérivés de l’empreinte du DOCX et de l’identité logique de chaque élément. Un titre éditorial correspond à un paragraphe source. La solidarité et les sauts proviennent des propriétés de ce paragraphe. Le profil utilise un document interne par DOCX. Une géométrie commune est utilisée lorsque les sections ont plusieurs dimensions. Les tableaux et images sont ajustés à la largeur d’écriture lorsque nécessaire. Les taquets sont bornés à la zone de texte. Les références non activables conservent leur libellé. Chaque adaptation qui modifie une propriété de conservation est une réserve localisée.
