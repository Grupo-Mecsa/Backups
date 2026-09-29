using Backup.Application.Pipeline;
using Backup.Domain.Jobs;

namespace Backup.Infrastructure.Transforms;

public sealed class EncryptionTransform : IStreamTransform
{
    public int Order => 20;

    public bool AppliesTo(BackupJob job) => job.IsEncrypted;

    public string GetExtension(BackupJob job) => BackupEncryption.FileExtension;

    public Stream WrapWrite(Stream output, BackupJob job) =>
        BackupEncryption.CreateEncryptingStream(output, job.EncryptionPassphrase!);
}
