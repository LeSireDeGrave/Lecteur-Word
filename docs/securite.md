# Sécurité et budgets du prototype

Le DOCX est une entrée non fiable. Les quotas suivants sont configurables et figurent dans chaque manifeste. Les tests de frontière du lot ont prouvé les refus indiqués ; les valeurs par défaut ont permis les trois documents synthétiques mesurés. Elles ne sont pas un dimensionnement définitif pour tous les manuscrits Word.

| Budget par inspection | Valeur par défaut |
|---|---:|
| ZIP source | 32 MiB |
| Entrées | 2 048 |
| Total décompressé | 128 MiB |
| Partie | 16 MiB |
| Rapport d'expansion par entrée | 200 |
| Médias | 256 × 8 MiB maximum, sous le total décompressé |
| Pixels d'image déclarés | 40 millions |
| Profondeur XML | 64 |
| Nœuds | 150 000 |
| Attributs / nœud | 64 |
| Taille d'une valeur | 256 Ki caractères |
| Allocations cumulées | 512 MiB |
| RSS du processus | 768 MiB |
| Durée coopérative | 30 s |
| Sortie totale | 128 MiB |

Les budgets d'allocations/RSS sont coopératifs, vérifiés pendant les parcours et par bloc de lecture. Ils utilisent des compteurs du processus, donc du travail concurrent peut provoquer un refus conservateur. Ils ne sont pas une isolation matérielle ni une borne instantanée sur une allocation native. Le contrôle dur du helper est distinct : `tools/run-isolated.py` impose CPU 31/32 s, taille de fichier 128 MiB, heap GC 384 MiB, surveille RSS 768 MiB et tue le groupe après 45 s. Les refus wall/RSS du superviseur ont été testés sur des enfants synthétiques. Ce lanceur vise le helper court, pas une session GUI persistante.

La composition a aussi un profil borné : 50 000 nœuds d'inspection, 2 millions de caractères et 500 pages. Une structure ou un budget dépassé produit un diagnostic et aucun faux rendu partiel disponible. Le viewer ne garde qu'une page principale et les miniatures voisines ; leur bitmap précédent est libéré. Ces limites ne constituent pas encore une isolation dure du processus GUI.

## Contrôles réalisés

- Répertoire central ZIP compté et borné avant toute allocation des entrées `ZipArchive`, comparaison des en-têtes locaux/centraux, comptes et modes de compression. Les extensions ZIP64 reconnues ne sont pas qualifiées et sont refusées.
- Signature ZIP/CFB ; lecture CFB limitée aux tables d'allocation/répertoires pour différencier ancien Word, chiffrement Office et conteneur inconnu. Aucune ouverture de flux actif ni déchiffrement.
- Chemins d'entrées bornés ; refus `..`, absolus, antislashs, encodages ambigus, doublons (y compris casse) et symlinks ZIP. Les `../` légitimes des **relations** sont normalisés sous la racine OPC ; une sortie de racine est refusée.
- Tailles déclarées et effectivement lues, rapport d'expansion, CRC-32 des parties et SHA-256 des ressources. Les archives répertoire explicites et certaines URI OPC encodées pourtant légitimes sont refusées conservativement dans ce prototype.
- `[Content_Types].xml`, relations racine et par partie, types des dépendances principales, cibles internes, ID de relations dupliqués et parties manquantes.
- XML : `DtdProcessing.Prohibit`, `XmlResolver=null`, quotas de caractères/profondeur, XML malformé refusé. Les parties XML non référencées sont aussi parcourues. Une partie binaire inconnue est hashée et diagnostiquée, jamais interprétée/exécutée.
- Aucune ressource externe, `altChunk`, modèle distant, URL, OLE ou macro n'est exécutée/chargée. La trace système sur fixture d'image et lien externes ne montre aucun socket IPv4/IPv6 ni `connect`. Le runtime .NET peut ouvrir son socket de diagnostic **AF_UNIX** ; ce n'est pas un serveur documentaire ou réseau.
- L'ouverture source Linux utilise `O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC`, puis exige un flux seekable. La fixture FIFO ne bloque pas l'ouverture ; les symlinks sources sont refusés. Le snapshot est ensuite indépendant des lectures SDK.
- Aucune écriture à l'input. Assertions SHA-256 avant/après succès, refus, annulation et GUI sur les fixtures. Les écritures concernent exclusivement les sorties neuves sélectionnées ou le staging privé de cette invocation.
- Refus des destinations existantes et ancêtres de sortie symboliques. Publication du répertoire par renommage, sans écrasement. Quotas appliqués pendant l'écriture JSON, archives et PDF ; les exceptions des callbacks PDF natifs sont reportées une fois revenu dans le code managé, avant publication.
- Annulation de lecture, composition et export ; rollback du staging en cas d'erreur coopérative. SIGKILL/panne peut laisser un staging privé, sans répertoire final annoncé.

## Isolation Linux : preuve et limites

Le bac à sable courant refuse `ptrace` et les espaces de noms utilisateur/réseau. Les essais explicites avec droits adaptés ont permis `strace` et `unshare --user --map-root-user --net`, y compris l'inspection de la fixture à relation externe dans un namespace sans réseau. Cela qualifie ce scénario de recette, **pas** une intégration automatique de sandbox dans l'application.

Le superviseur de ressources n'isole pas le système de fichiers ou le réseau. Seccomp, Landlock, profil AppArmor, cgroup dédié, intégration Wayland/portails et déploiement multi-distribution restent non testés. Le dossier de sortie doit appartenir au lanceur ; la défense contre une course d'un autre processus de même UID modifiant les ancêtres n'est pas qualifiée. Aucun de ces états n'est présenté comme une sécurité de production complète.

Les médias PNG exposés restent des blobs bornés à signature/dimensions vérifiées, sans validation complète de décodage. Ils portent un hash et une extension `.bin`. Le prototype de rendu/export refuse les images DOCX : il ne les passe donc pas à un décodeur d'images hostile. JPEG, SVG, équations, OLE et autres médias restent inventoriés/quarantainés. L'extension future de ce périmètre impose une qualification dédiée.
# Lecture simplifiée et images

La projection continue exige une inspection acceptée et complète ; elle ne contourne aucun refus ZIP/OPC/XML. Elle garde des limites propres (30 000 blocs, 2 millions de caractères comptabilisés, profondeur 64, 10 secondes, 512 expansions de notes) et l’annulation. Un dépassement maintient un refus motivé, avec inventaire disponible.

Seuls des PNG 8 bits non entrelacés gris/RGB/alpha passent le profil conservateur `SafePng` : CRC et ordre des chunks, zlib et lignes de pixels bornés, 8 MiB compressés, 8 Mpx/image, dimensions 8192, 32 MiB décompressés et limite temporelle. Les métadonnées non nécessaires sont omises d’une copie de rendu et cette omission est diagnostiquée. Les originaux/hashes restent conservés ; JPEG, palettes, SVG et autres formats non qualifiés restent des repères. Ce contrôle réduit la surface du décodeur natif, sans constituer une sandbox.

Le modèle borne les ressources à 64 images/24 Mpx ; la GUI borne son cache à 32 bitmaps/16 Mpx, virtualise le flux et les lignes des tableaux, et désalloue les images au changement de document. Une image hors budget conserve un repère explicite. Le modèle simplifié n’élargit aucun précontrôle d’export et n’active aucune ressource externe.

## Fontes incorporées

FONTS-001 garde les parties de fontes en quarantaine mémoire bornée, hors SafeMedia et hors ressources du CLI. Le chargement mémoire par Skia est précédé des contrôles de relation interne, SHA-256, désobfuscation, répertoire SFNT, tables requises et droits techniques fsType. Formats spécialisés et droits non qualifiés produisent un refus et un repli tracé. Aucun téléchargement, extraction, installation ou export des fontes. Le [périmètre et ses limites](polices-et-typographie.md) distinguent ce précontrôle d’un validateur complet ou d’une sandbox native.
