# Archive — moteur externe abandonné

**Décision ENGINE-ABANDON-001 : ElixirOffice est définitivement écarté.** Le raccordement et les anciens réglages sont retirés du lecteur ; ce projet ne fait plus partie de la solution, de son build ou de la publication applicative. Les fichiers sources expérimentaux sont conservés pour l'audit, exclus de la compilation du lecteur et des tests actifs. Le build/publication du projet historique est bloqué. Les rapports, runtimes, polices et notices privés restent conservés localement ; ils ne doivent pas servir de paquet à installer ou diffuser.

La suite de cette page décrit **l'expérience historique ENGINE-001**, y compris ses anciennes commandes désormais inopérantes dans le lecteur courant. Elle ne constitue plus une procédure d'activation. La [stratégie indépendante](strategie-rendu-docx-independant.md) remplace cette piste. Aucun code des moteurs écartés ne doit être copié, traduit ou adapté dans le moteur C#.

## Description historique conservée

Le lecteur conserve son moteur C# et son comportement habituel. Le candidat externe est désactivé par défaut ; il ne fait pas partie d'une distribution publique et aucune installation utilisateur n'est remplacée par cette expérience.

`DocxEngine.Experimental` est un travailleur C#/.NET 10 indépendant de l'interface. Il utilise une copie DOCX en lecture seule, validée par DocxCore. Un environnement navigateur séparé charge uniquement les ressources Word et de conversion du candidat ElixirOffice vérifié ; l'application bureautique, ses interfaces, Electron, les SDK tableur/présentation et les services de compte/cloud ne sont pas nécessaires à ce chemin. Les binaires restent dans un runtime privé ignoré par Git. Le convertisseur et le SDK Word restent des binaires monolithiques comportant du code inutilisé ; aucune réduction absolue à toutes les fonctions de lecture n'est revendiquée.

## Activation explicite

Pour une qualification locale disposant du runtime privé préparé et vérifié :

```sh
LECTEUR_WORD_EXPERIMENTAL_RUNTIME=/chemin/prive/runtime \
LECTEUR_WORD_EXPERIMENTAL_ELIXIR=1 ./LecteurWord document.docx
```

Configurer seulement le chemin laisse le candidat désactivé et rend disponible sa case expérimentale. Le bouton de retour conserve le moteur C# ; un échec du travailleur laisse disponibles la composition C# ou la lecture de secours et leurs diagnostics. Ce réglage n'est pas enregistré comme préférence permanente. Il ne faut pas pointer vers une installation complète de suite bureautique.

Le runtime est une liste positive de fichiers et d'empreintes, accompagnée des licences et notices, et contient le travailleur autonome. Il n'est ni téléchargé ni installé automatiquement. Son assemblage local et ses preuves restent dans la qualification privée ; ces fichiers tiers ne doivent pas être ajoutés au dépôt.

## Résultats et limites

Les pages affichées sont celles du moteur candidat. Le mode est explicitement **paginé partiel** ; ni le nombre de pages, ni la fidélité à Microsoft Word, ni l'exhaustivité de chaque objet ne sont qualifiés par cette activation. La qualification Word exige des références issues du même DOCX.

La navigation, le zoom du raster et les miniatures utilisent les pages calculées. La recherche utilise le texte Unicode du modèle et les textes dessinés supplémentaires identifiés, avec comparaison canonique des accents sans modifier les textes conservés. Le texte est sélectionnable dans une vue native en lecture seule. La sélection directe et le surlignage précis sur la page raster, les contours des glyphes, les rotations et certaines ligatures restent non qualifiés. La provenance XML n'est attribuée qu'aux correspondances démontrées ; les autres restent inconnues.

L'inventaire, les champs, notes, révisions et diagnostics DocxCore restent accessibles. Les objets ont des références d'inventaire, mais toutes leurs géométries et leurs correspondances de page ne sont pas établies. Les exports existants ne sont pas étendus à ce moteur. Un glyphe `.notdef`, une image incomplète ou une correspondance inconnue ne doit pas être présenté comme une qualification positive.

L'adaptateur distingue les points de code hors BMP de leurs unités UTF-16. Il corrige l'extraction du SDK sans transformer les caractères privés effectivement présents dans le document. Le registre des médias convertis doit survivre aux réinscriptions d'URL de l'importeur ; une image manquante interdit l'adoption silencieuse d'un résultat incomplet.

## Confinement et cycle de vie

Le navigateur ne reçoit aucun montage de sortie hôte en écriture. Entrée et runtime sont exposés en lecture seule ; réseau et PID sont isolés, avec répertoires temporaires limités. Le superviseur impose délai, CPU, mémoire observée, nombre de pages et budgets de sortie ; l'annulation termine son propre arbre de processus. Les sorties sont nouvelles et adoptées atomiquement. Les macros et plugins sont explicitement désactivés.

Le sandbox Chromium habituel n'est pas utilisable dans l'environnement de qualification observé. Son remplacement expérimental par le confinement OS et les limites mesurées **n'est pas une qualification de sécurité pour la production**. La borne RSS agrégée est surveillée périodiquement ; la limite de données du noyau s'applique par processus et n'est pas une borne cgroup de toute la mémoire physique.

Les pages, miniatures et index précédents sont libérés avant une nouvelle inspection expérimentale. Sous pression mémoire, la récupération des segments managés inutilisés précède le contrôle DocxCore ; ses budgets ne sont pas augmentés. Cette récupération peut ajouter une pause mesurée à l'ouverture.

## Licences et diffusion

Cette intégration est autorisée pour l'expérience locale seulement. ElixirOffice, les composants sdkjs/core, les ressources graphiques, les polices, les données de césure, le navigateur et ses dépendances ont leurs propres licences et notices. La séparation en processus ne permet aucune conclusion juridique automatique sur leur combinaison avec le lecteur.

La licence du produit n'est pas modifiée. Toute adoption définitive, redistribution du runtime ou publication de cette intégration nécessite un GO distinct après revue des textes, des sources correspondantes, des mentions et des droits des polices. Les mesures, matrices, corpus et preuves de recette restent privés et ignorés par Git.
