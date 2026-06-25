using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Net;

namespace FSO.Patcher
{
    public class CLIPatcher
    {
        private string[] Args;
        private List<string> Path;
        private int PathProgress = 0;
        private ReversiblePatcher CurrentPatcher;
        private bool AllowMonogameMod;
        private bool CleanPatch;
        public CLIPatcher(List<string> extractPath, string[] args)
        {
            Path = extractPath;
            Args = args;
        }

        private void FSONotClosed()
        {
            Console.WriteLine("Could not update OpenSO as write access could not be gained to the game files. Try running update.exe as an administrator.");
            Cleanup();
            Environment.Exit(0);
        }

        private void FileMissing(string path)
        {
            Console.WriteLine($"A file has been removed while advancing through the update chain ({path}). The update must now be aborted.");
            Cleanup();
            Environment.Exit(0);
        }

        private void FileCorrupt(string path)
        {
            Console.WriteLine($"An update archive was corrupt({ path}). The update must now be aborted.");
            Cleanup();
            Environment.Exit(0);
        }

        private void Cleanup()
        {
            try
            {
                if (File.Exists("OpenSO.exe.old"))
                    File.Move("OpenSO.exe.old", "OpenSO.exe");
            }
            catch (Exception)
            {

            }
        }

        private async Task AdvanceExtract()
        {
            if (PathProgress >= Path.Count)
            {
                //done
                StartOpenSO();
            }
            else
            {
                //extract next zip
                var path = Path[PathProgress++];
                Console.WriteLine($"===== Extracting {path} ({PathProgress}/{Path.Count}) =====");
                if (File.Exists(path))
                {
                    ZipArchive archive;
                    try
                    {
                        archive = ZipFile.OpenRead(path);
                    } catch (Exception)
                    {
                        FileCorrupt(path);
                        return;
                    }
                    var patcher = new ReversiblePatcher(archive);
                    if (path.Contains("extra") && AllowMonogameMod)
                    {
                        patcher.IgnoreFiles.RemoveWhere(x => x.Contains("MonoGame"));
                    }
                    CurrentPatcher = patcher;
                    patcher.OnStatus += Patcher_OnStatus;
                    if (PathProgress == 1)
                    {
                        //first patch
                        if (CleanPatch)
                        {
                            foreach (var file in Directory.GetFiles("Content/Patch/"))
                            {
                                //delete any stray patch files. Don't delete user or subfolders (eg. translations) because they might be important
                                try
                                {
                                    File.Delete(file);
                                }
                                catch (Exception)
                                {

                                }
                            }
                        }
                        var worked = await patcher.AttemptRename(8);
                        if (!worked)
                        {
                            PathProgress--;
                            FSONotClosed();
                            return;
                        }
                    }
                    while (patcher.ToExtract.Count > 0)
                    {
                        await patcher.AttemptExtract();
                        var remaining = patcher.GetIncompleteFiles();
                        if (remaining.Count > 0)
                        {
                            //dilemma!
                            var arc = await ShowErrors(remaining);
                            if (arc == 0)
                            {
                                //abort.
                                patcher.Revert();
                                Cleanup();
                                StartOpenSO();
                                return;
                            }
                            else if (arc == 1)
                            {
                                //retry
                            }
                            else if (arc == 2)
                            {
                                //ignore
                                patcher.Final();
                                File.Delete(path);
                                break;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"===== Completed {path} =====");
                            patcher.Final();
                            File.Delete(path);
                            await AdvanceExtract();
                        }
                    }
                }
                else
                {
                    FileMissing(path);
                }
            }
        }

        private async Task<int> ShowErrors(List<string> remaining)
        {
            var dialogResponse = new TaskCompletionSource<int>();
            string fileList;
            if (remaining.Count > 10)
            {
                fileList = string.Join("\r\n", remaining.Take(9));
                fileList += $"\r\n    ...and {remaining.Count - 9} more.";
            }
            else fileList = string.Join("\r\n", remaining);
            Console.WriteLine("Couldn't write one or more files. Make sure you are not running an instance of OpenSO! \r\nFiles:\r\n\r\n" + fileList);
            return 0;
        }


        private void Patcher_OnStatus(string message, float percent)
        {
            Console.WriteLine(message);
        }

        public void StartOpenSO()
        {
            if (!File.Exists("OpenSO.exe")) File.Copy("OpenSO.exe.old", "OpenSO.exe", true);
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                Console.WriteLine($"===== Starting OpenSO... Please wait! =====");
                var args = string.Join(" ", Args);
                if (args.Length > 0) args = " " + args;
                var startArgs = new ProcessStartInfo("mono", "OpenSO.exe" + args);
                startArgs.UseShellExecute = false;
                System.Diagnostics.Process.Start(startArgs);
            }
            else
            {
                System.Diagnostics.Process.Start("OpenSO.exe", string.Join(" ", Args));
            }
            Environment.Exit(0);
        }

        public async Task DownloadAndAdvance()
        {
            Console.WriteLine("Downloading archives:");
            //download the file then set it as our path
            var client = new WebClient();
            Directory.CreateDirectory("PatchFiles/");

            int i = 0;
            foreach (var file in ToDownload) {
                try
                {
                    Console.WriteLine($"Downloading {file}...");
                    await client.DownloadFileTaskAsync(new Uri(file), $"PatchFiles/extra{i}.zip");
                    Path.Add($"PatchFiles/extra{i}.zip");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Could not download {file}: {e.Message}");
                }
                i++;
            }
            await AdvanceExtract();
        }

        public List<string> ToDownload = new List<string>();

        public void Begin()
        {
            Console.WriteLine("===== OpenSO Patcher CLI - 2019 =====");
            Console.WriteLine(Path.Count + " update(s) to apply.");

            if (Args.Contains("--client"))
            {
                Console.WriteLine("OpenSO client requested. Downloading from servo.freeso.org.");
                ToDownload.Add("https://fso-builds.riperiperi.workers.dev/");
            }

            if (Args.Contains("--extras"))
            {
                Console.WriteLine("Unix Extras requested. Downloading from OpenSO.org.");
                ToDownload.Add("http://freeso.org/stuff/macextras.zip");
                AllowMonogameMod = true;
            }

            if (ToDownload.Count > 0)
            {
                CleanPatch = true;
                Task.Run(() => DownloadAndAdvance()).Wait();
            }
            else {
                CleanPatch = File.Exists("PatchFiles/clean.txt");
                if (CleanPatch)
                {
                    try
                    {
                        File.Delete("PatchFiles/clean.txt");
                    }
                    catch
                    {

                    }
                }
                Task.Run(() => AdvanceExtract()).Wait();
            }
        }
    }
}
