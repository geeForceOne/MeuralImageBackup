namespace MeuralImageBackup.Core.Models;

// The gallery each frame showed last, so a gallery's access count goes up only when the frame
// switches to it, not with every image. One row per frame.
public class LastGallery
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string FrameName { get; set; } = null!;
}
