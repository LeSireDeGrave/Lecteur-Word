# Versions et licences — audit local

Vérification officielle au 20 novembre 2026 : [.NET 10 LTS, runtime 10.0.15](https://dotnet.microsoft.com/en-us/platform/support/policy), [SDK 10.0.404](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json), [Open XML SDK 3.5.1](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1), [Avalonia 12.1.6](https://www.nuget.org/packages/Avalonia). Versions épinglées et lockfiles NuGet conservés. Les restaurations n'ont émis aucune alerte NuGet Audit ; cela ne constitue pas un audit de vulnérabilités complet.

Le graphe exact (versions, composants consommateurs, expressions SPDX et hashes NuGet) est généré par `python3 tools/license-inventory.py` dans `.local/qualification/licenses.json` : **29 versions de paquets NuGet**, y compris les actifs optionnels d'autres plateformes. Ceux-ci ne sont pas tous distribués dans le RID Linux.

| Dépendance principale | Version | Licence du paquet |
|---|---|---|
| DocumentFormat.OpenXml / Framework | 3.5.1 | MIT |
| System.IO.Packaging | 10.0.15 | MIT |
| Avalonia, Desktop, Fluent, Inter et backends | 12.1.6 | MIT pour les wrappers ; notices spécifiques des actifs embarqués |
| Avalonia.BuildServices | 11.3.2 | MIT, outil de build |
| SkiaSharp et actifs natifs correspondants | 3.119.4 | MIT pour le wrapper ; notices Skia et composants natifs à préserver |
| HarfBuzzSharp et actifs natifs | 8.3.1.3 | MIT, avec notices natives |
| MicroCom.Runtime | 0.11.6 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| ANGLE Windows optionnel | 2.1.27548.20260419 | Fichier de licences fourni, inventorié en LicenseRef ; hors runtime Linux |
| Microsoft.NETCore.App.Runtime.linux-x64 | 10.0.15 | MIT + THIRD-PARTY-NOTICES.TXT du runtime |

Skia 3.119.4 est la version de la ligne ABI utilisée par Avalonia 12.1.6. La remplacer silencieusement par la dernière version majeure de Skia serait un autre travail de compatibilité ; le lockfile évite aussi d'utiliser les paquets Skia personnalisés d'ODG présents dans le cache global.

La police de composition **DejaVu Serif 2.37** est intégrée au module Flow, copiée sans modification depuis le paquet système. SHA-256 `a52c2a69dfc93d0b511b98c1019a57eb3d6a62d328bdf1387f00851b02e3a315`. Sa notice exacte embarquée dans la table de noms est conservée dans `src/DocxFlow/Fonts/LICENSE-DejaVu.txt`, copiée au paquet et aux exports PDF ; le PDF l'inclut aussi dans ses métadonnées. SPDX : `Bitstream-Vera AND LicenseRef-DejaVu-Public-Domain`. Voir la [licence amont](https://dejavu-fonts.github.io/License.html). Le rendu ne substitue plus une version différente de police système. Inter, utilisé pour l'interface via le paquet Avalonia, requiert aussi les notices de police OFL-1.1.

Les bibliothèques système Linux restent nécessaires même avec .NET autonome. La sortie `ldd` du paquet est consignée localement : glibc (LGPL-2.1-or-later), libgcc/libstdc++ (GPL avec GCC-exception), fontconfig (MIT), FreeType (FTL OR GPL-2.0-only), Expat (MIT), zlib (Zlib), bzip2 (bzip2-1.0.6), libpng (libpng-2.0) et Brotli (MIT), notamment. ICU/OpenSSL et les bibliothèques X11/Wayland dépendent du système et du backend réellement chargé. La fermeture exhaustive des notices natives par distribution et la recette de ces backends restent à finaliser avant distribution publique ; une expression MIT NuGet ne remplace pas les licences de code tiers embarqué.

## Outils de qualification, absents du runtime

- Python / bibliothèques standard : PSF-2.0 ; validateur JSON Schema de la distribution, MIT.
- Rust `serde_json` et son graphe local de recette : MIT OR Apache-2.0 ; `memchr` MIT OR Unlicense. Versions exactes dans `tests/contract-rust/Cargo.lock`. Aucun crate OpusNova n'est lié.
- EPUBCheck 5.3.0 (règles EPUB 3.3), BSD-3-Clause avec notices de ses dépendances Java ; archive officielle vérifiée SHA-256 `6c07e68584b2e2ce2f89fe06e1246dfead3eb36b46b340e7d93524f29dcff6c5`. Le contrôle supplémentaire 5.4.0 a également réussi, mais la qualification retenue cible explicitement 3.3.
- Schémas officiels OASIS ODF 1.3 : outils de validation seulement, notices amont conservées dans les fichiers téléchargés.
- `strace`, GNU time, Poppler, MuPDF et LibreOffice installés : outils QA, jamais appelés par le lecteur ou ses exporteurs. Pas d'installation ou de modification de leur dépôt/profil utilisateur courant.

## Nouveau code et OpusNova

**Licence du nouveau code à confirmer par le propriétaire avant publication.** L'audit ne choisit pas une licence à sa place. Les notices tierces et droits existants sont préservés. OpusNova reste GPL-3.0-only ; aucun de ses fichiers ou historiques n'est copié dans ce produit et son export DOCX reste intact.

L'interopérabilité prévue est hors processus via un format de données documenté ; elle ne constitue pas une décision juridique finale de redistribution ou de packaging. La licence propre au nouveau code, les notices natives/Inter et le paquetage du helper devront être revus avant un GO de publication. Aucun conflit de licence démontré n'a été contourné, et aucun commit/push ou publication n'est autorisé ici.
