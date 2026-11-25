# Confidentialité Git locale

Installer les hooks avec `python3 tools/git-privacy/install.py` depuis le dépôt. L’installation utilise Git et Python 3. Elle laisse la configuration Git et les hooks tiers en place. Elle refuse de remplacer un hook tiers ou de passer outre un `core.hooksPath` personnalisé. Réinstaller après chaque changement validé du contrôleur ou de sa politique.

Le hook de commit lit les blobs ajoutés ou modifiés dans l’index, même après un ajout forcé. Le hook de message contrôle le texte du commit. Le hook de publication examine les métadonnées, les changements de chaque commit à envoyer et les annotations de tags. Il utilise comme limites les anciens objets fournis par Git pour la destination. Une nouvelle référence sans autre limite dans la même opération implique la lecture de tout son historique local. Les références internes d’outils et de sauvegarde sont exclues de la publication.

La politique associe chaque document fictif et ressource binaire approuvés à son chemin exact et à son SHA-256. Pour renouveler une ressource, revoir ses octets et sa provenance, mettre à jour l’empreinte, puis réinstaller les hooks. Les exceptions de détection textuelle portent sur une empreinte de valeur dans un chemin exact. Les sorties des tests et les preuves de qualification restent locales. Les tests non binaires restent contrôlés comme les sources.

Le contrôle exige une adresse Git protégée ou fictive. Il masque les valeurs détectées dans ses diagnostics. Une revue humaine reste nécessaire pour les informations en texte libre, les adresses postales, les noms et les données encodées. Les hooks locaux peuvent être désactivés par l’opérateur. Les réglages de protection GitHub restent complémentaires.
