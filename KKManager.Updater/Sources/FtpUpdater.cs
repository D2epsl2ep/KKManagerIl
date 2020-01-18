using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using KKManager.Updater.Data;
using KKManager.Util;

namespace KKManager.Updater.Sources
{
    public partial class FtpUpdater : UpdateSourceBase
    {
        private readonly FtpClient _client;

        private FtpListItem[] _allNodes;

        public FtpUpdater(Uri serverUri, NetworkCredential credentials = null) : base(serverUri.Host, 1)
        {
            if (serverUri == null) throw new ArgumentNullException(nameof(serverUri));

            if (credentials == null)
            {
                var info = serverUri.UserInfo.Split(new[] {':'}, 2, StringSplitOptions.None);
                if (info.Length == 2)
                    credentials = new NetworkCredential(info[0], info[1]);
            }

            if (serverUri.IsDefaultPort)
                _client = new FtpClient(serverUri.Host, credentials);
            else
                _client = new FtpClient(serverUri.Host, serverUri.Port, credentials);
        }

        public override void Dispose()
        {
            _client.Dispose();
        }

        public override async Task<List<UpdateTask>> GetUpdateItems(CancellationToken cancellationToken)
        {
            await Connect();
            _allNodes = _client.GetListing("/", FtpListOption.Recursive | FtpListOption.Size);
            return await base.GetUpdateItems(cancellationToken);
        }

        protected override async Task<Stream> DownloadFileAsync(string updateFileName, CancellationToken cancellationToken)
        {
            var str = new MemoryStream();
            try
            {
                if (await _client.DownloadAsync(str, updateFileName, 0, null, cancellationToken))
                    return str;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download file {updateFileName} - {ex}");
            }
            // Cleanup if download fails
            str.Dispose();
            return null;
        }

        protected override IRemoteItem GetRemoteRootItem(string serverPath)
        {
            if (serverPath == null) throw new ArgumentNullException(nameof(serverPath));
            var remote = _allNodes.FirstOrDefault(item => PathTools.PathsEqual(item.FullName, serverPath));
            if (remote == null) return null;
            var remoteItem = new FtpRemoteItem(remote, this, remote.FullName);
            return remoteItem;
        }

        private async Task Connect()
        {
            if (!_client.IsConnected)
            {
                await _client.AutoConnectAsync();
                if (_client.ServerType == FtpServer.VsFTPd)
                    _client.RecursiveList = true;
            }
        }

        private static DateTime GetDate(FtpListItem ftpListItem)
        {
            if (ftpListItem == null) throw new ArgumentNullException(nameof(ftpListItem));
            return ftpListItem.Modified != DateTime.MinValue ? ftpListItem.Modified : ftpListItem.Created;
        }

        private IEnumerable<FtpListItem> GetSubNodes(FtpListItem remoteDir)
        {
            if (remoteDir == null) throw new ArgumentNullException(nameof(remoteDir));
            if (remoteDir.Type != FtpFileSystemObjectType.Directory) throw new ArgumentException("remoteDir has to be a directory");

            var remoteDirName = PathTools.NormalizePath(remoteDir.FullName) + "/";
            var remoteDirDepth = remoteDirName.Count(c => c == '/' || c == '\\');

            return _allNodes.Where(
                item =>
                {
                    if (item == remoteDir) return false;
                    var itemFilename = PathTools.NormalizePath(item.FullName);
                    // Make sure it's inside the directory and not inside one of the subdirectories
                    return itemFilename.StartsWith(remoteDirName, StringComparison.OrdinalIgnoreCase) &&
                           itemFilename.Count(c => c == '/' || c == '\\') == remoteDirDepth;
                });
        }

        private async Task UpdateItem(FtpListItem sourceItem, FileInfo targetPath, IProgress<double> progressCallback, CancellationToken cancellationToken)
        {
            await Connect();

            await _client.DownloadFileAsync(
                targetPath.FullName, sourceItem.FullName,
                FtpLocalExists.Overwrite, FtpVerify.Retry | FtpVerify.Delete | FtpVerify.Throw,
                new Progress<FtpProgress>(progress => progressCallback.Report(progress.Progress)),
                cancellationToken);
        }

        private sealed class FtpRemoteItem : IRemoteItem
        {
            private readonly string _rootFolder;

            public FtpRemoteItem(FtpListItem sourceItem, FtpUpdater source, string rootFolder)
            {
                if (sourceItem == null) throw new ArgumentNullException(nameof(sourceItem));
                if (source == null) throw new ArgumentNullException(nameof(source));

                if (rootFolder != null)
                {
                    _rootFolder = rootFolder;
                    if (!sourceItem.FullName.StartsWith(_rootFolder)) throw new IOException($"Remote item full path {sourceItem.FullName} doesn't start with the specified root path {_rootFolder}");
                    ClientRelativeFileName = sourceItem.FullName.Substring(_rootFolder.Length);
                }

                SourceItem = sourceItem;
                Source = source;
                ItemSize = SourceItem.Size;
                ModifiedTime = GetDate(SourceItem);
            }

            public string Name => SourceItem.Name;
            public long ItemSize { get; }
            public DateTime ModifiedTime { get; }
            public bool IsDirectory => SourceItem.Type == FtpFileSystemObjectType.Directory;
            public bool IsFile => SourceItem.Type == FtpFileSystemObjectType.File;
            public string ClientRelativeFileName { get; }

            public FtpUpdater Source { get; }
            public FtpListItem SourceItem { get; }

            public IRemoteItem[] GetDirectoryContents(CancellationToken cancellationToken)
            {
                return Source.GetSubNodes(SourceItem).Select(x => (IRemoteItem)new FtpRemoteItem(x, Source, _rootFolder)).ToArray();
            }

            public async Task Download(FileInfo downloadTarget, Progress<double> progressCallback, CancellationToken cancellationToken)
            {
                await Source.UpdateItem(SourceItem, downloadTarget, progressCallback, cancellationToken);
            }
        }
    }
}
