# UX — référence et qualification

La référence utilisateur du 20 novembre 2026 est présente, conservée sans conversion :

`docs/maquettes/lecteur-word-maquette-clair-sombre.png`

Dimensions vérifiées : **1672 × 941**. SHA-256 :

`079276d8a88699c393622cd1500c1b753c6dc47cbcc03d75d00f4948105aa177`

Cette image est une maquette. Les captures de qualification restent locales et ignorées par Git ; les cas synthétiques sont distincts de la recette Linux du document réel expressément autorisé, exécutée sur une copie.

La référence ODG `Resources/OpenDarkDesignSystem.axaml` a été lue en place, sans modification ou copie de son code. Seule la direction de surfaces froides/ardoise, accents bleu-orange et commandes compactes a guidé le nouveau code. Les pictogrammes sont des géométries locales propres au projet.

## Réalisé

- Avalonia 12.1.6 / .NET 10 ; bandeau sobre avec fichier ouvert, commandes par icônes, aucun menu classique Fichier/Édition/Affichage.
- Ouvrir, rechercher, navigation repliable, précédent/suivant, zoom, export dans un seul pop-over, clair/sombre/système et FR/EN. Libellés accessibles et infobulles des icônes ; focus de recherche testé lors de la frappe.
- Papier blanc dans les trois thèmes : le thème de l'interface ne recolore pas le document. Miniatures des pages voisines issues de la même composition que la page principale.
- Compteur de pages uniquement après calcul interne. Bandeau « Composition expérimentale… sans conformité Word ». Un aperçu de texte ayant perdu une interprétation de style est explicitement signalé et ne devient pas un export fidèle.
- Deux modes explicites : pagination interne pour le profil composé et lecture simplifiée continue « Sans pages ». Le mode simplifié propose titres, tableaux, notes et repères, sans imiter des pages Word. Listes virtualisées, images décodées en cache borné, tableaux à défilement interne.
- Lecture simplifiée premium : surface de lecture claire/sombre, corps et interlignage confortables, espacements des titres et typographie d’écran. Les tailles/polices Word restent inventoriées ; cet affichage adapté ne constitue pas une restitution fidèle de leur géométrie.
- Les noms XML et messages techniques quittent le texte courant : petits repères contextuels avec infobulle et accès au détail. Les appels de notes dont la correspondance est vérifiée ouvrent une carte lisible, avec retour au passage. Les numéros sont ceux de la lecture ; une note incertaine garde un repère sans numéro inventé.
- Finitions READ-UX-003 : la note réserve un panneau à droite sur grande fenêtre, ou en bas sur petite fenêtre ; elle ne recouvre pas le passage. Le panneau reste ouvert lors d’un redimensionnement ou d’un changement de thème. Fermeture explicite, Échap et retour à l’appel rendent le focus au passage. Le repère d’origine est un soulignement discret de l’appel, sans sélection colorée de tout le paragraphe.
- Diagnostics : bilan utilisateur et conséquences pour la lecture en premier ; occurrences, codes, chemins et messages XML dans des détails repliés, également depuis les repères. Le rapport technique complet reste disponible.
- Typographie harmonisée dans le flux simplifié : corps de lecture 17 DIP / interligne 28, titres hiérarchisés (28, 24, 21 puis 19 DIP), marges réduites quand l’espace se resserre. Les valeurs du modèle documentaire sont conservées ; cette adaptation reste propre à l’interface.
- Les en-têtes/pieds sont accessibles dans une vue de section distincte du texte principal, avec indication des variantes et des liens à la section précédente. La recherche ouvre aussi les contenus des notes et des en-têtes/pieds.
- Les champs connus montrent leur valeur enregistrée si disponible, sans recalcul. Les repères distinguent valeur disponible (`ƒ`), champ inconnu (`ƒ?`) et valeur absente (`ƒ∅`). Les instructions complètes restent consultables dans les diagnostics. Aucun onglet Miniatures ni contrôle de page/zoom n’est présenté dans le flux sans pagination.
- Sommaire et signets ancrés sur les blocs du modèle ; recherche disponible dans le flux simplifié. Position précise dans un tableau long encore non qualifiée.
- Diagnostics regroupés par catégorie/composant/gravité/code ; informations secondaires repliées et accès depuis les repères. Rapport JSON complet enregistrable dans un fichier neuf, sans écrasement.
- Version compacte et icône de copie à droite de la barre d’état ; confirmation « Référence copiée ». L’icône d’information ouvre À propos : crédits officiels, licence à définir, notices embarquées et lien GitHub. Aucun menu supplémentaire.
- Raccourcis Ctrl+O, Ctrl+F, Ctrl+±, Ctrl+0, F9, Alt+←/→ et Échap. Contrôles de navigation/zoom et Ctrl+F testés offscreen ; recette physique AZERTY et portails de fichiers encore NT.
- Les résultats de recherche sont limités aux 100 premiers affichés, limite annoncée. Une structure non représentée n'est pas une recherche exhaustive qualifiée.
- Export : précontrôle du snapshot capturé avant le choix de destination, sortie neuve atomique, annulation ; les formats hors profil sont désactivés « Non qualifié ». Aucune conversion externe runtime.

## Non qualifié / décisions restantes

L'application n'est pas encore une visionneuse fidèle de DOCX généraux. Flotteurs, géométrie des tableaux, notes et sections ne suivent pas la mise en page Word. Les équations et corps de commentaires restent non représentés ; champs et révisions ne sont jamais recalculés/acceptés automatiquement. L’impression reste désactivée.

Les cas ciblés GUI couvrent clair/sombre, FR/EN, pages, zoom, recherche/focus, annulation et icônes accessibles, à **860 × 620**. À propos se replie jusqu’à **320 × 350** ; copies exactes et Tab/Échap sont vérifiés en Avalonia headless, avec captures 96/192 DPI. Une recette distincte ouvre une copie réelle sur X11 à 100 % et 150 % (facteur par processus), vérifie lecture et presse-papiers natifs, thèmes/langues et petites fenêtres. Le changement physique de moniteur reste NT. Tab et Échap sont également vérifiés sur X11 à 150 %, en clair/FR et sombre/EN, avec réception des événements et déplacement du focus/fermeture constatés. La recette complète du clavier AZERTY reste NT. Lecteur d’écran, portails natifs et changement du thème OS restent NT.

La référence du build est produite à chaque compilation : version sémantique, UTC, commit réel et état local si Git est disponible, cible ; aucun appel Git à l’exécution. L’heure locale et les informations du runtime sont affichées depuis cette identité. Une valeur absente reste « indisponible ». Le projet n’ayant aucun commit, il n’est pas possible d’afficher un identifiant Git actuel ; aucun n’est fabriqué. Les builds sans Git restent pris en charge.

L'argument de ligne de commande accepte un document local. Aucun fichier d'association existant n'est changé et aucune application par défaut n'est remplacée. Le candidat `.desktop` reste dans le paquet local ; son installation est une décision distincte.
