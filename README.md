# MeuralImageBackup

Keeps a local copy of every image shown on your Meural Canvas frames. Home Assistant tells the app which image a frame shows; the app downloads it into a folder per frame and gallery and remembers how often and when each gallery and image was shown. The web interface lets you browse, filter and clean up the collection.

The successor of MeuralImageUploader 1.x, and a drop-in replacement for it: same port, volumes, database, folders and API.

## Run it

```yaml
services:
  meuralimagebackup:
    image: ghcr.io/geeforceone/meuralimagebackup:latest
    ports:
      - "8090:80"
    environment:
      USE_FRAME_NAME_IN_PATH: "true"
    volumes:
      - ./uploads:/app/uploads
      - ./database:/app/database
    restart: unless-stopped
```

- `/app/uploads`: the images, as `<frame>/<gallery>/<title>.jpeg`.
- `/app/database`: the SQLite database `meural.db` (and the app's keys).
- `USE_FRAME_NAME_IN_PATH=false` leaves out the frame's folder: `<gallery>/<title>.jpeg`.

The image is also published to Docker Hub under MeuralImageUploader's old name, so existing installations update by pulling `latest`.

## API

### `POST /api/meural`

```json
{ "frameName": "Living room", "gallery": "Impressionists", "title": "Water Lilies", "url": "https://…/image.jpg", "id": 123456 }
```

Records the gallery and image and downloads `url`, unless the file is already there. Always answers `200`.

A Home Assistant `rest_command` for it:

```yaml
rest_command:
  meural_backup:
    url: http://<host>:8090/api/meural
    method: POST
    content_type: application/json
    payload: >
      {"frameName": "{{ frame }}", "gallery": "{{ gallery }}", "title": "{{ title }}",
       "url": "{{ url }}", "id": {{ id }}}
```

### `GET /api/version`

The app's version as plain text, for example `2.0.0.0`.

## Develop

```
dotnet build MeuralImageBackup.slnx
dotnet run --project src/MeuralImageBackup.Web     # data in ./data
dotnet test tests/MeuralImageBackup.Tests
```
