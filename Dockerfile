# Build from the repository root:
#   docker build -t meuralimagebackup .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/MeuralImageBackup.Core/MeuralImageBackup.Core.csproj src/MeuralImageBackup.Core/
COPY src/MeuralImageBackup.Web/MeuralImageBackup.Web.csproj src/MeuralImageBackup.Web/
RUN dotnet restore src/MeuralImageBackup.Web/MeuralImageBackup.Web.csproj

COPY src/ src/
# Built into the app for its release notes page.
COPY CHANGELOG.md ./
# The version shown in the app and by /api/version; the publish workflow passes the release tag's number.
ARG VERSION=dev
RUN dotnet publish src/MeuralImageBackup.Web/MeuralImageBackup.Web.csproj -c Release -o /app -p:InformationalVersion=$VERSION

# The regular (Ubuntu-based) runtime image ships ICU, which the app needs for region formats such
# as de-CH. Don't switch to a chiseled or invariant-globalization image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Compatible with MeuralImageUploader 1.x, so an existing installation keeps working after swapping
# the image (see "Compatibility" in CLAUDE.md):
# - port 80 inside the container (.NET 8 and later default to 8080);
# - the images in /app/uploads and the SQLite database in /app/database (relative to /app);
# - USE_FRAME_NAME_IN_PATH, true unless set to false.
# SKIP_DUPLICATE_DOWNLOADS (new, off unless set to true) stores each image only once.
# Runs as root, like before, so existing volumes work without changing their owner.
ENV ASPNETCORE_HTTP_PORTS=80 \
    USE_FRAME_NAME_IN_PATH=true
VOLUME ["/app/uploads"]
VOLUME ["/app/database"]
EXPOSE 80

ENTRYPOINT ["dotnet", "MeuralImageBackup.Web.dll"]
