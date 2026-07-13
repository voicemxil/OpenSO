using FSO.Common.Utils;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace FSO.Client.UI.Panels
{
    public class UIWebDownloaderDialog : UILoginProgress
    {
        private static readonly HttpClient DownloadClient = new HttpClient();
        private DownloadItem[] Items;
        private int CurrentItem;
        private DownloadItem ItemMeta;

        public event Callback<bool> OnComplete;

        public UIWebDownloaderDialog(string title, DownloadItem[] items) : base()
        {
            if (title != null) Caption = title;
            else Caption = GameFacade.Strings.GetString("f101", "9");
            ProgressCaption = "";
            Items = items;

            AdvanceDownloader();
        }

        private void ReportProgress(long bytesReceived, long totalBytes)
        {
            var percentage = (totalBytes > 0) ? (int)(bytesReceived * 100 / totalBytes) : 0;
            GameThread.NextUpdate(x =>
            {
                Progress = (100 * (CurrentItem - 1) + percentage) / Items.Length;
                ProgressCaption = GameFacade.Strings.GetString("f101", "2", new string[] {
                    ItemMeta.Name,
                    (bytesReceived/1000000f).ToString("0.00"),
                    (totalBytes/1000000f).ToString("0.00")+"MB",
                    CurrentItem.ToString(),
                    Items.Length.ToString()
                });
            });
        }

        private void DeleteFiles()
        {
            foreach (var item in Items)
            {
                if (File.Exists(item.DestPath))
                {
                    File.Delete(item.DestPath);
                }
            }
        }

        public void AdvanceDownloader()
        {
            if (CurrentItem >= Items.Length)
            {
                GameThread.NextUpdate(x => OnComplete?.Invoke(true));
                return;
            }
            var item = Items[CurrentItem++];
            ItemMeta = item;
            Directory.CreateDirectory(Path.GetDirectoryName(item.DestPath));
            Task.Run(async () =>
            {
                try
                {
                    using (var response = await DownloadClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? -1;
                        using (var source = await response.Content.ReadAsStreamAsync())
                        using (var dest = File.Create(item.DestPath))
                        {
                            var buffer = new byte[81920];
                            long received = 0;
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                dest.Write(buffer, 0, read);
                                received += read;
                                ReportProgress(received, totalBytes);
                            }
                        }
                    }
                    AdvanceDownloader();
                }
                catch (Exception)
                {
                    DeleteFiles();
                    GameThread.NextUpdate(x => OnComplete?.Invoke(false));
                }
            });
        }
    }

    public class DownloadItem
    {
        public string Url;
        public string DestPath;
        public string Name;
    }
}
