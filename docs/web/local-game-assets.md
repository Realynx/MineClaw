# Local Minecraft resources and Controller audio

Settings → Set up Minecraft textures and sounds walks through two separate imports. Choose the launcher to see the
appropriate folder hints; Windows paths have a copy button. Other operating systems and custom launcher locations can
use the same file and folder pickers.

1. **Import textures:** choose a Java 1.12.2 or Beta 1.8.1 client JAR. Its version is detected and selected automatically.
   The textures step becomes ready and the Java sound step becomes the next action.
2. **Import sounds:** for Java 1.12.2, select the launcher's entire `assets` folder, including `indexes` and `objects`.
   The step displays the imported sound count and points to the Controller's Sound menu. Existing imports are recognized
   when Settings is reopened; replacements can be made independently.

Replacing the texture JAR preserves previously imported sound files and event mappings. Imports are saved atomically;
open windows at the same site address receive import/removal updates. The browser is asked to retain these resources
under storage pressure. A temporary storage-read failure is retried instead of being remembered as a missing import.

Beta textures work, but the launcher's legacy Beta sound folders are not supported by this walkthrough yet.
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

Prism Launcher keeps these resources outside individual instances. On a typical Windows install, its JAR is under
`%APPDATA%\PrismLauncher\libraries\com\mojang\minecraft\1.12.2`, and the sound folder is
`%APPDATA%\PrismLauncher\assets`, beside `instances` and `libraries`. Select `assets` itself, not an instance's
`minecraft` folder or one of the asset subfolders.

The Controller's visible Sound slider, plus/minus buttons and percentage control a volume saved on this device. Mute
remembers the prior volume. A click or key gesture enables Web Audio playback; Enable sound remains visible if the
browser needs that interaction after a refresh. Returning to the window resumes previously unlocked audio when allowed.
Live server sound packets and block-break effects travel through the existing entity event feed, deduplicated across
pawns. Freecam uses spatial distance and camera direction. Top-down uses map X/Z distance, so camera altitude and
terrain height do not suppress sound. Audio stops on leaving Controller, muting, or hiding the browser tab; there is no
offline sound replay or background music download.

Implementation references: [browser-local storage](https://developer.mozilla.org/en-US/docs/Web/API/IndexedDB_API),
[Web Audio playback](https://developer.mozilla.org/en-US/docs/Web/API/Web_Audio_API/Using_Web_Audio_API), and
[1.12 sound event definitions](https://docs.minecraftforge.net/en/1.12.x/effects/sounds/).
