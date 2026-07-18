using FSO.Client.Controllers;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Framework.Parser;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.Client.UI.Controls
{
    public struct UIComboboxItem
    {
        public string Name;
        public object Value;
        public bool Disabled; // renders with the list's disabled (gray) style and can't be selected
    }

    /// <summary>
    /// Kind of hacks the message inbox dropdown into a combobox.
    /// Can be resized horizontally.
    /// </summary>
    public class UICombobox : UIContainer, IFocusableUI
    {
        public bool IsFocused { get; set; }
        public int TabIndex { get; set; }

        public object SelectedItem
        {
            get
            {
                return MenuListBox.SelectedItem?.Data;
            }
            set
            {
                Select(value);
            }
        }

        public int SelectedIndex
        {
            get
            {
                return MenuListBox.SelectedIndex;
            }
            set
            {
                MenuListBox.SelectedIndex = value;
            }
        }

        private int? _width;
        public int? Width
        {
            get
            {
                return _width;
            }
            set
            {
                _width = value;
                UpdateSize();
            }
        }

        public override Vector2 Size
        {
            get => new Vector2(Width ?? 340, 24);
            set => Width = (int)value.X;
        }

        public Texture2D backgroundCollapsedImage { get; set; }
        public Texture2D backgroundExpandedImage { get; set; }

        public UIButton DropDownButton { get; set; }

        public UIButton MenuScrollUpButton { get; set; }
        public UIButton MenuScrollDownButton { get; set; }
        public UISlider MenuSlider { get; set; }

        public UIListBox MenuListBox { get; set; }
        public UITextEdit MenuTextEdit { get; set; }

        public UIImage Background;
        public bool open;

        private List<UIComboboxItem> _items = new List<UIComboboxItem>();
        public List<UIComboboxItem> Items
        {
            get { return _items; }
            set
            {
                _items = value;

                MenuListBox.Items.Clear();
                if (value != null)
                {
                    MenuListBox.Items.AddRange(value.Select(x =>
                    {
                        return new UIListBoxItem(x.Value, new object[] { x.Name }) { Disabled = x.Disabled };
                    }));
                }

                MenuListBox.Items = MenuListBox.Items;
            }
        }

        UIScript Script;

        public event Action<object> OnSelect;

        public UICombobox()
        {
            var ui = Content.Content.Get().CustomUI;

            Script = this.RenderScript("messageinboxmenu.uis");
            backgroundCollapsedImage = ui.Get("archive_combobox.png").Get(GameFacade.GraphicsDevice);
            Background = new UIImage(backgroundCollapsedImage).With9Slice(40, 40, 6, 6);
            this.AddAt(0, Background);

            open = true;
            ToggleOpen();

            DropDownButton.OnButtonClick += new ButtonClickDelegate(DropDownButton_OnButtonClick);
            DropDownButton.Tooltip = null;
            MenuTextEdit.Mode = UITextEditMode.ReadOnly;

            // The combobox owns focus while open (it drives keyboard nav + closes on focus-out). Stop the
            // embedded list from grabbing focus on hover, which otherwise fired the combobox's FocusOut and
            // closed the dropdown the instant the mouse moved over a row.
            MenuListBox.GrabFocusOnHover = false;

            MenuListBox.AttachSlider(MenuSlider);
            MenuSlider.AttachButtons(MenuScrollUpButton, MenuScrollDownButton, 1f);

            MenuListBox.OnChange += SelectComboboxElement;

            MenuListBox.TextStyle = new UIListBoxTextStyle(MenuListBox.FontStyle)
            {
                SelectedColor = Color.Black,
                HighlightedColor = new Color(255, 255, 255),
                DisabledColor = new Color(150, 150, 150)
            };

            UpdateSize();
        }

        private void UpdateSize()
        {
            int width = Width ?? 340;

            if (open)
            {
                Background.SetSize(width, backgroundExpandedImage.Height);
            }
            else
            {
                Background.SetSize(width, backgroundCollapsedImage.Height);
            }

            MenuTextEdit.SetSize(width - 45, MenuTextEdit.Height);
            MenuListBox.SetSize(width - 49, MenuListBox.Height);
            DropDownButton.X = width - 21;
            MenuSlider.X = width - 19;
            MenuScrollUpButton.X = width - 23;
            MenuScrollDownButton.X = width - 23;
        }

        private void SelectComboboxElement(UIElement button)
        {
            UpdateComboText();

            var selected = MenuListBox.SelectedItem;
            if (selected == null) return;

            OnSelect?.Invoke(selected.Data);

            if (open)
            {
                ToggleOpen();
            }
        }


        // Environment.TickCount when a focus-out auto-closed the open list. Clicking the drop button while
        // the list is open first steals focus, so OnFocusChanged has usually ALREADY closed the list by the
        // time the button-click handler runs — and an unconditional ToggleOpen() there re-opened it, making
        // the button appear unable to close the dropdown (you had to pick an option). The timestamp lets the
        // click handler recognize "this same click just closed us" and treat the click as the close.
        private int _FocusClosedTick = int.MinValue;

        void DropDownButton_OnButtonClick(UIElement button)
        {
            // Unsigned delta: guards only when the focus-out happened within the last 100ms. (A signed
            // subtraction against the int.MinValue sentinel OVERFLOWED negative — "< 100" then swallowed
            // every open-click and the dropdowns couldn't open at all.)
            if (!open && (uint)(System.Environment.TickCount - _FocusClosedTick) < 100u)
            {
                // The focus-out from THIS click already closed the list — the user's intent was "close".
                GameFacade.Screens.inputManager.SetFocus(this);
                return;
            }
            ToggleOpen();
            GameFacade.Screens.inputManager.SetFocus(this);
        }

        public void ToggleOpen()
        {
            Invalidate();
            int width = Width ?? 340;

            if (open)
            {
                Background.Texture = backgroundCollapsedImage;
                Background.With9Slice(40, 40, 6, 6);
                Background.SetSize(width, backgroundCollapsedImage.Height);
            }
            else
            {
                Background.Texture = backgroundExpandedImage;
                Background.With9Slice(40, 40, 50, 25);
                Background.SetSize(width, backgroundExpandedImage.Height);
            }

            open = !open;
            MenuSlider.Visible = open;
            MenuScrollUpButton.Visible = open;
            MenuScrollDownButton.Visible = open;
            MenuListBox.Visible = open;
        }

        public void Select(object value)
        {
            // Try find the value in the current items
            if (value == null)
            {
                MenuListBox.SelectedItem = null;
            }
            else
            {
                MenuListBox.SelectedIndex = _items.FindIndex(x => x.Value == value);
            }

            UpdateComboText();
        }

        private void UpdateComboText()
        {
            var selected = MenuListBox.SelectedIndex;

            if (selected == -1)
            {
                MenuTextEdit.CurrentText = "";
            }
            else
            {
                MenuTextEdit.CurrentText = _items[selected].Name;
            }
        }

        public void OnFocusChanged(FocusEvent newFocus)
        {
            if (newFocus == FocusEvent.FocusOut && open)
            {
                ToggleOpen();
                _FocusClosedTick = System.Environment.TickCount; // see DropDownButton_OnButtonClick
            }
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            if (!IsFocused) return;

            if (state.ActivationKeyPressed)
            {
                if (!open) ToggleOpen();
                // never commit a disabled (grayed) item — mouse can't reach them, keys must not either
                else if (MenuListBox.SelectedIndex < 0 || !_items[MenuListBox.SelectedIndex].Disabled)
                    SelectComboboxElement(this);
            }
            if (state.NewKeys.Contains(Keys.Escape) && open)
                ToggleOpen();
            if (state.NewKeys.Contains(Keys.Up) && open && MenuListBox.SelectedIndex > 0)
                MenuListBox.SelectedIndex = StepSelectable(MenuListBox.SelectedIndex, -1);
            if (state.NewKeys.Contains(Keys.Down))
            {
                if (!open) ToggleOpen();
                else if (MenuListBox.SelectedIndex < _items.Count - 1)
                    MenuListBox.SelectedIndex = StepSelectable(MenuListBox.SelectedIndex, 1);
            }
        }

        // Next selectable index in the given direction, skipping Disabled items; stays put if none.
        private int StepSelectable(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < _items.Count; i += dir)
                if (!_items[i].Disabled) return i;
            return from;
        }
    }
}
