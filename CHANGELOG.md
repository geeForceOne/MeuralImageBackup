# Release notes

## 2.0.0 - 2026-11-10

MeuralImageUploader is now MeuralImageBackup: rebuilt on .NET 10 with a new interface. It's a drop-in replacement: the same port, volumes, database, folders and API, so Home Assistant and an existing installation keep working. The image moved to `ghcr.io/geeforceone/meuralimagebackup`.

### New

- A new look, dark by default, with a light theme or one that follows the device, chosen in the sidebar. Works on tablets and phones.
- Galleries show as cards with their images, and each gallery has its own page. Images show as a grid or a table.
- Images open large in a viewer: go through them with the arrow keys, open or download the original, or delete it.
- Sort and filter galleries and images by frame, name and when they were last shown; images also by missing files.
- Delete all galleries that haven't been shown for a while in one go.
- **Sync with disk**: adds what's in the uploads folder but missing from the database. Folders become galleries and files become images of the gallery whose folder they're in. Nothing is removed.
- **Duplicates**: finds images whose files are byte for byte the same, for example the same artwork uploaded to Meural twice, and keeps one.
- **Loose files**: the files in the uploads folder that no image uses (outside a gallery folder, or not an image), with a delete per file or per group.
- **A photo source for MeuralManager**: MeuralManager can browse the backup by date or gallery and put images into its playlists, next to Immich, with a token from the new **Settings** page.
- After an update, a banner links to these release notes.

### Fixed

- Images without a title now find their file (it's named after the Meural file id).
- An image shown in a second gallery is listed there too, not only in the gallery that showed it first.
- A failed download no longer leaves an error page saved as an image.
- `/Images/` only serves the downloaded images, no longer the app's other files.

## 1.1.1 - 2023-03-20

### New

- Switched to the product version.

### Fixed

- A gallery's last access didn't update.

## 1.1.0 - 2023-03-19

### New

- Setting the environment variable `USE_FRAME_NAME_IN_PATH` to `false` leaves out the frames' folders.

### Fixed

- URL encoding.
- Empty files are caught better.

## 1.0.0 - 2023-03-17

- Initial release.
