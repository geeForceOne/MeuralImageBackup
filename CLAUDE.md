# CLAUDE.md

MeuralImageBackup is a Docker-hosted Blazor Server app (.NET 10, `.slnx`) that keeps a local copy of every image shown on Meural Canvas frames. Home Assistant posts each frame's current image to `POST /api/meural`. The app downloads it into a folder per frame and gallery and records it in SQLite. There is one user and no login, so anything other apps keep per user is kept per browser here. It replaced MeuralImageUploader 1.1.1 (.NET 7, Radzen) and must stay a drop-in replacement for it.

`src/MeuralImageBackup.Core` holds the domain, EF Core and file logic and never depends on the UI. It does no console or UI I/O: log through `ILogger`, and long-running methods take `IProgress<string>?` and a `CancellationToken`. `src/MeuralImageBackup.Web` holds the UI and the APIs.

## Commands

```
dotnet build MeuralImageBackup.slnx
dotnet run --project src/MeuralImageBackup.Web         # port 5188, data in ./data
dotnet test tests/MeuralImageBackup.Tests
docker compose up --build                              # host port 8090
dotnet ef migrations add <Name> --project src/MeuralImageBackup.Core --startup-project src/MeuralImageBackup.Web --output-dir Data/Migrations
dotnet ef migrations has-pending-model-changes --project src/MeuralImageBackup.Core --startup-project src/MeuralImageBackup.Web   # after any model change
```

## Compatibility (hard rule: if a change would break one of these, stop and ask)

- **Container:** port 80 (`ASPNETCORE_HTTP_PORTS=80`), working directory `/app`, runs as root.
- **Volumes:** `/app/uploads` and `/app/database` (`meural.db`). Nothing else is persistent. Anything new that must survive a restart goes under `/app/database`, such as the Data Protection keys in `database/keys`. Thumbnails are a temp-folder cache and never go in a volume.
- **`USE_FRAME_NAME_IN_PATH`:** defaults to true, and any value that isn't a bool counts as true. `false` drops the frame folder level.
- **On-disk layout:** `uploads/<frame>/<gallery>/<title>.jpeg`, or `uploads/<gallery>/<title>.jpeg` without the frame level. `FileSystemHelpers` removes characters Windows doesn't allow. A missing frame or gallery becomes `Unknown`. A missing title becomes the Meural file id, then `Unknown`, never the database row id.
- **`POST /api/meural`:** takes JSON `{ frameName, title, gallery, url, id }`. All fields are optional and case-insensitive, and `id` is an int. It registers the gallery and image with the old "last gallery per frame" counting. It downloads the file unless it already exists at that path, deletes a zero-byte result, and **always answers 200 with an empty body**. No authentication.
- **`GET /api/version`:** the four-part assembly version as plain text.
- **`/Images/uploads/...`** serves `/app/uploads` only, never the rest of `/app` (that would expose the database).
- **Old routes:** `/Gallery`, `/Image` and `/ReleaseNotes` keep working. All routes are case-insensitive.
- **Database:** `meural.db` is upgraded in place. The tables `Galleries`, `Images` and `LastGalleries` and the migrations `20230305214800__001_Initial` and `20230320163505_002_LastAccessFix` keep their IDs. Schema changes go only in new migrations. Never edit or regenerate an existing migration.
- **Photo source API** (`Api/PhotoSourceApi.cs`): answers the subset of Immich's API that MeuralManager's `ImmichApiClient` uses, in Immich's JSON shapes; keep the shapes in step with that client. It is read only and needs `X-API-Key`. Only a hash of the key is stored.

## Decisions that aren't visible in the code

- An image belongs to exactly one gallery, the one whose folder holds its file. The same Meural file shown in another gallery is a separate row with its own file, and deleting one never touches the other.
- Duplicates: keep one image and remove the others. No merging.
- `GalleryImages` is legacy: nothing writes to it, but the table and its migration stay. The one-time Clean up deletes the extra copies but never a file that another row uses.
- Sync with disk and the cleanups only run when the user starts them. Sync never removes anything.
- What's new: any version other than the stored one counts, a lower one too. The pre-release builds were tagged 2.0.0 to 2.5.0 before everything was renumbered 2.0.0. Local dev builds show nothing.
- Each page remembers its view and sort per browser. Search, frame and "not shown for" are not remembered.
- US English. Numbers and dates go through the region format (`Format`), never the server culture. Times are stored in UTC.

## UI conventions and gotchas

- The style follows the sibling apps Trovami and LaLira. Copy code from them rather than referencing them. CSS is hand-written in `wwwroot/css/app.css` with no component library. Pages use the full window width. `--accent` is for actions and links; `--attention` is only for things that need attention, like missing files.
- Use the shared `ConfirmDialog`, `PromptDialog` and `Toast`. Clicking outside a dialog never closes it.
- Per-browser preferences go through `Prefs` (`meural.getPref`/`setPref`).
- Enter dates and times with `DateField`/`TimeField`, never a bare `<input type="date|time">`.
- Table cells are never `display: flex`; put a `<div class="row-actions">` inside the cell. Header alignment needs `.table th.num`.
- Numbers written into SVG attributes use the invariant culture.
- A callback passed as a plain `Func<>` needs `StateHasChanged`. Never name a parameter `Assets`, because it collides with .NET 10.
- A dialog opened from the image viewer must return focus with `meural.keepFocus`.
- Bool aria attributes are written as strings: `aria-pressed="@(x ? "true" : "false")"`.
- `favicon.svg` mirrors the small `BrandMark` drawing. Nothing moves under `prefers-reduced-motion`.
- Check external library APIs against current docs, not memory.

## Git

- Start every change on a branch off an up-to-date main. After a merge: pull main, `git branch -d` the merged branch, `git fetch --prune`.
- Commits and tags are made as `geeForceOne <1780268+geeForceOne@users.noreply.github.com>`. The repo's local git config sets this.
- Never commit databases, keys, downloaded images, `.env` or local settings. Check the staged files before every commit.
- Pushing a `vX.Y.Z` tag publishes the image to ghcr.io and creates the GitHub release from `CHANGELOG.md`. That is public.
