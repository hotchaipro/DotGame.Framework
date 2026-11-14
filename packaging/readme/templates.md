## Getting started

```sh
dotnet new install DotGame.Templates
dotnet new dotgame -n MyGame
dotnet run --project MyGame/MyGame.Desktop
```

A window opens with a ball bouncing around it; click to move it. The game's code is a shared project in
`MyGame/MyGame` and its content in `MyGame/Content`, shared by a project for each platform: `MyGame.Desktop` (Windows, macOS and Linux),
`MyGame.WebGL` (the web), `MyGame.Android` and `MyGame.iOS`. The desktop needs only the .NET 11 SDK:
the content tools come with the packages. The web needs the `wasm-tools` workload, Android the
`android` workload, and iOS the `ios` workload and a Mac with Xcode.

`--applicationId com.yourcompany.yourgame` sets the Android application ID and iOS bundle ID
(`com.example.` and the name in lower case otherwise).
