# Publication PDF, ODT, EPUB et NOVA

Le contrat de publication référence l’instantané DOCX inspecté, son modèle sémantique, sa typographie et ses pages natives. Le lecteur conserve le bail de composition pendant le dialogue et l’écriture. Les pages, fragments et ressources restent les objets du moteur. Les écrivains reconstruisent seulement les objets propres au format de sortie. Les liens et médias restent associés à leurs sources et aux identifiants de l’instantané.

Le précontrôle produit une décision par format et par plage. L’interface présente les diagnostics avant l’écriture. L’exporteur écrit un fichier temporaire voisin, le relit et le déplace par renommage après vérification. Le contrôle d’écrasement s’applique au fichier choisi. L’annulation et les quotas sont vérifiés jusqu’au renommage. L’ancienne API d’export en dossier conserve son périmètre d’entrée et utilise les mêmes écrivains. Le lecteur travaille avec des sorties locales et les ouvertures d’OpusNova et de Writer appartiennent à la recette.

## Matrice des éléments

**V** signifie vérifié sur un témoin ou le corpus. **P** indique une implémentation limitée ou une conversion signalée. **R** désigne un cas qui entraîne un refus avant publication. En PDF, « composé » désigne le rendu tel qu’il existe dans les pages natives, avec ses glyphes, vecteurs ou images. Les valeurs éditables ou les relations que le format conserve sont indiquées séparément.

| Élément | PDF natif | ODT | EPUB 3 | NOVA v27 |
|---|---|---|---|---|
| Métadonnées | V titre, auteur, valeurs reconnues | V propriétés Dublin Core et dates | V titre, langue, identifiant et propriétés reconnues | V titre et auteur, dates dans le manifeste |
| Titres et niveaux | V signets de navigation quand le niveau est connu | V titres éditables et niveaux | V XHTML et navigation par niveaux | R hiérarchie éditoriale |
| Paragraphes et styles | V glyphes et apparence composée | V paragraphes et styles de présentation automatiques | V paragraphes et CSS | V texte et plages de style |
| Polices, tailles et couleurs | V faces admissibles, sous-ensembles et texte extractible | V noms de faces et styles de texte | V noms de faces, couleurs et tailles en CSS | V face, taille, couleur et attributs du profil |
| Listes et numérotation | V marqueurs composés | P niveau simple, nombres décimaux ou puce standard | P même profil, listes XHTML | R listes |
| Tabulations et retraits | V positions composées | P tabulations et propriétés de paragraphe prises en charge | P retraits CSS, tabulations adaptées au support | P caractères de tabulation, R taquets et retraits de mise en page |
| Sections, marges, orientation | V dimensions sur chaque feuille | P disposition unique | P relecture adaptative, signalement des dimensions papier | P relecture éditable, signalement des dimensions papier |
| Sauts | V coupures physiques | P saut avant paragraphe, R cas internes complexes | R sauts de page explicites hors profil | R sauts de page et structure complexe |
| Tableaux, fusions et bordures | V composition et traits | V témoin de fusion horizontale, P variantes et bordures | V témoin de fusion horizontale, P variantes et bordures CSS | R tableaux |
| Images intégrées | V objets image et dimensions | V image PNG insérée, P JPEG natif | V PNG, texte alternatif et couverture choisie, P JPEG | R images |
| Dessins VML et DrawingML | V traits et polygones composés | P SVG dans le profil d’image en ligne | P ressource SVG | R |
| SmartArt | V dessin vectoriel composé | P formes, connecteurs et libellés en SVG en ligne | P même profil SVG | R |
| Graphiques et équations | P représentation déjà qualifiée par le moteur | R hors profil structuré | R hors profil structuré | R |
| MathType | V aperçu passif de Stage 2 | R conversion | R conversion | R |
| Zones de texte | V composition, ordre et rognage du papier | R conversion | R conversion | R |
| En-têtes et pieds | V ancrages, décorations et champs composés | R histoires auxiliaires non vides | R histoires auxiliaires non vides | R histoires auxiliaires non vides |
| Notes | V notes composées | P appels et corps éditables pris en charge dans le profil | P appels, corps et retours dans le profil | R notes |
| Hyperliens | V annotations externes et cibles internes qualifiées | V liens dans le corps | V liens XHTML | R liens |
| Signets et destinations | V destinations nommées et navigation | V témoin de signet et cible locale | V identifiants et fragments XHTML | R signets |
| Sommaires | V résultat composé et renvois vérifiés | R sommaire DOCX actif hors profil | V navigation EPUB produite à partir des titres, R champ de sommaire actif | R |
| Champs et numéros de page | V résultats de la composition | P PAGE et NUMPAGES simples, résultat des champs verrouillés pris en charge | P résultat des champs verrouillés | P résultat textuel des champs verrouillés |
| Commentaires et révisions | P état de lecture, historique signalé | R historique | R historique | R historique |
| Substitutions et glyphes | V rendu de la face résolue, pièce jointe de provenance pour les carrés et substitutions manuelles | P noms des faces et caractères Unicode du texte source | P noms des faces et caractères Unicode du texte source | P plages Unicode scalaires et faces du profil |
| Éléments hors profil | V diagnostic, refus des pages incomplètes et de l’incorporation de face non autorisée | V diagnostic de refus avant fichier | V diagnostic de refus avant fichier | V diagnostic de refus avant fichier |

La comparaison PDF qualifie la sortie du peintre natif. Les PDF gardent leurs dimensions, les annotations et les signets que le profil sait placer. La sélection et l’extraction sont confirmées pour le texte courant du corpus. La structure d’accessibilité PDF, les formes sérialisées comme objets éditables et l’extraction exacte des ligatures ou des glyphes de police symbolique restent hors de cette qualification. [Métadonnées du moteur PDF Skia](https://api.skia.org/structSkPDF_1_1Metadata.html)

Les profils ODT et EPUB passent par la structure sémantique, les styles effectifs et les géométries de cellules et dessins utilisables. Ils partagent la gestion des ressources et un contrôle de réouverture ZIP/XML. L’EPUB utilise le profil de lecture adaptatif, un paquet OPF, un sommaire XHTML et une feuille CSS. Le texte alternatif déclaré est conservé pour les images intégrées. L’incorporation de fontes redistribuables et les effets typographiques complexes restent hors profil. [OpenDocument 1.3](https://docs.oasis-open.org/office/OpenDocument/v1.3/os/part3-schema/OpenDocument-v1.3-os-part3-schema.html) · [EPUB 3.3](https://www.w3.org/TR/epub-33/)

L’encodeur NOVA suit les fichiers `manifest.json`, `documents/{id}.txt` et `styles/{id}.json` du schéma v27. Les plages de style utilisent les offsets scalaires Unicode. La version a été vérifiée à la révision `8633f8fb457396c9015304228694afc72ee7ded6` de `main`. Le profil éprouvé correspond au manuscrit éditable en paragraphes et styles simples. La forêt éditoriale, les ancrages natifs, les blocs, les notes et les ressources avancées entraînent un refus explicite pour ce format. [Dépôt OpusNova](https://github.com/LeSireDeGrave/OpusNova/tree/8633f8fb457396c9015304228694afc72ee7ded6)
