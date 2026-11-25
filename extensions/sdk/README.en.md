# Lecteur Word community kit · API 1

Run `LecteurWord --extension-tool-v1` in a terminal. It uses the extension manager’s validator and runs without opening a window. Replace `LecteurWord` with the executable path. An AppImage can forward the same arguments to its launcher.

| Command | Result |
| --- | --- |
| `init <category> <id> <new-directory>` | Creates an editable manifest and contribution |
| `pack <manifest.json> <payload.json> <output.lwext>` | Computes the digest, validates the contribution and writes the package |
| `validate <package.lwext>` | Validates a package and reports compatibility with this reader |
| `catalog <packages-HTTPS-base-url/> <package-directory> <index.json>` | Builds a local index from the packages and their SHA-256 hashes |
| `validate-catalog <index.json>` | Validates the local index without network access |

The JSON response contains `protocol: "lecteur-word-extension-tool"`, `apiVersion: 1` and `result`. Exit code 0 means success, 64 incorrect usage, 65 invalid data and 74 an access or write error. Commands preserve an existing destination. The DOCX converter has a separate contract.

## License and contributions

The SDK, its schemas, templates, and examples are licensed under [MIT](../LICENSE). Give the SPDX identifier and provenance of the files you distribute. When contributing, identify reused material and confirm that you can submit your work under MIT. Built-in translations use the project license.

## Create and validate

```sh
LecteurWord --extension-tool-v1 init navigation org.author.navigation my-extension
# Fill in the name, description, author, publisher and license.
mkdir packages
LecteurWord --extension-tool-v1 pack my-extension/manifest.json my-extension/payload.json packages/navigation-1.0.0.lwext
LecteurWord --extension-tool-v1 validate packages/navigation-1.0.0.lwext
```

An `.lwext` is a UTF-8 ZIP containing exactly `manifest.json` and `payload.json` at the root. Limits are 128 KiB for the archive, 32 KiB for the manifest and most contributions, and 256 KiB for a decompressed translation. The schema covers shape. The reader also validates digests, ranges, contrast, expressions and permitted actions. [Manifest schema](schemas/manifest-v1.schema.json) · [Catalog schema](schemas/catalog-v1.schema.json)

Package versions and compatibility bounds use `major.minor.patch`. The minimum is inclusive and the maximum is exclusive. An application prerelease precedes its corresponding final release. Optional dependencies name an installed and enabled extension and a supported version range. Updates must increase the package version. Review compatibility when permissions or dependencies change.

## Contributions and permissions

| `init` category | Feature | Permission declaration | Example |
| --- | --- | --- | --- |
| `theme` | `appearance.theme` | `[]` | [Forest](../demo-theme/) |
| `language` | `appearance.translation` | `[]` | [Canadian French](../demo-language/) |
| `statistics` | `reading.metrics` | `["document.metrics.read"]` | [Reading metrics](../demo-reading/) |
| `navigation` | `reading.navigation` | `["document.navigation"]` | [Reading navigation](../demo-navigation/) |
| `accessibility` | `reading.accessibility` | `[]` | [Magnified reading](../demo-accessibility/) |

All five payloads are declarative. The reader applies themes, translations and display preferences. The packaged kit includes the bilingual source strings in `messages-v1.json`. A bounded worker receives counters for statistics expressions. On Linux x86-64, seccomp blocks file and socket opens before the worker reads the request. Navigation actions use the reader’s built-in commands and index. Packages load no native code or scripts. Contributions do not receive DOCX paths or text. A failed version can be quarantined and explicitly restored in the manager. [Platform and contribution reference](../../docs/plateforme.md)

## Review a built-in catalogue

Find the French or English source message in `messages-v1.json`. Run `python3 tools/export-ui-messages.py` from the source tree to regenerate that index. Edit the value for the same key in `src/LecteurWord/Languages/<locale>.json`, preserving placeholders such as `{0:N0}`. Run `python3 tools/export-ui-messages.py --check-built-in` before submitting the change.

To try the change in an installed reader, run `--extension-tool-v1 init language` and copy the revised catalogue to the new project’s `payload.json`. Give the extension a distinct name and ID, then run `pack` and `validate`. Install and enable it, then choose its name from the globe menu. Verify the labels in context at a compact window size and include the locale and edited keys with the contribution. [Catalogues awaiting native review](../../docs/plateforme.md#langues)

## Prepare a local release

1. Test the package through the puzzle button to the right of the globe. Confirm installation and enable the package from its details.
2. Put it in a directory served by your own HTTPS test server. Pass the package directory’s HTTPS base URL to `catalog`, then serve the generated index. Downloaded bytes must match both the full indexed manifest and the SHA-256 hash.
3. Open **Catalog…**, enter the trusted index URL and choose **Refresh catalog**. Verify the entry in **Discover**, then download or install it.
4. Build a higher version with a new filename. Generate a new index file, validate it and replace the served index. Check **Updates**. An updated package starts disabled and requires explicit activation.
5. Check **Offline** after downloading. The verified index and cached archives remain usable. An uncached package triggers no network request in offline mode.

Catalog requests use HTTPS with standard certificate validation and redirects disabled. Integrity checks cover the hashes, manifest and payload, relative to the trusted index. The cache keeps up to 32 recently downloaded archives per index address. The reader has no default public catalog. Preserve licenses and provenance, include both JSON files, provide French and English descriptions when possible, and document reproducible checks. Use the contribution channel identified by the maintainer when it is available.
