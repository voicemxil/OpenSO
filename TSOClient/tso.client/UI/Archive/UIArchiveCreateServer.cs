using FSO.Client.Controllers;
using FSO.Client.Model.Archive;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Panels;
using FSO.Client.Utils;
using FSO.Common;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.Server.Servers.Lot.Domain;
using FSO.UI.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace FSO.Client.UI.Archive
{
    internal class UIArchiveCreateServer : UIDialog
    {
        private struct ServerFlag
        {
            public ArchiveConfigFlags Value;
            public string Caption;
            public bool DefaultValue;
            public int Indentation;
            public Action HelpAction;
            public UIButton FlagCheck;

            public ServerFlag(ArchiveConfigFlags value, string caption, bool defaultValue, int indentation = 0, Action helpAction = null)
            {
                Value = value;
                Caption = caption;
                DefaultValue = defaultValue;
                Indentation = indentation;
                HelpAction = helpAction;
                FlagCheck = null;
            }
        }

        private ServerFlag[] Flags = new ServerFlag[]
        {
            new ServerFlag(ArchiveConfigFlags.Offline, "Offline mode", false),
            new ServerFlag(ArchiveConfigFlags.UPnP, "Use UPnP", true, 0, UPnPHelp),
            new ServerFlag(ArchiveConfigFlags.HideNames, "Hide display names", false),
            new ServerFlag(ArchiveConfigFlags.Verification, "Require user verification", false, 0, VerificationHelp),
            default, // Gap (flag value is 0)
            new ServerFlag(ArchiveConfigFlags.AllOpenable, "All lots openable", true, 0, AllOpenableHelp),
            new ServerFlag(ArchiveConfigFlags.DebugFeatures, "All-player debug mode", false, 0, DebugModeHelp),
            //new ServerFlag(ArchiveConfigFlags.None, "Skill/money speed scale", false),
            new ServerFlag(ArchiveConfigFlags.AllowLotCreation, "Allow lot creation", true),
            new ServerFlag(ArchiveConfigFlags.AllowSimCreation, "Allow character creation", true),
            new ServerFlag(ArchiveConfigFlags.LockArchivedSims, "Lock archived characters", false, 1, ArchivedCharacterHelp),
        };

        private UIButton ExportButton;
        private UIButton UsersButton;
        private UIButton CustomPortsButton;
        private UIButton StartButton;
        private UITextBox NameInput;
        private ArchiveConfiguration Config;
        private Texture2D HelpButtonTexture = GetTexture(0x0000034200000001);

        private UICombobox SaveCombo;

        public UIArchiveCreateServer() : base(UIDialogStyle.Close, true)
        {
            var clientConfig = ClientArchiveConfiguration.Default;
            Config = clientConfig.ToHostConfig();

            Caption = "Host Server";

            var vbox = new UIVBoxContainer();

            vbox.Add(new UILabel()
            {
                Caption = "Save File:"
            });

            SaveCombo = new UICombobox()
            {
                Width = 200
            };

            vbox.Add(SaveCombo);

            PopulateSaves();
            SelectSaveByName(clientConfig.SelectedArchiveName);

            vbox.Add(new UILabel()
            {
                Caption = "Display name:"
            });

            vbox.Add(NameInput = new UITextBox()
            {
                Size = new Microsoft.Xna.Framework.Vector2(150, 25),
                CurrentText = clientConfig.PlayerName,
            });

            var flagsVbox = new UIVBoxContainer();

            for (int i = 0; i < Flags.Length; i++)
            {
                ref var flag = ref Flags[i];

                if (flag.Value != ArchiveConfigFlags.None)
                {
                    var flagHbox = new UIHBoxContainer();

                    var check = new UIButton(GetTexture(0x0000083600000001));
                    check.Selected = Config.Flags.HasFlag(flag.Value);

                    if (flag.Indentation > 0)
                    {
                        flagHbox.Add(new UISpacer(16, 1));
                    }

                    flag.FlagCheck = check;

                    flagHbox.Add(check);
                    var value = flag.Value;

                    check.OnButtonClick += (elem) =>
                    {
                        ToggleFlag(value);
                    };

                    flagHbox.Add(new UILabel()
                    {
                        Caption = flag.Caption,
                    });

                    if (flag.HelpAction != null)
                    {
                        UIButton helpBtn = new UIButton(HelpButtonTexture);
                        var helpAction = flag.HelpAction;
                        helpBtn.OnButtonClick += (elem) => helpAction();
                        flagHbox.Add(helpBtn);
                    }

                    flagHbox.AutoSize();

                    flagsVbox.Add(flagHbox);
                }
                else
                {
                    flagsVbox.Add(new UISpacer(16));
                }
            }

            flagsVbox.AutoSize();

            vbox.Add(flagsVbox);

            vbox.Add(ExportButton = new UIButton()
            {
                Caption = "Export Config"
            });

            var actionsHbox = new UIHBoxContainer();

            actionsHbox.Add(UsersButton = new UIButton()
            {
                Caption = "Users"
            });

            actionsHbox.Add(CustomPortsButton = new UIButton()
            {
                Caption = "Custom Ports"
            });

            actionsHbox.Add(StartButton = new UIButton()
            {
                Caption = "Start"
            });

            actionsHbox.AutoSize();
            vbox.Add(actionsHbox);

            vbox.AutoSize();
            vbox.Position = new Vector2(20, 45);

            // (hack) Move to end so it draws on top.
            vbox.Remove(SaveCombo);
            vbox.Add(SaveCombo);

            SetSize((int)vbox.Size.X + 40, (int)vbox.Size.Y + 70);
            Add(vbox);

            NameInput.OnChange += ValidateInputs;
            CustomPortsButton.OnButtonClick += ChangePorts;
            StartButton.OnButtonClick += Start;
            CloseButton.OnButtonClick += Close;
            ExportButton.OnButtonClick += Export;

            ValidateInputs(NameInput);

            UpdateButtons();
        }

        private void SelectSaveByName(string name)
        {
            SaveCombo.SelectedIndex = Math.Max(0, SaveCombo.Items.FindIndex((item) => item.Name == name));
        }

        private void ChangePorts(UIElement button)
        {
            UIArchiveServerPorts portDialog = null;
            portDialog = new UIArchiveServerPorts(Config, () =>
            {
                if (portDialog.GetLotPort(out ushort lotPort))
                {
                    Config.LotPort = lotPort;
                }

                if (portDialog.GetCityPort(out ushort cityPort))
                {
                    Config.CityPort = cityPort;
                }
            });

            UIScreen.GlobalShowDialog(portDialog, true);
        }

        private void Export(UIElement button)
        {
            UIScreen.GlobalShowDialog(new UIArchiveConfigExportDialog(), true);
        }

        private void PopulateSaves()
        {
            var manifests = ArchiveSaves.ListManifests();

            SaveCombo.Items = manifests.Select(x => new UIComboboxItem() { Name = x.Name, Value = x }).ToList();

            SaveCombo.SelectedIndex = manifests.Count > 0 ? 0 : -1;
        }

        private ArchiveConfigFlags GetDefaultFlags()
        {
            ArchiveConfigFlags result = ArchiveConfigFlags.None;

            foreach (var flag in Flags)
            {
                if (flag.DefaultValue)
                {
                    result |= flag.Value;
                }
            }

            return result;
        }

        private void UpdateButtons()
        {
            CustomPortsButton.Disabled = Config.Flags.HasFlag(ArchiveConfigFlags.UPnP);
            CustomPortsButton.Tooltip = CustomPortsButton.Disabled ? GameFacade.Strings.GetString("f128", "18") : null;
        }

        private void ToggleFlag(ArchiveConfigFlags flag)
        {
            Config.Flags ^= flag;

            foreach (var item in Flags)
            {
                if (item.FlagCheck != null)
                {
                    item.FlagCheck.Selected = (item.Value & Config.Flags) != 0;
                }
            }

            UpdateButtons();
        }

        private void Close(Framework.UIElement button)
        {
            SaveConfig();
            FindController<ConnectArchiveController>().SwitchMode(ConnectArchiveMode.Landing);
        }

        private void ValidateInputs(Framework.UIElement element)
        {
            StartButton.Disabled = NameInput.CurrentText.Length == 0;
        }

        private void Start(Framework.UIElement button)
        {
            SaveConfig();

            Visible = false;
            var selected = SaveCombo.SelectedItem as ArchiveManifest;

            var factory = new ArchiveServerFactory(Config, FindController<ConnectArchiveController>());

            factory.Start(selected, (bool success) =>
            {
                if (!success)
                {
                    Visible = true;
                }
            });
        }

        private void SaveConfig()
        {
            var clientConfig = ClientArchiveConfiguration.Default;
            var selected = SaveCombo.SelectedItem as ArchiveManifest;

            clientConfig.ApplyHostConfig(Config);
            clientConfig.PlayerName = NameInput.CurrentText;
            clientConfig.SelectedArchiveName = selected?.Name ?? "";
            clientConfig.Save();
        }

        public static void UPnPHelp()
        {
            UIAlert.Alert("UPnP", "UPnP attempts to automatically forward ports on your router to allow public access to your game server. Some routers have this disabled by default or simply don't support it, in which case you'll need to uncheck this option and manually forward the ports.", true);
        }

        public static void AllOpenableHelp()
        {
            UIAlert.Alert("All lots openable", "The original game server only allowed offline lots to be opened by their owner or roommates. When this option is set to true, any player can open any lot, as long as they are allowed to enter.", true);
        }

        public static void DebugModeHelp()
        {
            UIAlert.Alert("All-player debug mode", "The archive server gives admins the ability to spawn debug objects and use debug interactions. When this option is set to true, anyone can use in-lot debug features regardless of permissions.", true);
        }

        public static void ArchivedCharacterHelp()
        {
            UIAlert.Alert("Lock archived characters", "The archive server allows players to enter the game as any character that has been archived, or create their own. When this option is set to true, only admins and mods can enter the game as archived characters - normal users must create their own.", true);
        }

        public static void VerificationHelp()
        {
            UIAlert.Alert("User verification", "Without user verification, any user with the server IP can connect and join the city - authentication is automatic. Users can be banned by client and IP, but new users are always given the benefit of the doubt. \n\nWhen this option is enabled, new users will require verification from an admin or mod before they can interact with the game server. You can verify users from the User List ingame, which appears at the bottom left in the UCP. The button will start flashing if there are any pending verifications.", true);
        }
    }
}
