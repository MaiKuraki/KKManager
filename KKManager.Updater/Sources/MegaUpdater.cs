using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CG.Web.MegaApiClient;
using KKManager.Updater.Data;
using KKManager.Util;

namespace KKManager.Updater.Sources
{
    public class MegaUpdater : IUpdateSource
    {
        private readonly MegaApiClient.AuthInfos _authInfos;

        private readonly MegaApiClient _client;

        private List<INode> _allNodes;
        private MegaApiClient.LogonSessionToken _loginToken;

        public MegaUpdater(Uri serverUri, NetworkCredential credentials)
        {
            if (serverUri.Host.ToLower() != "mega.nz")
                throw new NotSupportedException("The link doesn't point to mega.nz - " + serverUri);

            var client = new MegaApiClient();
            _client = client;
            if (credentials != null)
                _authInfos = _client.GenerateAuthInfos(credentials.UserName, credentials.Password);
            CurrentFolderLink = serverUri;
        }

        private Uri CurrentFolderLink { get; }

        public void Dispose()
        {
            try
            {
                _allNodes = null;
                if (_client != null && _client.IsLoggedIn)
                    _client.Logout();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public async Task<List<UpdateTask>> GetUpdateItems(CancellationToken cancellationToken)
        {
            var nodes = await GetNodesFromLinkAsync(cancellationToken);
            return await CollectTasks(nodes, cancellationToken);
        }

        private async Task DownloadNodeAsync(MegaUpdateItem task, Progress<double> progress, CancellationToken cancellationToken)
        {
            await Connect();

            ((FileInfo)task.TargetPath).Directory?.Create();
            task.TargetPath.Delete();

            try
            {
                await _client.DownloadFileAsync(task.SourceItem, task.TargetPath.FullName, progress, cancellationToken);
            }
            catch (Exception)
            {
                // Needed to avoid partially downloaded files causing issues
                task.TargetPath.Delete();
                throw;
            }
        }
        private async Task<List<UpdateTask>> CollectTasks(List<INode> nodes, CancellationToken cancellationToken)
        {
            var results = new List<UpdateTask>();

            cancellationToken.ThrowIfCancellationRequested();

            var root = nodes.Single(x => x.Type == NodeType.Root);

            var subNodes = GetSubNodes(root).ToList();
            var updateManifest = subNodes.FirstOrDefault(x => x.Type == NodeType.File && x.Name == UpdateInfo.UpdateFileName);
            if (updateManifest == null) throw new FileNotFoundException($"Failed to get the update list - {UpdateInfo.UpdateFileName} is missing in host: mega");

            var result = await _client.DownloadAsync(updateManifest, null, cancellationToken);

            foreach (var updateInfo in UpdateInfo.ParseUpdateManifest(result, CurrentFolderLink.OriginalString, 10))
            {
                _latestModifiedDate = DateTime.MinValue;

                // Find the remote directory
                var updateNode = root;
                var pathParts = updateInfo.ServerPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var pathPart in pathParts)
                {
                    updateNode = GetSubNodes(updateNode).FirstOrDefault(node => node.Type == NodeType.Directory && string.Equals(node.Name, pathPart, StringComparison.OrdinalIgnoreCase));
                    if (updateNode == null)
                        throw new DirectoryNotFoundException($"Could not find ServerPath: {updateInfo.ServerPath} in host: mega");
                }

                var versionEqualsComparer = GetVersionEqualsComparer(updateInfo);

                var updateItems = ProcessDirectory(updateNode, updateInfo.ClientPath,
                    updateInfo.Recursive, updateInfo.RemoveExtraClientFiles, versionEqualsComparer,
                    cancellationToken);

                results.Add(new UpdateTask(updateInfo.Name ?? updateNode.Name, updateItems, updateInfo, _latestModifiedDate));
            }

            return results;
        }

        private static Func<INode, FileInfo, bool> GetVersionEqualsComparer(UpdateInfo updateInfo)
        {
            switch (updateInfo.Versioning)
            {
                case UpdateInfo.VersioningMode.Size:
                    return (item, info) => item.Size == info.Length;
                case UpdateInfo.VersioningMode.Date:
                    return (item, info) => (item.ModificationDate ?? item.CreationDate) <= info.LastWriteTimeUtc;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private async Task Connect()
        {
            if (_client.IsLoggedIn) return;

            await RetryHelper.RetryOnExceptionAsync(
                async () =>
                {
                    if (_client.IsLoggedIn) return;
                    if (_loginToken != null)
                        await _client.LoginAsync(_loginToken);
                    else if (_authInfos != null)
                        _loginToken = await _client.LoginAsync(_authInfos);
                    else
                        await _client.LoginAnonymousAsync();
                }, 2, TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        private async Task<List<INode>> GetNodesFromLinkAsync(CancellationToken cancellationToken)
        {
            await Connect();
            await RetryHelper.RetryOnExceptionAsync(async () => _allNodes = (await _client.GetNodesFromLinkAsync(CurrentFolderLink)).ToList(), 2, TimeSpan.FromSeconds(1), cancellationToken);
            return _allNodes;
        }

        private IEnumerable<INode> GetSubNodes(INode rootNode)
        {
            return _allNodes.Where(x => x.ParentId == rootNode.Id);
        }

        private DateTime _latestModifiedDate = DateTime.MinValue;

        private List<IUpdateItem> ProcessDirectory(INode remoteDir, DirectoryInfo localDir,
            bool recursive, bool removeExtraClientFiles, Func<INode, FileInfo, bool> versionEqualsComparer,
            CancellationToken cancellationToken)
        {
            var results = new List<IUpdateItem>();

            var localContents = new List<FileSystemInfo>();
            if (localDir.Exists)
                localContents.AddRange(localDir.GetFileSystemInfos("*", SearchOption.TopDirectoryOnly));

            foreach (var remoteItem in GetSubNodes(remoteDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (remoteItem.Type)
                {
                    case NodeType.File:
                        {
                            var localFile = localContents.OfType<FileInfo>().FirstOrDefault(x => string.Equals(x.Name, remoteItem.Name, StringComparison.OrdinalIgnoreCase));
                            if (localFile == null)
                                localFile = new FileInfo(Path.Combine(localDir.FullName, remoteItem.Name));
                            else
                                localContents.Remove(localFile);

                            var localIsUpToDate = localFile.Exists && versionEqualsComparer(remoteItem, localFile);

                            if (!localIsUpToDate)
                                results.Add(new MegaUpdateItem(remoteItem, this, localFile));

                            if (_latestModifiedDate < (remoteItem.ModificationDate ?? remoteItem.CreationDate))
                                _latestModifiedDate = remoteItem.ModificationDate ?? remoteItem.CreationDate;
                        }
                        break;

                    case NodeType.Directory:
                        if (recursive)
                        {
                            var localItem = localContents.OfType<DirectoryInfo>().FirstOrDefault(x => string.Equals(x.Name, remoteItem.Name, StringComparison.OrdinalIgnoreCase));
                            if (localItem == null)
                                localItem = new DirectoryInfo(Path.Combine(localDir.FullName, remoteItem.Name));
                            else
                                localContents.Remove(localItem);

                            results.AddRange(ProcessDirectory(remoteItem, localItem, recursive, removeExtraClientFiles, versionEqualsComparer, cancellationToken));
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            // Remove all files that were not on the remote
            if (removeExtraClientFiles)
                results.AddRange(UpdateSourceManager.FileInfosToDeleteItems(localContents));

            return results;
        }

        public sealed class MegaUpdateItem : IUpdateItem
        {
            private readonly MegaUpdater _source;

            public MegaUpdateItem(INode item, MegaUpdater source, FileSystemInfo targetPath)
            {
                TargetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
                SourceItem = item ?? throw new ArgumentNullException(nameof(item));
                _source = source ?? throw new ArgumentNullException(nameof(source));
                ItemSize = FileSize.FromBytes(item.Size);
                ModifiedTime = item.ModificationDate ?? item.CreationDate;
            }

            public INode SourceItem { get; }
            public FileSize ItemSize { get; }
            public DateTime? ModifiedTime { get; }
            public FileSystemInfo TargetPath { get; }

            public async Task Update(Progress<double> progressCallback, CancellationToken cancellationToken)
            {
                await _source.DownloadNodeAsync(this, progressCallback, cancellationToken);
            }
        }
    }
}
