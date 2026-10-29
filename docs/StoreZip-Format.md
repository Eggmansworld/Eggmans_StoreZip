# StoreZip format (version 1)

A StoreZip is an ordinary zip file with every member **stored** (no compression) and every other byte fixed, so the bytes depend only on the member names and contents. Anyone who builds a StoreZip from the same files gets the same file, with the same size, CRC32 and SHA-1, on any operating system and with any tool that follows this spec.

The TeknoParrot Collection uses StoreZips for every nested zip that comes from a MAME set: the game's own set, and the BIOS and device zips (`naomi.zip`, `jvs13551.zip` and so on). Their `<romzip>` line in the dat carries `format="storezip"`. Nested zips that don't come from MAME are left exactly as the game ships them.

## Why stored, and why it must be deterministic

The game zips that hold these nested zips are compressed as a whole by RomVault (zstd or torrentzip). Compressing the nested zip as well only makes the outer compression worse, so the nested zips are stored.

Zipping tools write whatever the file system has: modified dates, folder entries, extra timestamp fields, attributes and their own file order. Extracting a zip resets those to the local PC's values, so two people repeating the same steps get different bytes. A StoreZip is never built from extracted files. It is built straight from the members of a source zip, and nothing from the file system is written.

## The rules

Values are little-endian, as in every zip.

1. **Members.** Every file in the source zip. Folder entries (names ending in `/`) are not written. Paths keep their folders, as in `18wheelro/epr-22185.ic22`, with `/` as the separator (a `\` in a source name becomes `/`). A name may appear only once.
2. **Order.** Sorted by name lower-cased (invariant culture, ordinal compare). Two names that differ only in case are ordered by ordinal compare of the exact names. This is TorrentZip's order.
3. **Names.** Written as ASCII when every character is printable ASCII (0x20 to 0x7E), with general-purpose flags `0x0000`. Otherwise UTF-8, with flags `0x0800`.
4. **Local file header** for each member, followed by the name and then the data:

   | Field | Value |
   |---|---|
   | signature | `0x04034b50` |
   | version needed | 10, or 45 if the member uses zip64 (rule 7) |
   | flags | per rule 3 |
   | method | 0 (stored) |
   | time / date | `0xBC00` / `0x2198` (1996-12-24 23:32:00, as TorrentZip) |
   | CRC32 | the member's CRC32 |
   | compressed / uncompressed size | the member's size (both the same) |
   | name length | byte length of the name |
   | extra length | 0, or 20 if the size needs zip64 |

   There are no data descriptors.
5. **Central directory**, in the same order as the local headers:

   | Field | Value |
   |---|---|
   | signature | `0x02014b50` |
   | version made by | same as version needed (host 0, MS-DOS) |
   | version needed, flags, method, time, date, CRC32, sizes | as in the local header |
   | name length | as in the local header |
   | extra length | 0, or the zip64 field's size (rule 7) |
   | comment length, disk number, internal and external attributes | 0 |
   | local header offset | offset of the member's local header |

6. **End of central directory**:

   | Field | Value |
   |---|---|
   | signature | `0x06054b50` |
   | disk numbers | 0 |
   | entry counts (both) | number of members |
   | central directory size and offset | as written |
   | comment length | 0 |

   There is no zip comment.
7. **Zip64.** Used only where a value doesn't fit:
   - **Member size ≥ `0xFFFFFFFF`:** both size fields hold `0xFFFFFFFF`. A zip64 extra field (`0x0001`) follows the name: in the local header it is 16 data bytes (uncompressed size, then compressed size). In the central directory the same two values come first.
   - **Local header offset ≥ `0xFFFFFFFF`:** the central directory's offset field holds `0xFFFFFFFF`, and the offset goes in the zip64 extra field after any sizes.
   - **Version:** any member with either case uses version 45 in both headers.
   - **Archive level:** when the member count is ≥ `0xFFFF`, or the central directory's size or offset is ≥ `0xFFFFFFFF`, a zip64 end-of-central-directory record follows the central directory:
     - size 44, versions 45 and 45, disks 0;
     - the counts, central directory size and offset as 64-bit values.

     A zip64 locator follows it (disk 0, the record's offset, 1 disk), then the normal end record with the values that don't fit set to `0xFFFF` / `0xFFFFFFFF`.

A zip is a StoreZip when building a StoreZip from its own members gives back the same bytes.

## Making one from your own MAME set

Use **MAME merged sets** of the MAME version the dat names (currently 0.289), including for BIOS and device zips. Sets packed another way hold different files, so they give a different zip. For example, some "BIOS and devices" packs fold device ROMs into `naomi.zip`.

For best results, use the trusted Pleasuredome set as your reference MAME set.  If you get your MAME sets from anywhere else, I guarantee nothing.
https://pleasuredome.github.io/pleasuredome/index.html

Specifically, on the MAME page, download the latest "Set: MAME 0.xxx ROMs (merged)" torrent and download its content.
https://pleasuredome.github.io/pleasuredome/mame/index.html#:~:text=MAME%20full%20sets%3A-,MAME%20ROMs,-Set%3A%20MAME

1. Open **Eggman's StoreZip** (Windows, Linux and macOS).
2. Pick an output folder.
3. Drop the MAME zips (or folders of them) onto the window.

Each zip is rebuilt as a StoreZip in the output folder. With **Rename .zip to .zip_** ticked (the default), they are written as `name.zip_`. RomVault then treats each one as a plain file and never looks inside, so it can't confuse the nested zip with a set of its own. Put them in a RomVault ToSort and run Fix: RomVault matches them to the dat by hash and gives them their proper `.zip` names inside the game zips.

The source can be any zip with stored, deflate or zstd members (TorrentZip, RVZSTD, 7-Zip, Info-ZIP and others). The result is the same whatever the source format.

## When MAME changes

The dats follow the latest MAME. When a new MAME version changes a set's files, the nested zip's member list changes in the dat and so does its StoreZip. Rebuild it from the new MAME set.
