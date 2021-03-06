using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KKManager.Updater.Data;
using KKManager.Util;

namespace KKManager.Updater.Sources
{
    internal sealed class DeleteFileUpdateItem : IUpdateItem
    {
        public DeleteFileUpdateItem(FileSystemInfo targetPath)
        {
            TargetPath = targetPath;
        }

        public Task Update(Progress<double> progressCallback, CancellationToken cancellationToken)
        {
            TargetPath.Delete();
            return Task.CompletedTask;
        }

        public FileSystemInfo TargetPath { get; }
        public FileSize ItemSize => FileSize.Empty;
        public DateTime? ModifiedTime => null;
    }
}