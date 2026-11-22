# UX — référence et qualification

La référence utilisateur du 20 novembre 2026 est présente, conservée sans conversion :

`docs/maquettes/lecteur-word-maquette-clair-sombre.png`

Dimensions vérifiées : **1672 × 941**. SHA-256 :

`079276d8a88699c393622cd1500c1b753c6dc47cbcc03d75d00f4948105aa177`

Cette image est une maquette. Les captures de qualification restent locales et ignorées par Git ; les cas synthétiques sont distincts de la recette Linux du document réel expressément autorisé, exécutée sur une copie.

La référence ODG `Resources/OpenDarkDesignSystem.axaml` a été lue en place, sans modification ou copie de son code. Seule la direction de surfaces froides/ardoise, accents bleu-orange et commandes compactes a guidé le nouveau code. Les pictogrammes sont des géométries locales propres au projet.

## Réalisé

- Avalonia 12.1.6 / .NET 10 ; bandeau sobre avec fichier ouvert, commandes par icônes, aucun menu classique Fichier/Édition/Affichage.
- Ouvrir, rechercher, navigation repliable, précédent/suivant, zoom, export dans un seul pop-over, Chocolat & Crème, Bleu Microsoft, Système et changement de langue par l’icône planète. Libellés accessibles et infobulles des icônes ; focus de recherche testé lors de la frappe.
- Papier blanc dans les trois thèmes : le thème de l'interface ne recolore pas le document. Miniatures des pages voisines issues de la même composition que la page principale.
- Compteur de pages uniquement après calcul interne. Bandeau « Composition expérimentale… sans conformité Word ». Un aperçu de texte ayant perdu une interprétation de style est explicitement signalé et ne devient pas un export fidèle.
- Deux modes explicites : pagination interne pour le profil composé et lecture simplifiée continue « Sans pages ». Le mode simplifié propose titres, tableaux, notes et repères, sans imiter des pages Word. Listes virtualisées et images décodées en cache borné. Les tableaux du profil natif partagent la géométrie paginée, avec défilement horizontal si nécessaire et défilement vertical au niveau du document.
- Lecture simplifiée premium : surface de lecture stable dans les deux thèmes, corps et interlignage confortables, espacements des titres et typographie d’écran. Les tailles/polices Word restent inventoriées ; cet affichage adapté ne constitue pas une restitution fidèle de leur géométrie.
- Les noms XML et messages techniques quittent le texte courant : petits repères contextuels avec infobulle et accès au détail. Les appels de notes dont la correspondance est vérifiée ouvrent une carte lisible, avec retour au passage. Les numéros sont ceux de la lecture ; une note incertaine garde un repère sans numéro inventé.
- Finitions READ-UX-003 : la note réserve un panneau à droite sur grande fenêtre, ou en bas sur petite fenêtre ; elle ne recouvre pas le passage. Le panneau reste ouvert lors d’un redimensionnement ou d’un changement de thème. Fermeture explicite, Échap et retour à l’appel rendent le focus au passage. Le repère d’origine est un soulignement discret de l’appel, sans sélection colorée de tout le paragraphe.
- Diagnostics : bilan utilisateur et conséquences pour la lecture en premier ; occurrences, codes, chemins et messages XML dans des détails repliés, également depuis les repères. Le rapport technique complet reste disponible.
- Typographie harmonisée dans le flux simplifié : corps de lecture 17 DIP / interligne 28, titres hiérarchisés (28, 24, 21 puis 19 DIP), marges réduites quand l’espace se resserre. Les valeurs du modèle documentaire sont conservées ; cette adaptation reste propre à l’interface.
- Les en-têtes/pieds sont accessibles dans une vue de section distincte du texte principal, avec indication des variantes et des liens à la section précédente. La recherche ouvre aussi les contenus des notes et des en-têtes/pieds.
- Les champs connus montrent leur valeur enregistrée si disponible, sans recalcul. Les repères distinguent valeur disponible (`ƒ`), champ inconnu (`ƒ?`) et valeur absente (`ƒ∅`). Les instructions complètes restent consultables dans les diagnostics. Le flux sans pagination masque les miniatures et les commandes de page. Le zoom y adapte la taille du texte.
- Sommaire et signets ancrés sur les blocs du modèle ; recherche disponible dans le flux simplifié. Position précise dans un tableau long encore non qualifiée.
- Diagnostics regroupés par catégorie/composant/gravité/code ; informations secondaires repliées et accès depuis les repères. Rapport JSON complet enregistrable dans un fichier neuf, sans écrasement.
- Version compacte et icône de copie à droite de la barre d’état ; confirmation « Référence copiée ». L’icône d’information ouvre À propos : crédits officiels, licence à définir, notices embarquées et lien GitHub. Aucun menu supplémentaire.
- Raccourcis Ctrl+O, Ctrl+F, Ctrl+±, Ctrl+0, F9, Alt+←/→ et Échap. Contrôles de navigation/zoom et Ctrl+F testés offscreen ; recette physique AZERTY et portails de fichiers encore NT.
- Les résultats de recherche sont limités aux 100 premiers affichés, limite annoncée. Une structure non représentée n'est pas une recherche exhaustive qualifiée.
- Export : précontrôle du snapshot capturé avant le choix de destination, sortie neuve atomique, annulation ; les formats hors profil sont désactivés « Non qualifié ». Aucune conversion externe runtime.

## Non qualifié / décisions restantes

L'application n'est pas encore une visionneuse fidèle de DOCX généraux. Flotteurs, géométrie des tableaux, notes et sections ne suivent pas la mise en page Word. Les équations et corps de commentaires restent non représentés ; champs et révisions ne sont jamais recalculés/acceptés automatiquement. L’impression reste désactivée.

Les cas ciblés GUI couvrent Chocolat & Crème et Bleu Microsoft, FR/EN, pages, zoom, recherche/focus, annulation et icônes accessibles, à **860 × 620**. À propos se replie jusqu’à **320 × 350** ; copies exactes et Tab/Échap sont vérifiés en Avalonia headless, avec captures 96/192 DPI. Une recette distincte ouvre une copie réelle sur X11 à 100 % et 150 % (facteur par processus), vérifie lecture et presse-papiers natifs, thèmes/langues et petites fenêtres. Le changement physique de moniteur reste NT. La recette historique a également vérifié Tab et Échap sur X11 à 150 % dans les deux thèmes alors disponibles, avec réception des événements et déplacement du focus/fermeture constatés. La recette complète du clavier AZERTY reste NT. Lecteur d’écran, portails natifs et changement du thème OS restent NT.

La référence du build est produite à chaque compilation : version sémantique, UTC, commit réel et état local si Git est disponible, cible ; aucun appel Git à l’exécution. L’heure locale et les informations du runtime sont affichées depuis cette identité. Une valeur absente reste « indisponible ». Le projet n’ayant aucun commit, il n’est pas possible d’afficher un identifiant Git actuel ; aucun n’est fabriqué. Les builds sans Git restent pris en charge.

L'argument de ligne de commande accepte un document local. Aucun fichier d'association existant n'est changé et aucune application par défaut n'est remplacée. Le candidat `.desktop` reste dans le paquet local ; son installation est une décision distincte.

## UX-OPUS-001 — Consultation et éléments du document

Chocolat & Crème reprend les tons de la capture fournie par le propriétaire. Bleu Microsoft applique une palette bleue à la même interface de lecture. Les barres, les panneaux et les icônes sont implémentés en Avalonia. La page calculée, la lecture continue et les notices de build restent accessibles depuis la même fenêtre.

| Contrôle | Usage |
| --- | --- |
| Barre haute | Ouvrir, propriétés, export existant, sommaire, recherche, signets, navigation de page et modes de lecture disponibles. Les commandes de règle et d’ajustement physique apparaissent avec une page calculée. L’icône planète bascule entre le français et l’anglais. |
| Barre basse | DOCX en lecture seule, page calculée ou lecture continue, statistiques du corps lisible, langue déclarée si disponible, zoom, propriétés, version et copie de la référence. Le zoom du flux adapte le corps et l’interlignage. |
| Navigation · F9 | Sommaire hiérarchique repliable, signets, recherche, miniatures lorsque disponibles et position courante. |
| Éléments · F8 | Tiroir fermé par défaut. Les filtres et les propriétés exploitent l’index du snapshot courant. Les séparateurs permettent de modifier la largeur. En fenêtre étroite, le dernier panneau demandé reçoit la place disponible. |
| Règle · F7 | Règle de consultation en cm ou mm, avec dimensions, marges et repères du paragraphe retenu. Cliquez sur une ligne pour sélectionner le paragraphe correspondant. Les repères suivent le zoom, le défilement horizontal et la section. |
| Marques · ¶ | Espaces, espaces insécables identifiés, tabulations, fins de paragraphes et sauts source. Le passage peut être copié par le menu contextuel du flux. La recherche et la copie interrogent le modèle de lecture. |

Les statistiques couvrent les paragraphes et cellules du corps lisible, valeurs de champs affichées comprises, avec exclusion des notes, en-têtes et pieds. Les mots sont comptés par séquences de lettres ou chiffres avec leurs marques combinantes. Les caractères correspondent aux points de code du texte de ce périmètre, séparateurs explicites compris. La langue provient des métadonnées déclarées ou de la langue par défaut des styles.

Le tiroir distingue la page calculée, le bloc de lecture, le bloc contenant et le contenu auxiliaire consultable. Une localisation de bloc contenant vise le bloc supérieur de la lecture continue. Les propriétés indiquent le chemin structurel, l’identité dans le snapshot et l’état connu. Les branches inactives restent consultables dans les diagnostics techniques. Les vignettes, l’index et les marques de page sont propres au document courant. Les limites d’affichage des marques sont signalées pour la page ou le passage concerné.

La pagination C# garde ses critères de qualification. La règle est une projection des géométries disponibles et la fidélité Microsoft Word reste à qualifier. Le changement physique de moniteur et la lecture avec un lecteur d’écran demandent une recette distincte.

### Reprise UX-OPUS-001

Le tiroir offre deux filtres distincts pour les tableaux et les cellules. Une cellule peut être recherchée par aperçu ou identité. Ses propriétés indiquent sa ligne, sa colonne et, lorsque le modèle les fournit, son étendue et sa cellule de fusion d’origine. Une cellule placée par le compositeur ouvre la page calculée. Dans un tableau directement atteint dans le flux, la commande amène la cellule dans la zone visible et lui donne le focus. Pour les autres structures, l’interface indique le bloc contenant ou l’absence de localisation. Le bandeau indique l’indisponibilité de la règle quand la lecture est continue.

Les marques de mise en forme utilisent les identifiants SEMANTIC-001 partagés avec TEXT-001. Les marques de caractères sont associées à la plage UTF‑16 du vrai nœud texte. Les marques de structure portent la source de la tabulation, du saut, du paragraphe ou de la section. La superposition paginée utilise les coordonnées du compositeur existant. La recherche et la copie gardent le texte du modèle.

Dans **Diagnostics → Couverture typographique**, l’analyse à la demande réutilise le snapshot déjà ouvert. Les fragments sans mesure montrent leurs scalaires Unicode et donnent accès au texte original sélectionnable, à ses plages UTF‑16 et aux diagnostics de mesure. Les passages ou notes vérifiés peuvent être rejoints. La couverture calculée par TEXT-001 reste distincte de la pagination et de l’affichage du lecteur.


### UX-THEME-002

Les choix sont **Chocolat & Crème**, **Bleu Microsoft** et **Système**. Système associe la préférence claire du bureau à Chocolat & Crème et sa préférence sombre à Bleu Microsoft. Quand le bureau ne fournit pas cette information, Chocolat & Crème s’applique. Les deux thèmes utilisent une surface de lecture stable et le même rendu DOCX.

Le choix est enregistré au niveau utilisateur dans `lecteur-word/settings.json` sous `XDG_CONFIG_HOME`, ou sous `~/.config` par défaut. Les valeurs historiques du thème sombre, dont le code `2`, sont migrées vers `blue`. La migration conserve les autres clés et valeurs. Les couleurs bleues utilisent une variante Avalonia dérivée de Light et des ressources de contrôle locales. **Bleu Microsoft** désigne la palette de l’interface de Lecteur Word. [Variantes de thèmes Avalonia](https://docs.avaloniaui.net/docs/styling/theme-variants).
