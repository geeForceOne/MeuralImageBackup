# Release notes

## 2.0.0 - 2026-11-08

MeuralImageUploader is now MeuralImageBackup: rebuilt on .NET 10 with a new interface. It's a drop-in replacement: the same port, volumes, database, folders and API, so Home Assistant and an existing installation keep working.

### New

- A new look, dark by default, with a light theme or one that follows the device, chosen in the sidebar.
- Galleries show as cards with their images, and each gallery has its own page.
- Images open large in a viewer: go through them with the arrow keys, open or download the original, or delete it.
- Sort and filter galleries and images by frame, name and when they were last shown; images also by missing files.
- Delete all galleries that haven't been shown for a while in one go.
- Works on tablets and phones.
- After an update, a banner links to these release notes.

### Fixed

- Images without a title now find their file (it's named after the Meural file id).
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
