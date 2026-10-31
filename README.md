# Eggman's StoreZip

Makes **StoreZips**: zips with every file stored (no compression), sorted, with fixed dates and nothing else in the zip. The same ROMs always give the same StoreZip, byte for byte, on any computer.

The [TeknoParrot Collection](https://github.com/Eggmansworld/TeknoParrot) nests MAME zips inside its game zips: the game's own MAME set, and BIOS and device zips such as `naomi.zip` and `jvs13551.zip`. Those nested zips are StoreZips, marked `format="storezip"` in the dat. With this tool you can build them yourself from your own MAME set, and they match the hashes in the dat.

The exact format is in [docs/StoreZip-Format.md](docs/StoreZip-Format.md).

## Download

Get the latest build from [Releases](https://github.com/Eggmansworld/Eggmans_StoreZip/releases):

| System | File |
|---|---|
| Windows (x64) | `EggmansStoreZip-vX.Y.Z-win-x64.zip` |
| Linux (x64) | `EggmansStoreZip-vX.Y.Z-linux-x64.tar.gz` |
| macOS (Apple silicon) | `EggmansStoreZip-vX.Y.Z-osx-arm64.tar.gz` |
| macOS (Intel) | `EggmansStoreZip-vX.Y.Z-osx-x64.tar.gz` |

Each build is self-contained, so no .NET install is needed.

## How to use

1. Pick an output folder (**Browse...**).
2. Drop MAME zips, or folders of them, onto the window (or use **Add zips...** / **Add folder...**). Each zip is converted as soon as it's dropped.
3. Put the files from the output folder in a RomVault ToSort and run Fix.

Use **MAME merged sets** of the MAME version the dat names, including for BIOS and device zips. The reference is Pleasuredome's "MAME 0.xxx ROMs (merged)" set from the [Pleasuredome MAME page](https://pleasuredome.github.io/pleasuredome/mame/index.html). Sets packed another way (non-merged, split, or "BIOS and devices" packs that fold device ROMs into the BIOS zip) hold different files, so they give a different zip.

The source zips can be any normal zip: TorrentZip, RomVault's ZSTD, 7-Zip, Info-ZIP and so on. The result doesn't depend on how the source was packed.

### Rename .zip to .zip_ (on by default)

RomVault must treat nested zips as plain files, not open them as sets. Written as `name.zip_`, RomVault doesn't recognise the extension and only hashes the file as a whole. Fix matches it to the dat by hash and gives it its proper `.zip` name inside the game zip. Leave this on unless you're using a "FileOnly" ToSort.

### Workers (2 by default)

How many zips are converted at the same time. 2 suits most PCs. Use 1 when the zips are on a NAS or a slow hard drive, or more on a fast SSD. The output is the same whatever the setting.

## First run

- **Windows:** run `EggmansStoreZip.exe`. SmartScreen may warn about an unknown publisher: choose **More info**, then **Run anyway**.
- **Linux:** extract the tar.gz and run `./EggmansStoreZip` (it's already executable).
- **macOS:** extract the tar.gz and move **Eggman's StoreZip.app** to Applications. The app isn't signed by Apple, so run this once in Terminal, then open it normally:

  ```bash
  xattr -cr "/Applications/Eggman's StoreZip.app"
  codesign --force --deep -s - "/Applications/Eggman's StoreZip.app"
  ```

Settings (the output folder, the `.zip_` option and Workers) are remembered in your user settings folder.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build EggmansStoreZip.slnx
./publish-storezip.ps1   # Windows, Linux and macOS packages in dist\
```

- `src/StoreZip.Core`: the StoreZip writer and check (stored, deflate and zstd sources; zip64 when needed). The TeknoParrot Collection's dat tools use it too.
- `src/StoreZip.App`: the Avalonia desktop app.

## License

[MIT](LICENSE)
