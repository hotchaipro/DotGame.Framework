## Getting started

```sh
dotnet new install DotGame.Templates
dotnet new dotgame -n MyGame
dotnet run --project MyGame/MyGame.Desktop
```

A window opens with a ball bouncing around it; click to move it. The game's code is a shared project in
`MyGame/MyGame` and its content in `MyGame/Content`, shared by a project for each platform: `MyGame.Desktop` (Windows, macOS and Linux),
`MyGame.WebGL` (the web), `MyGame.Android` and `MyGame.iOS`. The desktop needs only the .NET 11 SDK:
the content tools come with the packages. The web needs the `wasm-tools` workload, and on Linux
Python 3 too (most distributions have it, but container images often don't). Android needs the
`android` workload, the Android SDK and a JDK, and iOS the `ios` workload and a Mac with Xcode. Visual
Studio on Windows installs Android's three with its .NET Multi-platform App UI development workload.
Otherwise this installs the SDK and a JDK (or adds what's missing to Android Studio's SDK folder); then
set `ANDROID_HOME` and `JAVA_HOME` to the two folders:

```sh
dotnet build MyGame/MyGame.Android -t:InstallAndroidDependencies -p:AndroidSdkDirectory=<sdk folder> -p:JavaSdkDirectory=<jdk folder> -p:AcceptAndroidSDKLicenses=True -p:AndroidManifestType=GoogleV2
```

`--applicationId com.yourcompany.yourgame` sets the Android application ID and iOS bundle ID
(`com.example.` and the name in lower case otherwise).
