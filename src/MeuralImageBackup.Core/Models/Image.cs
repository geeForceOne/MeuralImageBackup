namespace MeuralImageBackup.Core.Models;

// An image shown on a frame, once per Meural file id and gallery: the same file shown in another
// gallery is another image there, with its own file in that gallery's folder. Times are UTC.
// The shape is MeuralImageUploader's table "Images"; see the migrations.
public class Image
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public Gallery? Gallery { get; set; }

    // Meural's id of the image; names the file when the image has no title.
    public int FileId { get; set; }
    public string Name { get; set; } = null!;
    public DateTime CreatedAt { get; set; }

    // When the image was last reported, and how often.
    public DateTime LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
}
