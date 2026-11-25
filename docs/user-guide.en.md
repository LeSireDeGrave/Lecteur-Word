# Lecteur Word on Linux

## Install, update and uninstall

The V1 preparation builds target x86-64 Linux with glibc. The .NET runtime is included. The system supplies base libraries, ICU, OpenSSL, Fontconfig and the X11 and OpenGL/EGL libraries for the graphical reader. This edition uses XWayland in a Wayland session. See the delivery report for the environments that were tested.

Run `sha256sum -c SHA256SUMS` in the delivery directory. The checksums verify integrity against the supplied list.

| Format | Install or run | Uninstall |
| --- | --- | --- |
| DEB | `sudo apt install ./lecteur-word-….deb` | `sudo apt remove lecteur-word` |
| RPM | `sudo dnf install ./lecteur-word-….rpm` | `sudo dnf remove lecteur-word` |
| AppImage | `chmod u+x ./LecteurWord-….AppImage`, then run it | Close the reader and remove the file |
| TAR.GZ | Extract and run `./app/LecteurWord` from the extracted directory | Close the reader and remove the extracted directory |

Replace abbreviated names with the actual filenames. Update a DEB or RPM by running the installation command with the newer package. To update an AppImage, close the reader and replace the file. On systems without usable FUSE, pass `--appimage-extract-and-run` to the AppImage.

For an application-menu entry without administrator access, run `python3 install-linux.py --from ./app --install --no-desktop-shortcut` from the extracted archive. Omit the last option to add an XDG desktop shortcut as well. Repeat with the new archive to update. Close the reader and run `python3 install-linux.py --uninstall` to remove the managed installation. To register an AppImage without a separate integration tool, extract it with `--appimage-extract`, then run `python3 squashfs-root/usr/share/lecteur-word/install-linux.py --from squashfs-root/usr/lib/lecteur-word --install --no-desktop-shortcut`.

The DEB, RPM and registered TAR.GZ installations offer **Open With Lecteur Word** for DOCX files. Your desktop keeps its chosen default application. Updates and uninstallation preserve preferences, extensions and session data in the `lecteur-word` XDG directories. To reset them, close the application and remove the relevant user data manually.

## Open and browse a document

Open a DOCX from the file manager’s Open With menu, by dropping it onto the reader, with the folder button, or with **Ctrl+O**. Drag and drop accepts one local DOCX at a time. The reader opens the original read-only. Paths may contain spaces and accented characters.

The toolbar offers continuous reading and, when the document can be composed, single-page, facing-page and fit-to-width views. The navigation panel provides thumbnails, headings, bookmarks, notes, headers and footers, and search (**Ctrl+F**). Verified note references open the corresponding note. The **Document elements** panel locates elements and shows the retained details.

| Shortcut | Action |
| --- | --- |
| Tab / Shift+Tab | Move through controls |
| F6 / Shift+F6 | Move to the next / previous region |
| F7 / F8 / F9 | Ruler / document elements / navigation |
| Alt+Left / Alt+Right | Previous / next page in a paginated view |
| Ctrl+plus / Ctrl+minus | Adjust document zoom |
| Ctrl+0 / Ctrl+9 | Reset zoom / fit to width |

## Display, accessibility and extensions

Choose System, Light, Chocolate & Cream, Microsoft Blue or Dark from the toolbar. The new theme applies immediately to internal windows and their menus. Theme extensions use the same resources. Native file pickers follow the desktop theme.

Open the language list from the globe. With the keyboard, press **Tab** to reach the globe, then press **Enter**. After typing a search, press **Down Arrow** to reach the first match, **Tab** to move through the choices, and **Enter** to confirm. Each language appears under its own name, with a check mark for the current choice. **Install more languages** opens the Languages category in the extension manager. Changes take effect immediately. The accessibility preferences in the header control interface magnification and reading colors. Document zoom and export colors have their own settings.

The puzzle button opens the extension manager. Enter the path to a `.lwext` file, drop it on the window or use **Browse**. Review its details, install it and enable it. Updates are installed disabled. The manager shows permissions, dependencies and available settings. A quarantined extension can be re-enabled after confirmation. A remote catalog is available when an approved source is configured.

Continuous reading exposes controls and text through the Linux AT-SPI bridge when enabled in the session. The paginated view exposes page names and controls alongside raster page rendering.

Use the **underlined A** next to the theme controls to set the **Reading text color**. Automatic mode adjusts contrast against the active background. Original colors, white, black, cream, and a custom color are available. The reader remembers the choice and applies it in continuous, single-page, and facing-page views. Illustrations and exports retain their own colors.

## Export

**DOCX → PDF / ODT / EPUB 3 / NOVA**

Open **Export**, choose a format and a destination, then review the preflight. Reported limitations describe conversions, presentation changes and preservation limits. Replacing an existing file requires confirmation.

Compatible conversion is selected each time the export window opens. **Advanced options** contains **Require full preservation** and the preservation details, which you can copy on request.

| Format | Use |
| --- | --- |
| PDF | Pages composed by the reader, with a page range and image-quality settings |
| ODT | Edit freely in an OpenDocument-compatible word processor. |
| EPUB 3 | Reflowable edition for an e-reader, with an optional cover |
| NOVA | Editable OpusNova project in native format 27 |

Microsoft Word layout depends on the fonts and features used by each document. The reader offers views and exports within its supported profile and reports adaptations or refusals. **Document properties** lists requested, selected, missing and substituted fonts. The substitution tool helps inspect certain historical glyphs. Different fonts or font versions can affect line and page breaks.

## Session recovery and issue reporting

After an interruption, the reader may offer to recover the last reading position. Use **Resume last reading** in Help to return to a saved session. An incident report can be viewed, copied or saved locally. Review the draft before sending an issue and choose the information you want to share. **About** shows the application version, build commit, engine and supported contracts.

## Licenses

Original Lecteur Word work is licensed under MIT. Distributions include the `LICENSE` text and the notices for fonts, dependencies, and launcher components. Open **About** to read the **Full MIT license text** and **Third-party components and legal notices**.

This product includes DNG technology under license by Adobe Systems Incorporated.

## Standalone converter

Extract the separate CLI archive. Keep the executable and its libraries together. The CLI runs without a display server.

```sh
./lecteur-word-cli capabilities
./lecteur-word-cli convert --contract-version 1 --format nova --nova-version 27 \
  --source 'Revised chapter.docx' --output 'Revised chapter.nova'
```

CLI formats are `pdf`, `odt`, `epub3` and `nova`. JSON events report progress, preflight and the final result. Exit status 0 means the output was published. Statuses 64, 65, 70, 74 and 130 mean invalid usage, rejected input or profile, internal error, I/O error and cancellation. Pass `--overwrite` to allow replacement and press **Ctrl+C** to cancel. Third-party applications can configure an absolute executable path and query `capabilities` before calling the converter.
