# Local Minecraft resources and Controller audio

Settings → Minecraft textures and sounds imports a Java 1.12.2 or Beta 1.8.1 client JAR using the browser's file picker.
The browser reads only allowlisted textures, models, blockstates and sound resources. It never executes Java classes or
uploads the archive. The prepared atlases and audio remain in IndexedDB for that site address on that browser profile.
An installed PWA uses the same origin's storage. HTTP and HTTPS addresses have separate storage.

No Minecraft PNG or OGG files are shipped in the web bundle. Without imported resources, the viewer uses its original
flat-colour geometry. Imported packs replace the placeholders in open viewers. Remove deletes that version's local pack.
The old extraction and packing scripts are optional local development utilities; their PNG/OGG outputs are ignored by Git.
Geometry metadata fixtures remain for renderer regression tests. This change does not rewrite repository history.

For Java 1.12.2, the client JAR is normally `.minecraft/versions/1.12.2/1.12.2.jar`. Sounds are downloaded separately by
the Minecraft launcher. After launching the version once, import `.minecraft/assets` with the sound folder picker. The
importer reads `indexes/1.12.json` and its referenced hash-named `objects` files; it does not fetch assets from the internet.
The original JAR alone generally contains no sounds. Missing resources remain silent rather than using unrelated clips.

The Controller's Sound menu controls a volume saved on this device. A click or key gesture enables Web Audio playback.
Live server sound packets and block-break effects travel through the existing entity event feed, deduplicated across
pawns. Freecam uses spatial distance and camera direction. Top-down uses map X/Z distance, so camera altitude and
terrain height do not suppress sound. Audio stops on leaving Controller, muting, or hiding the browser tab; there is no
offline sound replay or background music download.

Implementation references: [browser-local storage](https://developer.mozilla.org/en-US/docs/Web/API/IndexedDB_API),
[Web Audio playback](https://developer.mozilla.org/en-US/docs/Web/API/Web_Audio_API/Using_Web_Audio_API), and
[1.12 sound event definitions](https://docs.minecraftforge.net/en/1.12.x/effects/sounds/).
