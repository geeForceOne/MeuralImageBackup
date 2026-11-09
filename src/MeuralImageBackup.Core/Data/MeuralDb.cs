using MeuralImageBackup.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeuralImageBackup.Core.Data;

// MeuralImageUploader's database, upgraded in place: the same tables, columns, index names and
// migration ids, so an existing meural.db is recognized as up to date. Change the schema only with
// a new migration, never by editing the existing ones.
public class MeuralDb(DbContextOptions<MeuralDb> options) : DbContext(options)
{
    public DbSet<Gallery> Galleries => Set<Gallery>();
    public DbSet<LastGallery> LastGalleries => Set<LastGallery>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<GalleryImage> GalleryImages => Set<GalleryImage>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Gallery>(b =>
        {
            b.HasIndex(g => g.Name).HasDatabaseName("IX_Gallery_Name");
            b.HasMany(g => g.Images).WithOne(i => i.Gallery).HasForeignKey(i => i.GalleryId);
        });

        modelBuilder.Entity<Image>(b =>
        {
            b.HasIndex(i => i.Name).HasDatabaseName("IX_Image_Name");
            b.HasIndex(i => i.FileId).HasDatabaseName("IX_Image_FileId");
        });

        modelBuilder.Entity<GalleryImage>(b =>
        {
            b.HasKey(l => new { l.GalleryId, l.ImageId });
            b.HasIndex(l => l.ImageId);
            b.HasOne<Gallery>().WithMany().HasForeignKey(l => l.GalleryId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Image>().WithMany().HasForeignKey(l => l.ImageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LastGallery>(b =>
            b.HasIndex(g => g.FrameName).HasDatabaseName("IX_LastGallery_FrameName"));

        modelBuilder.Entity<Setting>(b => b.HasKey(s => s.Key));
    }
}
