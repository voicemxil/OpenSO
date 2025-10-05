## Build

1. Navigate to the `FSO.MacOS` folder.
2. Run the following command in Terminal in that folder to build a release publish:

```bash
dotnet publish -c Release --self-contained true /p:PublishSingleFile=true
```

This will generate a .app bundle in: bin/Release/net9.0/osx-arm64/publish folder.
You can then copy the app to your Applications folder.

## Launch 3D

To open the app in 3D mode, run the open command with `--args -3d`:

E.g when the application is placed in the Applications folder, you can run:

```bash
open /Applications/FreeSO.app --args -3d
```
