# Publication PDF, ODT, EPUB et NOVA

La publication utilise l’instantané DOCX inspecté, le modèle sémantique, la typographie et les pages natives. Le lecteur conserve le bail de composition pendant le dialogue et l’écriture. Le précontrôle indique les adaptations et les refus avec leur provenance. L’écrivain relit sa sortie temporaire avant de la renommer dans le dossier choisi. L’annulation, les erreurs et les dépassements de quota préservent la destination. Writer, EPUBCheck, MuPDF, Poppler et OpusNova interviennent dans la recette externe.

## Conservation par format

| Élément | PDF natif | ODT 1.3 | EPUB 3 | NOVA 27 |
|---|---|---|---|---|
| Métadonnées | Titre, auteur et propriétés reconnues | Dublin Core et dates | Titre, auteur, langue et identifiant | Titre, auteur et dates |
| Titres et paragraphes | Texte et signets hiérarchiques | Titres, paragraphes, propriétés effectives et styles nommés | Titres XHTML et navigation hiérarchique | Paragraphes, titres et arborescence déclarée par les styles source |
| Caractères et polices | Sous-ensembles incorporés selon les droits et le profil, texte extractible | Styles et faces déclarées | CSS et faces déclarées | Plages scalaires et styles natifs |
| Listes | Marqueurs composés | Niveaux et numérotation qualifiés, libellés éditables en adaptation signalée | Listes structurées et libellés, styles CSS | Listes au motif et au retrait compatibles |
| Sections et coupures | Feuilles composées, document entier ou plage | Styles de page, marges, orientation et transitions qualifiées | Chapitres redistribuables, coupures papier adaptées | Géométrie commune et sauts avant paragraphe qualifiés |
| Tableaux | Grille et bordures composées | Tables éditables, fusions, bordures, fonds | Tables XHTML, fusions, CSS | Blocs natifs, fusions, bordures uniformes, marges compatibles |
| Images | Images et transparences composées | PNG et JPEG admis, description disponible | PNG, JPEG et SVG admis, couverture source ou choisie | PNG et JPEG admis dans un paragraphe isolé, ressource et alternative |
| Vecteurs et SmartArt | Traits et formes du peintre natif | SVG en ligne ou dans une zone compatible | SVG intégré, profil des dessins en ligne | Refus des dessins complexes |
| Graphiques et équations | Représentation disponible dans le peintre, aperçu passif qualifié | Refus hors profil | Refus hors profil | Refus hors profil |
| Histoires auxiliaires | Placement, champs et dessins composés | Variantes reliées aux styles de page, texte et certains cadres éditables | Contenu utile inclus une fois, folios papier écartés | Refus des histoires utiles hors profil |
| Notes | Appels et corps composés | Notes éditables, appels et corps | Appels, corps et liens de retour | Appels et corps natifs, numérotation OpusNova |
| Liens et signets | Annotations et destinations qualifiées | Hyperliens, signets par plage et références | Liens entre chapitres et vers les notes | Liens et signets du manuscrit |
| Sommaires et champs | Résultats du moteur | Numéros et totaux de page, renvois dynamiques, entrées de sommaire et résultats qualifiés | Navigation XHTML et résultats utiles qualifiés | Renvois compatibles et texte vérifié, refus des champs actifs hors profil |
| Commentaires et révisions | État de lecture et diagnostic d’historique | Refus de l’historique hors profil | Refus de l’historique hors profil | Refus de l’historique hors profil |
| Glyphes historiques | Marque visible ou choix utilisateur selon la composition, provenance sur demande explicite | Code original et face déclarée selon le profil | Code original et face déclarée selon le profil | Code original dans les plages scalaires selon le profil |

La fidélité PDF est mesurée par rapport aux feuilles natives de Lecteur Word. Les options d’image couvrent l’encodage sans perte et deux niveaux JPEG pour les images opaques. L’option de provenance ajoute uniquement les enregistrements des glyphes concernés et leur origine dans le paquet source. Elle est désactivée à chaque ouverture du dialogue. [Métadonnées du moteur PDF Skia](https://api.skia.org/structSkPDF_1_1Metadata.html)

L’ODT utilise les styles de page, un document de styles et un corps éditable. L’EPUB assemble un paquet OPF, les chapitres XHTML, les feuilles de style et les ressources intégrées. Les ancres sont résolues après le découpage, y compris pour le retour d’une note vers son appel. Le texte alternatif déclaré est repris. L’incorporation de polices EPUB, les habillages complexes et certaines transitions physiques restent à qualifier. [OpenDocument 1.3](https://docs.oasis-open.org/office/OpenDocument/v1.3/os/part3-schema/OpenDocument-v1.3-os-part3-schema.html) · [EPUB 3.3](https://www.w3.org/TR/epub-33/)

## Contrat DOCX vers NOVA

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

Les identifiants sont dérivés de l’empreinte du DOCX et de l’identité logique de chaque élément. Un titre éditorial correspond à un paragraphe source. La solidarité et les sauts proviennent des propriétés de ce paragraphe. Le profil utilise un document interne par DOCX. Les changements de géométrie entre sections, les références internes aux corps annexes et les objets pour lesquels la représentation native est insuffisante donnent lieu à un refus localisé.
