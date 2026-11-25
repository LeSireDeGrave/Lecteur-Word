%global debug_package %{nil}
%global __os_install_post %{nil}
Name:           lecteur-word
Version:        %{lw_version}
Release:        %{lw_release}
Summary:        Read and export DOCX documents
License:        MIT AND OFL-1.1 AND Bitstream-Vera AND BSD-3-Clause AND Apache-2.0 AND FTL AND IJG AND Zlib AND libpng-2.0 AND (GPL-3.0-or-later WITH GCC-exception-3.1) AND LicenseRef-Adobe-DNG-SDK AND LicenseRef-LecteurWord-Branding
AutoReqProv:    no
Requires:       glibc, libgcc, libstdc++, ca-certificates, libbrotli, krb5-libs, libicu, openssl-libs, tzdata, zlib-ng-compat, fontconfig
Requires:       libX11, libICE, libSM, libXcursor, libXi, libXrandr, libXinerama, libXext, mesa-libGL, mesa-libEGL
Requires:       desktop-file-utils, shared-mime-info

%description
Read and inspect DOCX documents. Export to PDF, ODT, EPUB 3 and NOVA.
The application ships with its .NET runtime.

%install
mkdir -p "%{buildroot}"
cp -a "%{_sourcedir}/package/." "%{buildroot}/"

%files
/usr/bin/lecteur-word
/usr/lib/lecteur-word
/usr/share/applications/lecteur-word.desktop
/usr/share/icons/hicolor/256x256/apps/lecteur-word.png
%license /usr/share/doc/lecteur-word/copyright
%license /usr/share/doc/lecteur-word/LICENSE
%license /usr/share/doc/lecteur-word/BRANDING.md
%doc /usr/share/doc/lecteur-word/README.fr.md
%doc /usr/share/doc/lecteur-word/README.en.md
%doc /usr/share/doc/lecteur-word/CHANGELOG.md
%doc /usr/share/doc/lecteur-word/BUILD.json
%doc /usr/share/doc/lecteur-word/DEPENDENCIES.json
%license /usr/share/doc/lecteur-word/notices
