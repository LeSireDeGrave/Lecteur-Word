# Compatibilité de composition — LAYOUT-001C

Cette extension utilise DOCX-CORE, DOCX-FLOW et les polices figées existantes. La pagination reste expérimentale et distincte du profil des exporteurs. Elle ne mesure pas la fidélité à Microsoft Word. Les résultats sur corpus privés et les captures restent locaux.

## Paragraphes et sections

La justification `both` distribue l’espace entre les mots sur les lignes issues du retour automatique, conserve les caractères et leurs offsets, et laisse la dernière ligne alignée à gauche. Les combinaisons avec saut de ligne manuel, image ou tabulation sur une ligne à justifier restent refusées. L’alignement distribué et les scripts nécessitant une composition complexe restent hors profil.

Les retraits gauche/droite et première ligne peuvent être négatifs dans les limites du profil ; les contrôles de débordement physique restent actifs. `start` et `end` sont interprétés dans le profil latin gauche-droite. L’espacement contextuel supprime l’espacement propre du paragraphe concerné entre voisins de même style et même section, sans franchir un tableau ou une cellule. Les autres espacements adjacents utilisent toujours le maximum.

Les propriétés `pPr/rPr` s’appliquent à la marque de paragraphe et aux métriques de ligne, sans remplacer le format des runs. Leur cascade suit les valeurs par défaut, les styles de base, les styles dérivés puis les propriétés directes. Les couleurs de thème de texte sans teinte/ombrage utilisent le thème et son mapping ; un thème absent ou transformé reste diagnostiqué et bloquant.

Les tabulations gauches peuvent revenir à la ligne, y compris les suites de tabulations. Le texte et chaque tabulation restent dans les lignes avec leur provenance. Les retours automatiques complexes après tabulation droite/centrée/décimale restent refusés. Signets, marques de correction et identifiants informatifs restent inventoriés. Les libellés de liens sont composés sans charger leur cible ; ce rendu n’ajoute pas de navigation cliquable aux pages calculées.

Une `docGrid` sans type, ou de type `default`, est inactive. Une grille active reste bloquante. Les sections `nextPage` et les sections `continuous` à géométrie identique gardent leur périmètre antérieur ; départs pairs/impairs, colonnes et changement continu de géométrie restent hors profil. La césure automatique et les marges miroir actives sont refusées.

## Tableaux

L’AutoFit simple, explicite ou implicite, mesure les mots, lignes et images avec les polices réellement résolues. Il construit des minima par colonne, respecte les cellules couvrant plusieurs colonnes et arbitre les préférences de largeur sous la limite disponible. Les largeurs de tableau en points ou pourcentage, et les préférences de cellules en points/pourcentage, sont conservées. Une grille automatique absente ou non dimensionnée peut être initialisée à partir de la première ligne ; chaque ligne doit ensuite couvrir exactement cette grille.

Les colonnes calculées sont diagnostiquées avec provenance. Un minimum incompatible avec la largeur disponible provoque un refus chiffré. Cette méthode bornée ne prétend pas reproduire tous les arbitrages de l’algorithme AutoFit de Word. Les tabulations dans les cellules AutoFit, tableaux imbriqués, styles conditionnels et propriétés de lignes provenant d’un style restent refusés.

Les styles de tableau généraux hérités peuvent fournir propriétés de tableau, marges, bordures, fond de cellule et format des paragraphes/runs. Les bordures simples, doubles, pointillées et tiretées sont représentées. Les conflits entre bordures adjacentes restent refusés. Les fusions et en-têtes répétés gardent leurs identifiants et provenances ; lignes et groupes fusionnés restent indivisibles.

## JPEG intégrés

DOCX-CORE reconnaît les dimensions des JPEG baseline 8 bits, à un ou trois composants, sans dépendance graphique. Cette reconnaissance d’en-tête ne vaut pas validation du décodage. DOCX-FLOW contrôle les segments, exige un scan baseline complet, retire les métadonnées avant décodage natif et exige un résultat de décodage complet. Les huit orientations EXIF sont appliquées explicitement dans une copie PNG de rendu. L’indicateur Adobe de transformation des couleurs est conservé lorsqu’il appartient au profil accepté.

Le modèle conserve dimensions encodées, dimensions orientées, orientation, boîte Word en EMU et SHA-256 source. Une différence de proportions entre la boîte Word et les pixels orientés est signalée ; la boîte Word reste prioritaire. Les JPEG progressifs, CMYK, tronqués, externes ou hors budget ne sont pas composés. Les profils colorimétriques/métadonnées omis restent dans l’original et l’inventaire ; leur fidélité colorimétrique n’est pas qualifiée.

Limites par image : 8 Mio d’entrée, dimensions de 8 192 pixels au maximum, 8 millions de pixels ; copie de rendu plafonnée à 8 Mio et contrôle de durée de 1,5 s autour du décodage. Ce contrôle ne transforme pas le décodeur natif en processus isolé. Le budget documentaire de 64 ressources et 24 millions de pixels reste actif. PNG conserve son validateur existant. Les flottants, recadrages et effets hors profil ne sont pas convertis en images intégrées ordinaires.

## Structures essentielles et vérification

Notes utilisateur, champs, révisions, en-têtes/pieds et objets spécialisés restent bloquants lorsqu’ils ne peuvent pas être composés intégralement. Une partie de notes qui ne contient que les séparateurs de service reconnus peut être identifiée comme dépourvue de note utilisateur ; cette exception ne retire ni note ni appel. L’inventaire et la lecture simplifiée restent disponibles suivant leurs profils propres.

Sources de tests synthétiques : `CompatibilityCases.cs`, avec les cas explicites `compat-paragraphs`, `compat-spacing`, `compat-tab-wrap`, `compat-autofit`, `compat-table-styles`, `compat-jpeg`, `compat-jpeg-safety` et `compat-boundaries`. `compat-corpus` exige les onze copies et contrôle leurs empreintes, le texte de l’inventaire, les lignes produites et les refus. `native-compat` exige une copie explicitement désignée et vérifie l’échelle réellement observée. Chaque cas se lance séparément ; ces sources ne sont pas un rapport de recette.

Les contrats OpusNova, le manifeste v1 et les précontrôles PDF/ODT/EPUB3 ne sont pas étendus. La comparaison visuelle Word nécessite une référence exploitable du même DOCX ; sans celle-ci, son statut reste NT.

## Références de format

- [Espacement contextuel Microsoft](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.contextualspacing?view=openxml-3.0.1).
- [Largeur préférée de tableau](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.tablewidth?view=openxml-3.0.1) et [largeur préférée de cellule](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.tablecellwidth?view=openxml-2.20.0).
- [Grille de document Microsoft](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.docgrid?view=openxml-3.0.1).
