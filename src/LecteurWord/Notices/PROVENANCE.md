# Notices embarquées

Ces textes accompagnent les composants tiers. Le code original de Lecteur Word est couvert par [MIT](../../../LICENSE).

- `01-Packages.txt` : ID, version, auteurs, copyright et licence déclarés dans les manifestes NuGet verrouillés du viewer ; texte MIT reproduit depuis SkiaSharp.
- `02` : licence identique des wrappers SkiaSharp 3.119.4 et HarfBuzzSharp 8.3.1.3.
- `03` : notices natives SkiaSharp 3.119.4, identiques octet pour octet à celles d’HarfBuzzSharp 8.3.1.3.
- `04` : notices de System.IO.Packaging 10.0.15.
- `05` et `06` : licence et notices du runtime Microsoft.NETCore.App linux-x64 10.0.15 local. Le runtime réellement chargé est affiché séparément dans la référence du build.
- `07` : licence fournie par Avalonia.Angle.Windows.Natives 2.1.27548.20260419 ; présence possible dans une distribution portable, sans chargement sous Linux.
- `08` : copie inchangée de la licence de la police DejaVu Serif fournie par DocxFlow.
- `09` : identité/copyright/licence relevés dans les métadonnées UTF-16 des polices Inter 3.019 embarquées dans Avalonia.Fonts.Inter 12.1.6. Texte standard OFL 1.1 reproduit depuis `/usr/share/doc/fonts-manchufont/OFL.txt.gz`, après retrait du copyright propre à cette autre police.

Lors d’un changement des dépendances ou de publication pour une autre plateforme, revoir cet inventaire et les notices. Aucun corpus utilisateur ni résultat de test n’est inclus.

- `10` : Wuffs, notice Apache-2.0 du commit `e3f919ccfe3ef542cfc983a82146070258fb57f8` déclaré par le Skia distribué.
- `11` : Vulkan Memory Allocator, licence MIT du commit `c788c52156f3ef7bc7ab769cb03c110a53ac8fcb`. Le binaire Linux contient `vmaCreateAllocator`.
- `12` : GNU GPL v3 et exception GCC Runtime Library 3.1 de GCC 8.3.0, pour les fragments du runtime liés par les bibliothèques graphiques Linux.
