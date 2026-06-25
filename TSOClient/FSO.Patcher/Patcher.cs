using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FSO.Patcher
{
    public partial class Patcher : Form
    {
        private int RenameRetry = 0;
        private int RENAME_MAX_ATTEMPTS = 8;
        static HashSet<string> IgnoreFiles = new HashSet<string>()
        {
            //"updater.exe",
            "Content/config.ini",
            "NLog.config"
        };

        private string[] Args;

        public Patcher(string[] args)
        {
            InitializeComponent();
            Args = args;
        }

        private void Patcher_Load(object sender, EventArgs e)
        {
            //attempt to locate the patch files
            StatusLabel.Text = "Starting up...";

            if (!File.Exists("PatchFiles/patch.zip"))
            {
                MessageBox.Show("Could not find OpenSO Patch Files (these must be downloaded by the game!). Starting OpenSO...");
                StartOpenSO();
                return;
            }

            Task.Run(() =>
            {
                AttemptRename();
            });
        }

        public async Task<bool> ExtractEntry(ZipArchiveEntry entry, int tryNum)
        {
            var name = (entry.FullName == "update.exe") ? "update2.exe" : entry.FullName;
            var targPath = Path.Combine("./", name);
            Directory.CreateDirectory(Path.GetDirectoryName(targPath));
            try
            {
                entry.ExtractToFile(targPath, true);
                StatusLabel.Text = name + " Extracted...";
                return true;
            }
            catch (Exception e)
            {
                if (e is DirectoryNotFoundException) return true;
                if (tryNum++ > 3)
                {
                    Console.WriteLine("Could not replace " + targPath + "!");
                    return false;
                }
                else
                {
                    StatusLabel.Text = "Waiting for "+name+"..." + e.ToString();
                    await Task.Delay(3000);
                    return await ExtractEntry(entry, tryNum);
                }
                
            }
        }

        public async void Extract()
        {
            StatusLabel.Text = "Extracting OpenSO Files...";

            var archive = ZipFile.OpenRead("PatchFiles/patch.zip");
            foreach (var file in Directory.GetFiles("Content/Patch/"))
            {
                //delete any stray patch files. Don't delete user or subfolders (eg. translations) because they might be important
                File.Delete(file);
            }
            var entries = archive.Entries;
            foreach (var entry in entries)
            {
                if (IgnoreFiles.Contains(entry.FullName)) continue;
                while (true)
                {
                    var result = await ExtractEntry(entry, 0);
                    if (!result)
                    {
                        var dresult = MessageBox.Show("Couldn't replace a file. Make sure you are not running an instance of OpenSO! If this is discord-rpc.dll, you can safely ignore this.", "Error", MessageBoxButtons.AbortRetryIgnore);
                        if (dresult == DialogResult.Abort)
                        {
                            Cleanup();
                            Application.Exit();
                            return;
                        } else if (dresult == DialogResult.Ignore)
                        {
                            continue;
                        }
                    } else
                    {
                        break;
                    }
                }
            }
            archive.Dispose();
            StartOpenSO();
        }

        public void AttemptRename()
        {
            try
            {
                File.Delete("OpenSO.exe.old");
                if (File.Exists("OpenSO.exe"))  //shouldn't be in use, unless the user has incorrectly renamed and run the freeso executable
                    File.Move("OpenSO.exe", "OpenSO.exe.old");
            }
            catch (Exception)
            {
                if (RenameRetry++ < RENAME_MAX_ATTEMPTS)
                {
                    StatusLabel.Text = "Waiting for OpenSO to Close...";
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        AttemptRename();
                    });
                    return;
                }
                else
                {
                    var result = MessageBox.Show("Could not update OpenSO as write access could not be gained to the game files. Try running update.exe as an administrator.", "Error", MessageBoxButtons.RetryCancel);
                    if (result == DialogResult.Cancel)
                    {
                        Cleanup();
                        Application.Exit();
                    } else
                    {
                        RenameRetry = 0;
                    }
                    return;
                }
            }
            Extract();
        }

        public void Cleanup()
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

        public void StartOpenSO()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                System.Diagnostics.Process.Start("mono", "OpenSO.exe "+string.Join(" ", Args));
            }
            else
            {
                System.Diagnostics.Process.Start("OpenSO.exe", string.Join(" ", Args));
            }
            Application.Exit();
        }

        private void StatusLabel_Click(object sender, EventArgs e)
        {

        }
    }
}
