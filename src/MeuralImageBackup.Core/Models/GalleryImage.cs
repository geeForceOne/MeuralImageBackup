namespace MeuralImageBackup.Core.Models;

// Legacy, from pre-release builds: an image that was also listed in a gallery other than its own
// (Image.GalleryId), with a copy of its file in that gallery's folder (migration 003_GalleryImages).
// Now an image belongs to one gallery and nothing writes this table any more; the one-time
// cleanup (LibraryService.CleanUpLegacyAsync) empties it. A later migration can drop it.
public class GalleryImage
{
    public int GalleryId { get; set; }
    public int ImageId { get; set; }
}
