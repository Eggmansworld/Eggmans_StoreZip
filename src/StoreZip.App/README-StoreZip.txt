Eggman's StoreZip
=================

Makes StoreZips: zips with every file stored (no compression), sorted, with
fixed dates and nothing else in the zip. The same ROMs always give the same
StoreZip, byte for byte, on any computer. The TeknoParrot Collection's nested
MAME zips (the game's set, and BIOS / device zips such as naomi.zip and
jvs13551.zip) are StoreZips, so you can build them yourself from your own MAME
set and they match the hashes in the dat.

HOW TO USE

1. Pick an output folder (Browse...).
2. Drop MAME zips, or folders of them, onto the window (or use Add zips... /
   Add folder...). Each zip is converted as soon as it's dropped.
3. Put the files from the output folder in a RomVault ToSort and run Fix.

Use Pleasuredome's MAME MERGED sets of the MAME version the dat names (the
dat's changelog says which), complete to Pleasuredome's merged dat for that
version. BIOS and device zips come from the same merged set. Sets packed
another way (non-merged, split, or "BIOS and devices" packs that fold device
ROMs into the BIOS zip) hold different files, so they give a different zip.

The source zips can be any normal zip: TorrentZip, RomVault's ZSTD, 7-Zip,
Info-ZIP and so on. The result doesn't depend on how the source was packed.

RENAME .ZIP TO .ZIP_ (ON BY DEFAULT)

Nested zips must be treated by RomVault as plain files, not opened as sets.
Written as name.zip_, RomVault doesn't recognise the extension and only hashes
the file as a whole, which is exactly what's needed. Fix matches it to the dat
by hash and gives it its proper .zip name inside the game zip. Leave this on
unless you know you're using a "FileOnly" ToSort.

WORKERS (2 BY DEFAULT)

How many zips are converted at the same time. 2 suits most PCs. Use 1 when
the zips are on a NAS or a slow hard drive, or more on a fast SSD. The output
is the same whatever the setting.

FIRST RUN

Windows: run EggmansStoreZip.exe. Windows SmartScreen may warn about an
  unknown publisher: More info, then Run anyway.

Linux: extract the tar.gz and run ./EggmansStoreZip (it's already executable).

macOS: extract the tar.gz and move "Eggman's StoreZip.app" to Applications.
  The app isn't signed by Apple, so once, in Terminal, run:

      xattr -cr "/Applications/Eggman's StoreZip.app"
      codesign --force --deep -s - "/Applications/Eggman's StoreZip.app"

  Then open it normally.

Settings (output folder, the .zip_ option, Workers) are remembered in your
user settings folder. StoreZip-Format.md, included here, is the exact format.
