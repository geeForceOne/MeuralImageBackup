namespace MeuralImageBackup.Core.Models;

// An image that's also in a gallery other than its own (Image.GalleryId): the same Meural file shown
// from a second gallery, or found in that gallery's folder by the sync. Its file is a separate copy
// in that gallery's folder. Added in 2.1.0 (migration 003_GalleryImages).
public class GalleryImage
{
    public int GalleryId { get; set; }
    public int ImageId { get; set; }
}
