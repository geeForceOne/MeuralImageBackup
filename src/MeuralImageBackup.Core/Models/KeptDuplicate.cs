namespace MeuralImageBackup.Core.Models;

// An image whose group of identical files the user chose to keep whole ("Keep all"), with the
// fingerprint (SHA-256) of the file it had then. A group stays off the Duplicates list while every
// image in it has a row with the group's fingerprint, so an image that joins later lists the group
// again. Rows go with their image. Migration 005_KeptDuplicates.
public class KeptDuplicate
{
    public int ImageId { get; set; }
    public string Hash { get; set; } = "";
}
