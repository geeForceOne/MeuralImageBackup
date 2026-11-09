# Release notes

## 2.4.1 - 2026-11-09

### Fixed

- **Keep this one** no longer makes the list of identical files flicker: the group disappears and the others close the gap.

## 2.4.0 - 2026-11-09

### New

- **Loose files** page: files in the uploads folder that no image uses (outside a gallery folder, a name several images share, or not an image), with a delete per file or per group.

### Changed

- Each image is stored once: when a frame shows it from another gallery, it isn't downloaded again. The `SKIP_DUPLICATE_DOWNLOADS` setting is gone; a leftover one is ignored.
- **Keep this one** for identical files keeps that image as it is and removes the others, right away, without asking. Nothing is merged any more; **Keep one of each** does the whole list.

### Improved

- Sync with disk says why files were skipped (outside a gallery folder, or a name several images share), and the log names each one.

### Fixed

- Minor fixes and updates.

## 2.3.0 - 2026-11-09

### New

- **Identical files** on the Duplicates page: finds different images whose files are byte for byte the same, whatever their names, for example the same artwork uploaded to Meural twice.
- Keep one image of such a group and the others are merged into it: their galleries list it, their show counts are added, and their files are deleted. **Merge all** does every group at once, keeping the most shown image.

### Improved

- The Duplicates page now has two tabs: **Same image** (one image saved in several gallery folders) and **Identical files**.

## 2.2.0 - 2026-11-09

### New

- **Duplicates** page: lists every image that's stored in more than one gallery's folder, with how much space the extra copies take. Keep only the copy you choose, or one copy of each in one go (the one in the gallery the image was first shown in). The image stays listed in all its galleries, which show the copy that's left.
- New setting `SKIP_DUPLICATE_DOWNLOADS`: set it to `true` and an image that's stored already isn't downloaded again when a frame shows it from another gallery. That gallery lists it and shows the stored copy. Off by default, so the folders keep the layout they had.
- **Sync with disk** shows what it's doing while it runs, and can be stopped.
- **A photo source for MeuralManager**: MeuralManager can browse the backup by date or gallery and put images into its playlists, next to Immich. It reads through a read-only part of Immich's API, with a token generated on the new **Settings** page.
- **Settings** page: the MeuralManager token (generate, replace, revoke), and the container's settings at a glance.

### Fixed

- Deleting an image or a gallery no longer deletes an image's last copy while other galleries still list it: the file moves to one of those.
- A gallery whose copy of an image was deleted, while another gallery still has one, shows that copy instead of "file missing".

## 2.1.0 - 2026-11-08

### New

- **Sync with disk** on the Galleries page: adds what's in the uploads folder but missing from the database. Folders become galleries, and images found in a gallery's folder are listed in that gallery. Nothing is removed.
- An image can now be in several galleries. When a frame shows it from another gallery (for example after a gallery was renamed in Meural), it's listed there too, instead of only in the gallery that showed it first.

### Fixed

- Galleries whose images had all been seen in another gallery first showed "No images yet", although their folder was full. Run Sync with disk once to fill them.
- Deleting an image or a gallery only removes that gallery's copy; the image stays in its other galleries.

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
