# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

MeuralImageBackup is a Docker-hosted web app that keeps a local copy of every image shown on Meural Canvas frames. Home Assistant posts the frame's current image to the API; the app downloads it into a folder per frame and gallery and records galleries and images (with access counts and last access) in SQLite. The web UI lets you browse, filter, preview and clean up what was collected.

This repository is a **migration** of the old MeuralImageUploader (.NET 7, Radzen, at `D:\Programming\HA\MeuralImageUploader`, github.com/geeForceOne/MeuralImageUploader, last version 1.1.1). The old code is the reference for behavior. Its look and UI are not: the new UI is rebuilt to the standard of the sibling apps Trovami (`D:\Programming\Trovami`) and LaLira (`D:\Programming\LaLira`), whose code is the reference for patterns and style. Copy what's needed into this repository rather than depending on them.

- **The Docker setup and the API stay compatible** (see Compatibility). An existing installation must keep working after swapping the image: same volumes, same database, same files, same Home Assistant calls.
- **The UI and its functionality are modernized.** No Radzen. Hand-written UI in the style of Trovami and LaLira.
- **Single user, no login**, as before. There are no accounts, so anything Trovami keeps per user (theme, dismissed What's new) is kept per browser here.

### Functionality of the old app (to be modernized, not copied)
- **Galleries.** A table of galleries per frame: frame, name, access count, last accessed, image count. Filters: frame, "not accessed for N days", gallery name. A click on the name previews the gallery's images. Deleting a gallery deletes its folder on disk and its database rows.
- **Images.** A table of all images: frame, gallery, a thumbnail and name, whether the file exists on disk, access count, last accessed, with the same filters. "Delete images with no file" removes the rows whose file is missing.
- **Release notes.** A hand-written page. It's replaced by the page built from `CHANGELOG.md` (see What's new).

### Taken over from Trovami and LaLira
- **What's new.** After an update the browser shows a banner ("MeuralImageBackup was updated to X.Y.Z") linking to the release notes page, built from `CHANGELOG.md`, until it's closed. It's remembered per browser, since there are no users. A browser isn't told about the version it first saw, and local "dev" builds show nothing. Carry the old release notes (1.0.0 to 1.1.1) into `CHANGELOG.md`.
- **Themes.** Dark (default), Light or System, rendered as `<html data-theme>`, remembered per browser.

### Wording
- **US English only**, American spelling, in the UI, docs and comments. No localization layer, but numbers and dates are still formatted with a region format (`Format`), never the server culture. Without users, the region format comes from the browser (the old app was fixed to de-CH, which stays the fallback).

## Compatibility (hard rule)

Every change must keep these working, so existing installations and Home Assistant automations keep running unchanged. If a change would break one, stop and ask.

- **Container.** Listens on port **80** inside the container (`EXPOSE 80`). .NET 8+ images default to 8080, so the Dockerfile sets `ASPNETCORE_HTTP_PORTS=80` (or `ASPNETCORE_URLS=http://+:80`). The working directory is `/app` and the app runs as root, as before, so existing volumes work without changing their owner.
- **Volumes.** `/app/uploads` (the images) and `/app/database` (the SQLite file `meural.db`). Nothing else is persistent. Anything new that must survive a restart (Data Protection keys, settings) goes under `/app/database`, never into the image or a new volume.
- **Environment.** `USE_FRAME_NAME_IN_PATH` (default `true`; anything that doesn't parse as a bool counts as `true`): `false` leaves out the frame folder level. (`SKIP_DUPLICATE_DOWNLOADS` existed in 2.2.0 to 2.3.0 and is gone: its behavior is the default now, and a leftover variable is ignored.)
- **On-disk layout.** `uploads/<frame>/<gallery>/<title>.jpeg`, or `uploads/<gallery>/<title>.jpeg` without the frame level. Names are cleaned of the characters invalid on Windows (`FileSystemHelpers`); a missing frame or gallery becomes `Unknown`; a missing title becomes the Meural file id, then `Unknown`. Existing files must be found by the new code with exactly these rules. (The old UI and `GetRandomFileAsync` built the fallback name from the database row id instead of the file id; that's a bug, not part of the contract.)
- **`POST /api/meural`.** JSON body `{ "frameName", "title", "gallery", "url", "id" }` (all optional, case-insensitive, `id` an int). It registers the gallery and image (with the old "last gallery per frame" counting), downloads `url` to the path above unless the file exists there or, for a recorded image, in any of its galleries' folders (each image is stored once since 2.3.1, the user's decision), deletes a zero-byte result, and **always answers 200** with an empty body, even when the download fails. No authentication.
- **`GET /api/version`.** Returns the four-part assembly version as plain text (for example `1.1.1.0`).
- **`/Images/uploads/...`.** Image URLs under `/Images/` keep resolving to the files in `/app/uploads`. The old app served all of `/app` there, including the database; that isn't part of the contract and must not be kept.
- **Database.** The existing `meural.db` is upgraded in place. The tables (`Galleries`, `Images`, `LastGalleries`), their columns and the two existing migrations (`20230305214800__001_Initial`, `20230320163505_002_LastAccessFix`) are carried over with the same migration IDs, so `__EFMigrationsHistory` matches. New schema only through new migrations.
- Route matching stays case-insensitive (`/api/Meural` works too).

## Commands

```
dotnet build MeuralImageBackup.slnx                    # build everything
dotnet run --project src/MeuralImageBackup.Web         # run the app locally (port 5188, data in ./data)
dotnet test tests/MeuralImageBackup.Tests              # unit tests
docker compose up --build                              # build and run the container (host port 8090)
dotnet ef migrations add <Name> --project src/MeuralImageBackup.Core --startup-project src/MeuralImageBackup.Web --output-dir Data/Migrations
```

## Workflow

When the user gives feedback or requests changes in a back-and-forth (bug reports, tweaks, "also do X"), don't implement each one immediately as it comes in. Acknowledge it, queue it, and keep gathering. Only start making changes once the user explicitly says "go" (or an equally unambiguous confirmation). Purely informational questions can still be answered right away.

## Git workflow

Never run `git commit` or `git push` unless the user explicitly asks for it in that turn. A commit approval does not imply a push approval, and approval from an earlier turn does not carry forward to later changes. Every change starts on its own branch off an up-to-date main; after a merge, pull main, delete merged branches (`-d`) and `git fetch --prune`. Commit as `geeForceOne <1780268+geeForceOne@users.noreply.github.com>`, never with a real name or Gmail. Write commit messages that explain why.

Nothing secret or personal is ever committed: databases, Data Protection keys, downloaded images, `.env`, local settings. Check staged files before every commit.

## Architecture

Latest .NET (currently .NET 10, `net10.0`), Blazor Server, `.slnx` solution.

- **`src/MeuralImageBackup.Core`** (class library, no dependency on the web UI): domain models, EF Core persistence and migrations, gallery and image registration, the download, file naming and folder layout, cleanup. Long-running methods take an `IProgress<string>?` and a `CancellationToken`; no console or UI I/O in Core (log through `ILogger`, not `Console.WriteLine`).
- **`src/MeuralImageBackup.Web`** (Blazor Server, interactive server render mode, prerendering off): UI, hosting, and the compatible API as minimal APIs (`Api/CompatibleApi.cs`: `/api/meural`, `/api/version`), plus `/thumbs/{id}` for the grids.
- **Photo source API** (`Api/PhotoSourceApi.cs`, 2.2.0): MeuralManager (`D:\Programming\MeuralUi`) browses the backup through it as a source next to Immich, with a second instance of its `ImmichApiClient`. So it answers the subset of Immich's REST API that client uses, in Immich's JSON shapes (`users/me`, `search/metadata`, `search/smart`, `albums`, `assets/{id}/thumbnail`, `assets/{id}/original`); keep the shapes in step with that client. No people. An asset is an image with a file (id = image id, date = first saved); an album is a gallery. Read only. Every call needs the token from the Settings page in `X-API-Key` (`ApiTokenService`: only a SHA-256 hash is stored, in the `Settings` table, migration `004_Settings`); no token, no access. Status code pages are skipped there (`SkipStatusCodePages`), so errors are JSON.
- **The UI reads everything at once.** `LibraryService.LoadAsync` loads all galleries and images and walks the uploads folder once to see which files exist; pages filter and sort in memory (`Filters`). Filters live in the URL query, so Back restores them.
- **An image can be in several galleries.** `Images.GalleryId` is the gallery it was first shown in (as before); `GalleryImages` (migration `003_GalleryImages`, 2.1.0) links it to every other gallery that showed it or whose folder has its file. Each gallery's folder usually has its own copy (see Shared copies), and the UI lists one `ImageView` per image and gallery (`Key` tells them apart; thumbnails take `?g=`). Deleting an image or gallery removes only that gallery's copy; an image leaving its own gallery moves to another one it's in.
- **Shared copies.** A gallery whose folder lacks an image's file shows the copy another of the image's galleries has (`ImageView.StoredInGalleryName`); it isn't "missing". So deletes never remove an image's last copy while other galleries list it: `KeepLastCopyAsync` moves the file to the gallery the image belongs to next. The Duplicates page (`/duplicates`, `Library.Duplicates`) deletes extra copies but keeps every gallery's listing. Its "Identical files" tab (`?kind=files`, `IdenticalFilesService`) finds *different* images whose files are byte for byte the same (SHA-256, only files sharing a size are read, fingerprints cached in memory by path, size and time); `LibraryService.RemoveIdenticalAsync` keeps one image and removes the others (their files and entries; the kept image is unchanged, no merging, the user's decision).
- **Sync with disk** (`LibraryService.SyncWithDiskAsync`, a button on the Galleries page, never automatic, with progress and Cancel): adds galleries for unrecorded folders, links files to images recorded elsewhere (matched by file name), and adds entries for files nobody recorded (FileId from a numeric name, else 0, which registration fills in later). It never removes anything. Its matching runs in one place (`SyncAsync`), also as a dry run for `FindLooseFilesAsync`: image files outside a gallery folder, image files whose name several images share (it won't guess), and non-images. The Loose files page (`/files`) lists and deletes those; `DeleteLooseFilesAsync` re-checks the list, so it never deletes a file an image uses.
- **Pages:** Galleries (`/`, cards, stats, filters, delete what hasn't been shown for N days), a gallery (`/g/{id}`), Images (`/images`, grid or table, "only missing files", remove entries without a file), Duplicates (`/duplicates`), Loose files (`/files`), Settings (`/settings`: the API token, the container's settings), Release notes. The old routes `/Gallery`, `/Image` and `/ReleaseNotes` still work. `ImageViewer` shows one image large (arrow keys, Escape, open, download, delete).
- **`tests/MeuralImageBackup.Tests`**: file naming and folder layout against the old rules, gallery and image registration, the library's deletes, images in several galleries and the sync, an EF Core 7 `meural.db` taken over as it is, and the HTTP contract through `WebApplicationFactory`.

### Hosting and data
- **SQLite** through EF Core, at `/app/database/meural.db` in the container. Paths come from `Storage__UploadsPath`, `Storage__DatabaseDirectory` and `Storage__ThumbnailsPath`, with defaults that match the old relative paths (`uploads/`, `database/` under the working directory); see Compatibility. Local development points them at `./data` (appsettings.Development.json). The Data Protection keys go to `database/keys`.
- **Thumbnails** (SkiaSharp, longest side 480 px) are only a cache, in the temp folder by default, keyed by the original's path, size and time; never put them in a volume.
- **Region format and time zone come from the browser**: the culture from `Accept-Language` (de-CH when it doesn't say), the time zone from the `mib-tz` cookie App.razor's script sets (cascaded as `AppUi.Zone`). Times are stored in UTC.
- A new repository, github.com/geeForceOne/MeuralImageBackup; the old MeuralImageUploader repository stays as it is.
- A `vX.Y.Z` tag runs GitHub Actions: `docker-publish.yml` sends the image to `ghcr.io/geeforceone/meuralimagebackup` only (no Docker Hub), as X.Y.Z / X.Y / latest. Existing MeuralImageUploader installs switch by changing the image name; everything else stays compatible. The workflow also creates the GitHub release from that version's `CHANGELOG.md` section. The version is passed as a build argument and shows in the app and in `/api/version`.

## Conventions

- **Verify external APIs, don't work from memory.** Check current official docs before coding against a library or service. Package versions and SDK shapes change.
- **Shared dialogs.** Reusable, awaitable `ConfirmDialog`, `PromptDialog` and `Toast` components in `Components/Shared/`. Clicking outside a dialog never closes it; Cancel (or Escape where offered) does.
- **Hand-written CSS design system** in `wwwroot/css/app.css`, no component library: "a gallery after hours": charcoal walls, one gilt accent (`--accent`) for actions and links, the logo's coral (`--attention`) only for what needs attention (missing files). IBM Plex Sans and Mono, self-hosted. Dark-first with a light theme (Dark, Light, System in the sidebar, per browser). Responsive: sidebar on desktop, icon rail on tablets, top bar and tab bar on phones. Every page uses the full window width, with no maximum; only small controls size to their content.
- **Per-browser preferences** (theme, the seen version for What's new, the Images view) go through `meural.getPref` / `meural.setPref` (localStorage, `mib.` prefix), wrapped so blocked storage falls back to the default.
- **Logo** (`BrandMark`): a framed picture (hills and a coral sun) hanging in front of its copy, the backup. Animated like Trovami's and LaLira's (`brand-motion.js`: copy, shine, swing, develop): the header logo plays a motion on hover, the busy indicator (loading, reconnecting) loops. Below 40 px a simpler drawing with thicker lines; `favicon.svg` is that one with fixed colors, keep the two in step. Nothing moves under `prefers-reduced-motion`.
- **Dates and times are entered in the region format**: use `DateField` / `TimeField`, never a bare `<input type="date">` or `type="time"`.
- **Table cells are never `display: flex`** (it breaks the row lines); put a `<div class="row-actions">` inside the cell.
- **Migrations are never regenerated or edited once they exist**, not even before the first release: existing databases record them by ID. A schema change is always a new migration.
- **Gotcha:** numbers written into SVG attributes must use the invariant culture.
- **Gotcha:** a component callback passed as a plain `Func<...>` (not EventCallback) doesn't re-render the parent; call StateHasChanged after it changes state.
- **Gotcha:** don't name a component parameter `Assets`; it collides with `ComponentBase.Assets` in .NET 10.
- **Gotcha:** a dialog opened from the image viewer takes the keyboard focus; `meural.keepFocus` gives it back when the dialog closes, or the arrow keys and Escape stop working.
- **Gotcha:** EF Core 9 and later refuse to migrate when the model differs from the last migration's snapshot. After a model change, check with `dotnet ef migrations has-pending-model-changes` (same project arguments as above).
- **Gotcha:** Blazor renders a bool attribute value `true` as an empty attribute and drops it for `false`; write `aria-pressed="@(x ? "true" : "false")"` (same for aria-expanded and aria-checked).
