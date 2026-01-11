using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.UI.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Client.UI.Archive
{
    internal class UIArchiveEventsDialog : UIDialog
    {
        // TODO: manual enablement

        private ArchiveConfiguration Config;
        private EventConfig Events;
        private TextStyle ModifierHeaderStyle;
        private TextStyle GroupHeaderStyle;

        private UIVBoxContainer RootVBox;
        private UIContainer ActiveModifierEditor;
        private UIButton[] ModifierButtons;
        private UIContainer[] ModifierEditors;

        private List<Action> CheckUpdateCallbacks;

        private bool ManualMode = false;
        private bool IsChanged = false;

        public UIArchiveEventsDialog(ArchiveConfiguration config) : base(UIDialogStyle.OK, true)
        {
            Caption = "Events";
            Config = config;
            Events = config.Events ?? default;

            CheckUpdateCallbacks = [];

            ModifierHeaderStyle = TextStyle.DefaultLabel.Clone();
            ModifierHeaderStyle.Shadow = true;
            ModifierHeaderStyle.Color = Color.White;
            ModifierHeaderStyle.Size = 16;

            GroupHeaderStyle = TextStyle.DefaultLabel.Clone();
            GroupHeaderStyle.Shadow = true;
            GroupHeaderStyle.Size = 14;

            var vbox = new UIVBoxContainer();
            RootVBox = vbox;

            var tabHbox = new UIHBoxContainer();

            ModifierButtons = new UIButton[Events.modifiers.Length];
            ModifierEditors = new UIContainer[Events.modifiers.Length];

            for (int i = 0; i < Events.modifiers.Length; i++)
            {
                var modifier = Events.modifiers[i];

                var btn = new UIButton()
                {
                    Caption = modifier.label
                };

                int btnI = i;
                btn.OnButtonClick += (elem) =>
                {
                    SetModifierEditor(btnI);
                };

                ModifierButtons[i] = btn;

                tabHbox.Add(btn);
            }

            vbox.Add(tabHbox);

            for (int i = 0; i < Events.modifiers.Length; i++)
            {
                ModifierEditors[i] = GenerateModifier(i);
            }

            vbox.Position = new Vector2(20, 45);

            if (Events.modifiers.Length > 0)
            {
                SetModifierEditor(0);
            }

            Add(vbox);

            OKButton.OnButtonClick += OKButton_OnButtonClick;
        }

        private void UpdateCheckButtons()
        {
            foreach (var action in CheckUpdateCallbacks)
            {
                action();
            }
        }

        private static Texture2D GetCheckTexture(bool radio)
        {
            return GetTexture(radio ? 0x0000045200000001u : 0x0000083600000001u);
        }

        private void SetModifierEditor(int i)
        {
            var vbox = RootVBox;
            var children = vbox.GetChildren();
            int insertIndex = children.Count;
            if (ActiveModifierEditor != null)
            {
                insertIndex = children.IndexOf(ActiveModifierEditor);
                vbox.Remove(ActiveModifierEditor);
            }

            ActiveModifierEditor = ModifierEditors[i];

            vbox.AddAt(insertIndex, ActiveModifierEditor);

            for (int j = 0; j < ModifierButtons.Length; j++)
            {
                ModifierButtons[j].Selected = j == i;
            }

            vbox.AutoSize();

            SetSize((int)vbox.Size.X + 40, (int)vbox.Size.Y + 70);
        }

        private void OKButton_OnButtonClick(UIElement button)
        {
            if (IsChanged)
            {
                Config.Events = Events;

                Config.SaveEvents();
            }

            UIScreen.RemoveDialog(this);
        }

        private ref bool GetCheckVar(ref EventModifierOption option)
        {
            return ref option.enableTimed;
        }

        private void GenerateOption(UIContainer container, int modifierId, int optionId)
        {
            var option = Events.modifiers[modifierId].options[optionId];

            var hbox = new UIHBoxContainer();

            var check = new UIButton(GetCheckTexture(option.unique != null));

            Action updateMethod = () =>
            {
                var option = Events.modifiers[modifierId].options[optionId];

                check.Selected = GetCheckVar(ref option);
            };

            check.OnButtonClick += (elem) =>
            {
                ref var option = ref Events.modifiers[modifierId].options[optionId];
                ref var isChecked = ref GetCheckVar(ref option);

                if (option.unique != null && !isChecked)
                {
                    // Need to clear all other overlapping uniques before checking this one.

                    if (ManualMode)
                    {
                        for (int i = 0; i < Events.modifiers.Length; i++)
                        {
                            ref var modifier = ref Events.modifiers[i];

                            for (int j = 0; j < modifier.options.Length; j++)
                            {
                                ref var otherOption = ref modifier.options[j];

                                if (otherOption.unique == option.unique)
                                {
                                    GetCheckVar(ref otherOption) = false;
                                }
                            }
                        }
                    }
                    else
                    {
                        // For timed, it's just within the same modifier.
                        ref var modifier = ref Events.modifiers[modifierId];

                        for (int j = 0; j < modifier.options.Length; j++)
                        {
                            ref var otherOption = ref modifier.options[j];

                            if (otherOption.unique == option.unique)
                            {
                                GetCheckVar(ref otherOption) = false;
                            }
                        }
                    }
                }

                isChecked = !isChecked;

                IsChanged = true;

                UpdateCheckButtons();
            };

            CheckUpdateCallbacks.Add(updateMethod);

            updateMethod();

            hbox.Add(check);

            var label = new UILabel()
            {
                Caption = option.label
            };

            hbox.Add(label);

            hbox.AutoSize();
            container.Add(hbox);
        }

        private void GenerateOptionGroup(UIContainer container, string categoryLabel, int modifierId, int[] optionIds)
        {
            var vbox = new UIVBoxContainer();

            var label = new UILabel()
            {
                Caption = categoryLabel,
                CaptionStyle = GroupHeaderStyle,
            };

            vbox.Add(label);

            foreach (int option in optionIds)
            {
                GenerateOption(vbox, modifierId, option);
            }

            vbox.AutoSize();
            container.Add(vbox);
        }

        private UIContainer GenerateModifier(int modifierId)
        {
            var modifier = Events.modifiers[modifierId];
            var vbox = new UIVBoxContainer();

            var optByCategory = modifier.options.Select((x, index) => (index, x)).GroupBy((option) => option.x.category).ToArray();

            for (int i = 0; i < optByCategory.Length; i += 2)
            {
                var hbox = new UIHBoxContainer();

                var groupOne = optByCategory[i];

                GenerateOptionGroup(hbox, groupOne.First().x.category, modifierId, groupOne.Select(x => x.index).ToArray());

                if (i + 1 < optByCategory.Length)
                {
                    hbox.Add(new UISpacer(20));

                    var groupTwo = optByCategory[i + 1];
                    GenerateOptionGroup(hbox, groupTwo.First().x.category, modifierId, groupTwo.Select(x => x.index).ToArray());
                }

                vbox.Add(hbox);
            }

            return vbox;
        }
    }
}
