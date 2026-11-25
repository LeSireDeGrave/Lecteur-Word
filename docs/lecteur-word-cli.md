# Lecteur Word CLI

`lecteur-word-cli` publie un DOCX en PDF, ODT, EPUB 3 ou NOVA. Il réutilise le modèle inspecté, la composition et les exporteurs de Lecteur Word. Il fonctionne en terminal ou comme sous-processus, sans serveur graphique ni suite bureautique.

## Construire et lancer

```sh
dotnet build src/LecteurWord.Cli/LecteurWord.Cli.csproj -c Release -m:1
dotnet publish src/LecteurWord.Cli/LecteurWord.Cli.csproj -c Release -m:1 \
  -r linux-x64 --self-contained -o ./sortie-cli
./sortie-cli/lecteur-word-cli capabilities
./sortie-cli/lecteur-word-cli convert --contract-version 1 \
  --source 'Chapitre révisé.docx' --format nova --output 'Chapitre révisé.nova'
```

Pour une distribution avec le runtime .NET 10 installé, utiliser l’exécutable du build ou lancer `dotnet lecteur-word-cli.dll`. Déployer avec lui les bibliothèques natives de composition et la licence de police issues du dossier de publication.

| Option | Effet |
|---|---|
| `--source` / `--output` | Chemins de lecture et d’écriture. Le contenu DOCX est reconnu par inspection, y compris si son nom est sans extension. Le dossier de destination doit exister. |
| `--format` | `pdf`, `odt`, `epub3` ou `nova`. Extensions de sortie respectives `.pdf`, `.odt`, `.epub` et `.nova`. |
| `--nova-version 27` | Exige la version du paquet NOVA attendue. Cette option s’applique au format `nova`. |
| `--policy compatible` | Politique par défaut. Les adaptations autorisées et les réserves apparaissent dans le résultat. |
| `--policy complete` | Exige le profil de conservation intégrale du moteur. Une réserve bloquante empêche l’écriture. Pour le PDF, les glyphes de remplacement, l’historique éditorial absent ou les cibles de liens indisponibles empêchent aussi l’écriture. |
| `--overwrite` | Autorise le remplacement de la destination. Par défaut, un fichier existant est protégé. |
| `--from-page n` / `--to-page n` | Bornes PDF inclusives, numérotées à partir de 1. Une borne absente conserve l’extrémité correspondante. |
| `--pdf-image-quality n` | JPEG de qualité 1 à 100, ou 101 pour conserver le profil sans perte par défaut. |
| `--pdf-provenance` | Joint au PDF la provenance des glyphes remplacés. |
| `--cover <file>` | Sélectionne un PNG ou un JPEG compatible pour la couverture EPUB. |
| `--cover-first-image` | Utilise la première illustration compatible comme couverture EPUB. |

## Contrat de sous-processus v1

Interroger `capabilities` avant d’épingler la version d’intégration. L’appel de conversion accepte `--contract-version 1` et refuse une version inconnue. Transmettre chaque argument séparément au processus et lire la sortie standard au fil de l’eau. Les événements sont des objets JSON UTF-8, à raison d’un objet par ligne. L’enveloppe contient toujours `protocol: "lecteur-word-convert"`, `contractVersion: 1`, `eventType` et `payload`.

- `capabilities` fournit `applicationVersion`, `engineVersion`, les formats, les politiques, la version NOVA par défaut `novaVersion` et les versions prises en charge `novaVersions`. L’objet `build` reprend l’identité affichée dans À propos, dont le commit de compilation, la présence de changements locaux et la version de l’API d’extensions. Les valeurs indisponibles sont indiquées par `null`.
- `progress` indique `phase`, `completed` et `total`. Les compteurs sont propres à la phase. Le nombre de phases et d’événements peut évoluer.
- `preflight` transmet `allowed`, `status`, `profile`, `issues` et `reservations` après analyse et avant écriture. Une conversion en politique intégrale s’arrête si la conservation exige de refuser une réserve.
- `result` termine une conversion. Il contient `exitCode`, `code` et `status`. Après réussite, il contient aussi `output`, le profil `profile`, `sourceSha256`, le nombre de feuilles du modèle source `pages`, les `issues`, les `reservations` et les `inspectionDiagnostics`. Une issue porte un code, un message, un indicateur `blocksExport` et, si disponible, une provenance `source.partUri` et `source.path`. Les échecs peuvent fournir `option` ou `details`, ainsi que les diagnostics calculés avant l’arrêt.

Une conversion est réussie après un événement `result` dont `exitCode` vaut 0 et la sortie normale du processus avec le même code. Les statuts de réussite sont `ready`, `converted` et `reserved`. Utiliser les codes structurés pour prendre des décisions et les messages pour l’affichage. Les champs et codes supplémentaires sont compatibles avec v1. Aucun ordre fixe n’est requis pour les diagnostics.

| Retour | Signification |
|---|---|
| 0 | Sortie vérifiée et publiée atomiquement, avec les éventuelles réserves indiquées. |
| 64 | Arguments, options ou version de contrat invalides. |
| 65 | Source incompatible, profil refusé ou données de conversion invalides. |
| 70 | Erreur interne. |
| 74 | Problème d’accès ou d’écriture, y compris destination occupée. |
| 130 | Annulation coopérative avant publication. |

Sur Linux, envoyer `SIGINT` ou `SIGTERM` pour annuler. Sur Windows, utiliser l’interruption de console. Attendre la sortie du processus et son résultat. Le moteur écrit d’abord un fichier temporaire dans le dossier de destination, relit ce fichier puis publie par renommage. Il conserve l’original DOCX et supprime le temporaire sur les échecs et annulations gérés. Une terminaison forcée par le système peut laisser un temporaire `.lecteur-export-*.tmp`. Après le renommage final, une conversion terminée reste une réussite.

## Représentations compatibles

Le moteur essaie la représentation native et éditable, puis un équivalent fonctionnel ou structuré. Pour les dessins, il réemploie d’abord les images et vecteurs vérifiés, puis le rendu de l’objet seul. Une zone de texte vérifiée dont le cadre reste hors profil peut être reprise à son emplacement sous forme de paragraphes éditables en ODT, EPUB et NOVA. La réserve conserve sa provenance et signale l’adaptation du cadre. Les appels multiples d’une même note sont des références au même corps en ODT et en EPUB. Une impossibilité de vérification, une incompatibilité technique ou le dépassement d’un budget peuvent empêcher l’écriture.

NOVA 27 conserve les signets et les liens vérifiés, les notes, les styles et la structure éditable des tableaux admis. Un sommaire Word à marques qui correspond aux chapitres du projet et dont les titres et destinations concordent devient un sommaire natif. Les folios sont recalculés dans OpusNova. Un numéro d’ordre répété devant le même numéro écrit dans le titre peut être regroupé. Les sommaires à libellés ou sélections différents gardent leurs entrées et renvois éditables quand cette représentation est vérifiable.

### Évolutions à transmettre à OpusNova

| Domaine | Développement proposé |
| --- | --- |
| Sommaire | Ajouter une sélection ordonnée de destinations par identité et des libellés éditoriaux facultatifs au sommaire natif. Conserver les réglages par niveau des marques, y compris les entrées sans folio. |
| Champs et renvois | Ajouter des références actualisables aux propriétés personnalisées et un affichage du contenu d’une plage de signet. Le mode « Titre » du renvoi natif restitue aujourd’hui le nom du signet. |
| Notes | Permettre un renvoi vers un corps de note depuis plusieurs positions. Distinguer ce renvoi de l’appel qui attribue le numéro et conserver le libellé d’appel personnalisé. |
| Sections, en-têtes et pieds | Rattacher un style de page à une plage du manuscrit. Ajouter les variantes de première page par section, leur héritage, les redémarrages de folios et un corps riche pour chaque région. Les règles globales actuelles couvrent certaines valeurs simples. |
| Tabulations | Ajouter les caractères de conduite personnalisés aux valeurs prédéfinies du modèle. Qualifier la correspondance du séparateur décimal existant avec la langue effective du passage DOCX. |
| Tableaux | Ajouter les bordures propres à chaque bord de cellule et les marges distinctes par côté. Étendre la pagination aux lignes fractionnables et aux fusions qui traversent le groupe de titres récurrents. Les titres contigus et la marge uniforme par cellule sont déjà natifs. |
| Images et dessins | Permettre les liens sur les objets, l’ancrage en ligne dans les notes et cellules, ainsi qu’une ressource vectorielle native bornée avec texte et chemins conservés. |

## Intégration dans une application tierce

Une application peut chercher `lecteur-word-cli` dans un chemin absolu configuré, dans son dossier de dépendances ou dans le `PATH` de son processus. Déployer le dossier Linux x64 complet si l’application embarque le CLI. Ne jamais sélectionner un exécutable dans le dossier du DOCX. Au premier appel, contrôler le retour de `capabilities`, l’enveloppe `lecteur-word-convert` v1, `engineVersion`, le format `nova` et la présence de la version visée dans `novaVersions`.

OpusNova peut créer un dossier de travail privé et demander un nouveau fichier NOVA dans ce dossier. Passer les chemins entiers comme arguments distincts pour conserver espaces et accents. Lire les lignes JSON pendant l’exécution pour afficher la progression et les réserves du précontrôle. Choisir `--policy complete` si l’acceptation des réserves doit précéder la production, puis relancer en `compatible` après décision de l’utilisateur. Une politique compatible peut produire le projet immédiatement après le précontrôle. Ouvrir le projet après réception d’un résultat réussi et confirmation du code de retour. Nettoyer le dossier d’intégration quand son contenu n’est plus utilisé.

Un appel Rust peut utiliser `std::process::Command` et lire les événements avec `BufRead::lines`. Sur un appel asynchrone, conserver le PID pour envoyer le signal d’annulation au processus et continuer à vider les flux jusqu’à sa sortie. Les applications peuvent ignorer les types d’événements qu’elles ne traitent pas et traduire les diagnostics à partir de leurs codes. La langue choisie dans le lecteur n’agit pas sur les événements CLI.

```rust
use std::io::{BufRead, BufReader};
use std::process::{Command, Stdio};

let mut child = Command::new(cli_path)
    .args(["convert", "--contract-version", "1", "--format", "nova", "--nova-version", "27", "--source"])
    .arg(source_docx)
    .arg("--output").arg(destination_nova)
    .stdout(Stdio::piped())
    .spawn()?;
let stdout = child.stdout.take().expect("stdout redirigée");
for event in BufReader::new(stdout).lines() {
    // Décoder l’enveloppe JSON et conserver le dernier événement result.
    traiter_evenement(&event?)?;
}
let status = child.wait()?;
```
