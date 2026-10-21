# Third-party notices

JP-Vision application code and the developer signing key belong to this project owner. This notice does not assign a public source-code license to JP-Vision itself.

| Component | Version / source | License / use |
|---|---|---|
| OpenCV | 5.0.0, existing user distribution | Apache-2.0; dynamically linked Runtime; `OpenCV-LICENSE.txt` |
| Qt (legacy Studio only) | 6.12.0 MinGW installed kit | Historical 0.6.x Qt client; not included in the 0.7.0 WPF runtime |
| QtNodes | tag 3.0.16, commit 7c6341a66a8e46b8988140b9e60d892b6a3560b3 | BSD-3-Clause; unmodified dependency, linked statically into legacy Qt Studio |
| nlohmann/json | 3.12.0 | MIT; vendored single header and license |
| MinGW / GCC runtime | installed mingw1310_64 toolchain | Legacy runtime DLLs deployed with Qt; GCC Runtime Library Exception and applicable MinGW licenses |
| .NET / Microsoft VC++ | .NET 10 self-contained Studio; app-local VC145 release CRT | Microsoft respective distribution terms; WPF sample is framework-dependent |
| xUnit, Test SDK | 2.9.3 / runner 3.1.4 / SDK 17.14.1 | Development tests only; not client components |

The 0.7.0 WPF Studio does not deploy Qt, QtNodes or MinGW DLLs. It bundles .NET through dotnet publish and the matching release VC++ CRT from the installed Visual Studio redistributable directory. See [Microsoft redistributable file guidance](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files). The paragraphs about Qt below apply to historical Qt distributions and retained source.

Qt was deployed through windeployqt with DLLs and plugins kept as separate files. A customer can replace the Qt libraries with compatible versions; no integrity lock is placed on Qt DLLs. Keep the Qt source and license information available when redistributing the application and comply with the applicable license or use an appropriate commercial Qt license. [Qt's official LGPL obligations](https://www.qt.io/development/open-source-lgpl-obligations).

Matching Qt source: the Qt 6.12.0 source distribution / v6.12.0 tags in the official [qtbase](https://code.qt.io/cgit/qt/qtbase.git/tag/?h=v6.12.0) and [qtsvg](https://code.qt.io/cgit/qt/qtsvg.git/tag/?h=v6.12.0) repositories. JP-Vision does not modify Qt or QtNodes source. Installed-kit binary build provenance beyond the supplied version has not been independently reconstructed.

The existing OpenCV distribution was provided by the owner and reused; its source build options are not independently recreated in this delivery. If replacing or publicly redistributing that binary, retain its component notices and verify optional bundled third-party modules with its original provider. The core OpenCV license text is copied from the official 5.x source repository.

nlohmann/json header SHA-256: aaf127c04cb31c406e5b04a63f1ae89369fccde6d8fa7cdda1ed4f32dfc5de63.

Runtime and WPF client packages do not contain QtNodes/Qt, a license issuer, signing private keys, or machine license files. These notices and full copied license texts accompany the Studio distribution.
