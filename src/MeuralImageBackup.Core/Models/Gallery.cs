namespace MeuralImageBackup.Core.Models;

// A gallery (Meural's playlist) as shown on one frame. The same gallery on two frames is two rows.
// Times are UTC. The shape is MeuralImageUploader's table "Galleries"; see the migrations.
public class Gallery
{
    public int Id { get; set; }
    public string FrameName { get; set; } = null!;
    public string Name { get; set; } = null!;
    public DateTime CreatedAt { get; set; }

    // When the frame last switched to this gallery, and how often it did.
    public DateTime LastAccessedAt { get; set; }
    public int AccessCount { get; set; }

    public List<Image>? Images { get; set; }
}
