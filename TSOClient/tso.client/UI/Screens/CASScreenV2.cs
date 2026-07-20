using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using FSO.Common.Utils;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;

namespace FSO.Client.UI.Screens
{
    /// <summary>
    /// Modern Create-A-Sim, behind the GlobalSettings.ModernCAS flag: perspective sim in the region
    /// left of a right-anchored translucent panel with Head/Body/Bio tabs. The legacy sprite UI is hidden; only the inherited
    /// data controls are reused (head/body collection viewers, name/description edits read by
    /// PersonSelectionEditController), so the avatar-create save path / server PDU is untouched.
    /// </summary>
    public class CASScreenV2 : PersonSelectionEdit
    {
        // Panel size; position is computed per window size (anchored to the right edge, vertically
        // centred) in LayoutGeometry, since the 1024x768 design space is centred in the real window.
        private const float PW = 545, PH = 662, PMargin = 24;
        private float _PX, _PY;
        private UIImage _Backdrop;
        private UIImage _Panel;
        private const int IconSize = 42;                 // gender/skin glyph button size
        private const int HeadCellW = 72, HeadCellH = 72;  // head thumbs are native ~square (33x33)
        private const int BodyCellW = 72, BodyCellH = 139; // body thumbs are native tall (33x70)
        private const int CellOff = 6, CellR = 12;         // image inset + corner radius
        // Both grids share cell width + margins, and the browser width exactly fits GridCols columns,
        // so the grids centre in the panel and everything else aligns to their first-cell left edge.
        private const int GridMargin = 8, GridCols = 6;
        private const float GridFitW = GridCols * (HeadCellW + GridMargin) + GridMargin;
        private const int ArrowSize = 36;                  // page prev/next chevron buttons
        private static readonly Color Accent = new Color(46, 196, 150); // teal (dark-studio accent)
        private static readonly Vector4 CASAmbient = new Vector4(1.15f, 1.15f, 1.15f, 1f);

        private int _Tab;
        private readonly List<UIImage> _TabBg = new List<UIImage>();
        private readonly List<UILabel> _TabLabel = new List<UILabel>();
        private Texture2D _TabActive, _TabInactive;
        private TextStyle _LightText, _DarkText;
        private UIButton _PgLeft, _PgRight;
        private UIImage _NameBox, _BioBox;
        private UILabel _NameLabel, _BioLabel, _NameReqLabel;

        public CASScreenV2() : base()
        {
            var gw = GlobalSettings.Default.GraphicsWidth;
            var gh = GlobalSettings.Default.GraphicsHeight;
            var gd = GameFacade.GraphicsDevice;

            // perspective sim viewport, sized in LayoutGeometry to the region left of the panel with
            // the avatar centred in it (PanFraction 0 = no off-centre truck)
            SimBox.AutoRotate = true;
            SimBox.Interactive = true;
            SimBox.PanFraction = 0f;
            this.AddAt(1, SimBox);
            if (DialogBackground != null) this.Remove(DialogBackground);

            m_HeadSkinBrowser.OnChange += (e) => SimBox.FocusHead();
            m_BodySkinBrowser.OnChange += (e) => SimBox.FocusBody();

            // hide all legacy controls
            foreach (var child in new List<UIElement>(Children))
            {
                if (child == Background || child == SimBox) continue;
                child.Visible = false;
            }

            if (Background != null) Background.Visible = false;
            _Backdrop = new UIImage(BrandBackdrop(gd, 512, 320));
            this.AddAt(1, _Backdrop); // behind the sim (sim shifts to index 2)

            _Panel = new UIImage(RoundedRect(gd, (int)PW, (int)PH, 18, new Color(19, 23, 30, 222),
                new Color(104, 118, 140, 120), 2));
            this.AddAt(3, _Panel);

            // slight lift over neutral: the flat techniques have no key light, so a plain 1.0
            // ambient reads dim against the dark studio backdrop. LayoutGeometry re-applies this —
            // keep both sites on CASAmbient or the first layout pass silently resets it.
            if (SimBox.Avatar != null) SimBox.Avatar.AmbientLight = CASAmbient;

            _TabActive = RoundedRect(gd, 150, 40, 11, Accent);
            _TabInactive = RoundedRect(gd, 150, 40, 11, new Color(38, 44, 56, 210));
            _LightText = TextStyle.Create(new Color(216, 224, 234), 13);
            _DarkText = TextStyle.Create(new Color(6, 22, 17), 13);
            string[] tabs = { "Head", "Body", "Bio" };
            for (int i = 0; i < tabs.Length; i++)
            {
                var bg = new UIImage(_TabInactive);
                this.Add(bg);
                _TabBg.Add(bg);
                var lbl = new UILabel { Caption = tabs[i], CaptionStyle = _LightText };
                this.Add(lbl);
                _TabLabel.Add(lbl);
                int idx = i;
                bg.ListenForMouse((type, state) => { if (type == UIMouseEventType.MouseUp) SetTab(idx); });
            }

            ReskinButton(FemaleButton,     GenderIcon(gd, IconSize, IconSize, false));
            ReskinButton(MaleButton,       GenderIcon(gd, IconSize, IconSize, true));
            ReskinButton(SkinLightButton,  SkinIcon(gd, IconSize, IconSize, new Color(242, 211, 181)));
            ReskinButton(SkinMediumButton, SkinIcon(gd, IconSize, IconSize, new Color(198, 138, 91)));
            ReskinButton(SkinDarkButton,   SkinIcon(gd, IconSize, IconSize, new Color(120, 78, 52)));

            // Head and body thumbs have different native aspects (~1:1 vs ~33:70), so each grid gets its
            // own cell shape; the image area (ThumbSize - 2*offset) must match to avoid stretching.
            m_HeadSkinBrowser.Size            = new Vector2(GridFitW, PH - 230);
            m_HeadSkinBrowser.ThumbSize       = new Vector2(HeadCellW, HeadCellH);
            m_HeadSkinBrowser.ThumbMargins    = new Vector2(GridMargin, GridMargin);
            m_HeadSkinBrowser.ThumbImageOffsets = new Vector2(CellOff, CellOff);
            m_HeadSkinBrowser.ThumbButtonImage = GlassStrip(gd, HeadCellW, HeadCellH, CellR);
            m_HeadSkinBrowser.Relayout();
            m_BodySkinBrowser.Size            = new Vector2(GridFitW, PH - 230);
            m_BodySkinBrowser.ThumbSize       = new Vector2(BodyCellW, BodyCellH);
            m_BodySkinBrowser.ThumbMargins    = new Vector2(GridMargin, GridMargin);
            m_BodySkinBrowser.ThumbImageOffsets = new Vector2(CellOff, CellOff);
            m_BodySkinBrowser.ThumbButtonImage = GlassStrip(gd, BodyCellW, BodyCellH, CellR);
            m_BodySkinBrowser.Relayout();

            // page chevrons flanking the numeric pagination bar; they act on the visible browser and
            // their enabled state tracks it in Update
            var leftTex = ArrowIcon(gd, ArrowSize, ArrowSize, true);
            var rightTex = ArrowIcon(gd, ArrowSize, ArrowSize, false);
            _PgLeft = new UIButton(leftTex);
            ReskinButton(_PgLeft, leftTex, ArrowSize);
            _PgLeft.OnButtonClick += b => ActiveBrowser.SelectedPage--;
            this.Add(_PgLeft);
            _PgRight = new UIButton(rightTex);
            ReskinButton(_PgRight, rightTex, ArrowSize);
            _PgRight.OnButtonClick += b => ActiveBrowser.SelectedPage++;
            this.Add(_PgRight);

            // Bio tab: field labels + darkened bordered boxes behind the uis-scripted text edits
            // (textures baked at the edits' size here; positioned in LayoutGeometry), and the edit
            // text restyled light — the TSO default yellow is unreadable on the dark panel
            var editText = TextStyle.Create(new Color(224, 230, 238), 12);
            var boxFill = new Color(11, 14, 20, 235);
            var boxBorder = new Color(74, 86, 104, 150);
            if (NameTextEdit != null)
            {
                NameTextEdit.TextStyle = editText;
                _NameBox = new UIImage(RoundedRect(gd, (int)NameTextEdit.Size.X + 20,
                    (int)NameTextEdit.Size.Y + 14, 10, boxFill, boxBorder, 2));
                this.AddAt(4, _NameBox); // above the panel, below the legacy text controls
                _NameLabel = new UILabel { Caption = "Name:", CaptionStyle = _LightText };
                this.Add(_NameLabel);
                // the accept button silently disables on an invalid name, so spell the rules out
                _NameReqLabel = new UILabel
                {
                    Caption = "Name requirements:\n* Letters and spaces only\n* Starts with a letter\n* 3-24 characters",
                    CaptionStyle = TextStyle.Create(new Color(140, 152, 170), 10),
                    Wrapped = true,
                    Alignment = TextAlignment.Left | TextAlignment.Top,
                    Size = new Vector2(360, 64)
                };
                this.Add(_NameReqLabel);
            }
            if (DescriptionTextEdit != null)
            {
                DescriptionTextEdit.TextStyle = editText;
                int colBottom = 260 + (int)(DescriptionScrollDownButton?.Size.Y ?? 24); // scroll column extent
                int boxH = (int)System.Math.Max(DescriptionTextEdit.Size.Y, colBottom) + 14;
                _BioBox = new UIImage(RoundedRect(gd, (int)DescriptionTextEdit.Size.X + 58, boxH, 10,
                    boxFill, boxBorder, 2));
                this.AddAt(4, _BioBox);
                _BioLabel = new UILabel { Caption = "Bio:", CaptionStyle = _LightText };
                this.Add(_BioLabel);
            }

            LayoutGeometry();
            SetTab(0);
        }

        /// <summary>
        /// Size/position everything that depends on the real window size. The screen root is the
        /// centred 1024x768 design space, so the window's top-left corner in local coordinates is
        /// ((1024-gw)/2, (768-gh)/2). Runs at construction and again on every GameResized.
        /// </summary>
        private void LayoutGeometry()
        {
            var gw = GlobalSettings.Default.GraphicsWidth;
            var gh = GlobalSettings.Default.GraphicsHeight;
            var winTopLeft = new Vector2((1024 - gw) / 2f, (768 - gh) / 2f);

            _Backdrop.SetSize(gw, gh);
            _Backdrop.Position = winTopLeft;

            // panel hugs the right edge of the window, vertically centred
            _PX = winTopLeft.X + gw - PW - PMargin;
            _PY = winTopLeft.Y + System.Math.Max(12f, (gh - PH) / 2f);
            _Panel.X = _PX; _Panel.Y = _PY;

            // sim viewport = the region between the left screen edge and the panel's left edge, full
            // height; the avatar centres within it (PanFraction 0). SetPerspective enters the mode
            // once; later resizes go through SetPerspectiveSize so orbit/focus state survives.
            int regionW = System.Math.Max(64, (int)(_PX - winTopLeft.X));
            SimBox.Position = winTopLeft;
            SimBox.Size = new Vector2(regionW, gh);
            if (!SimBox.Perspective)
            {
                SimBox.SetPerspective(true, regionW, gh);
                if (SimBox.Avatar != null) SimBox.Avatar.AmbientLight = CASAmbient;
            }
            else SimBox.SetPerspectiveSize(regionW, gh);

            // one shared left edge for all panel content: the grids centre in the panel, and the tab
            // row, buttons and bio fields align to the grids' first-cell left edge
            float gridX = _PX + (PW - GridFitW) / 2f;
            float contentX = gridX + GridMargin;
            float contentW = GridFitW - 2 * GridMargin;

            float tabY = _PY + 16;
            for (int i = 0; i < _TabBg.Count; i++)
            {
                float tx = contentX + i * 162;
                _TabBg[i].Position = new Vector2(tx, tabY);
                _TabLabel[i].Position = new Vector2(tx + 56, tabY + 11);
            }

            Place(FemaleButton,        contentX,       _PY + 78);
            Place(MaleButton,          contentX + 48,  _PY + 78);
            Place(SkinLightButton,     contentX + 134, _PY + 78);
            Place(SkinMediumButton,    contentX + 182, _PY + 78);
            Place(SkinDarkButton,      contentX + 230, _PY + 78);

            Place(m_HeadSkinBrowser,   gridX, _PY + 150);
            Place(m_BodySkinBrowser,   gridX, _PY + 150);

            // page chevrons vertically centred on the pagination strip (bottom 45px of the browser)
            float pgY = _PY + 150 + (PH - 230) - 45 + (45 - ArrowSize) / 2f;
            Place(_PgLeft,  contentX, pgY);
            Place(_PgRight, contentX + contentW - ArrowSize, pgY);

            // Bio tab: labelled fields over darkened boxes, on the same content edge
            float nameY = _PY + 116;
            Place(_NameLabel,   contentX + 4, nameY - 32);
            Place(_NameBox,     contentX,     nameY - 7);
            Place(NameTextEdit, contentX + 10, nameY);

            float descY = _PY + 196;
            float descW = (DescriptionTextEdit != null) ? DescriptionTextEdit.Size.X : 0;
            float sliderX = contentX + 10 + descW + 14;
            Place(_BioLabel,           contentX + 4, descY - 32);
            Place(_BioBox,             contentX,     descY - 7);
            Place(DescriptionTextEdit, contentX + 10, descY);
            Place(DescriptionSlider,   sliderX,     descY);
            Place(DescriptionScrollUpButton,   sliderX - 2, descY - 6);
            Place(DescriptionScrollDownButton, sliderX - 2, descY + 260);
            // in the open space between the bio box (ends ~descY+291) and the buttons (_PY+600)
            Place(_NameReqLabel, contentX + 4, descY + 310);

            Place(AcceptButton,        _PX + 330, _PY + 600);
            Place(CancelButton,        _PX + 170, _PY + 600);
        }

        public override void GameResized()
        {
            base.GameResized(); // recentres the design space + legacy background
            LayoutGeometry();
            SetTab(_Tab); // Place() marks everything visible; restore tab visibility
        }

        private void Place(UIElement e, float x, float y)
        {
            if (e == null) return;
            e.Visible = true;
            e.Position = new Vector2(x, y);
        }

        // Swap a UIButton onto a generated 4-state glass icon. Frame width == w so the 9-slice is a
        // no-op; ImageStates re-syncs the click region.
        private void ReskinButton(UIButton btn, Texture2D tex, int w = IconSize)
        {
            if (btn == null) return;
            btn.Texture = tex;
            btn.ImageStates = 4;
            btn.Width = w;
        }

        private void SetTab(int tab)
        {
            _Tab = tab;
            for (int i = 0; i < _TabBg.Count; i++)
            {
                _TabBg[i].Texture = (i == tab) ? _TabActive : _TabInactive;
                _TabLabel[i].CaptionStyle = (i == tab) ? _DarkText : _LightText;
            }

            bool head = tab == 0, body = tab == 1, bio = tab == 2;
            if (m_HeadSkinBrowser != null) m_HeadSkinBrowser.Visible = head;
            if (m_BodySkinBrowser != null) m_BodySkinBrowser.Visible = body;
            foreach (var b in new[] { FemaleButton, MaleButton, SkinLightButton, SkinMediumButton, SkinDarkButton,
                                      _PgLeft, _PgRight })
                if (b != null) b.Visible = !bio;
            foreach (UIElement b in new UIElement[] { NameTextEdit, DescriptionTextEdit, DescriptionSlider,
                                                      DescriptionScrollUpButton, DescriptionScrollDownButton,
                                                      _NameLabel, _NameBox, _BioLabel, _BioBox })
                if (b != null) b.Visible = bio;
            if (_NameReqLabel != null) _NameReqLabel.Visible = bio;

            if (head) SimBox.FocusHead();
            else if (body) SimBox.FocusBody();
        }

        private UICollectionViewer ActiveBrowser => (_Tab == 1) ? m_BodySkinBrowser : m_HeadSkinBrowser;

        // Keep the page chevrons' enabled state in sync with the visible browser. Page and data
        // changes funnel through too many paths to hook individually; the Disabled setter no-ops
        // on unchanged values, so polling here is free.
        public override void Update(UpdateState state)
        {
            base.Update(state);
            var b = ActiveBrowser;
            int pages = (b?.DataProvider == null) ? 0 : b.NumPages;
            if (_PgLeft != null) _PgLeft.Disabled = b == null || b.SelectedPage <= 0;
            if (_PgRight != null) _PgRight.Disabled = b == null || b.SelectedPage >= pages - 1;
        }

        // Solid rounded-rectangle texture (transparent outside the radius, soft-AA edge), with an
        // optional border ring of borderW just inside the boundary. Colors carry their own alpha.
        private static Texture2D RoundedRect(GraphicsDevice gd, int w, int h, int r, Color color,
                                             Color? border = null, int borderW = 0)
        {
            var tex = new Texture2D(gd, w, h);
            var data = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int cx = (x < r) ? r : (x >= w - r ? w - r - 1 : x);
                    int cy = (y < r) ? r : (y >= h - r ? h - r - 1 : y);
                    float dx = x - cx, dy = y - cy;
                    float edge = r - (float)System.Math.Sqrt(dx * dx + dy * dy); // distance inside the boundary
                    float cov = MathHelper.Clamp(edge + 0.5f, 0f, 1f);
                    if (cov <= 0f) { data[y * w + x] = Color.Transparent; continue; }
                    var col = (border != null && edge <= borderW) ? border.Value : color;
                    data[y * w + x] = new Color(col.R, col.G, col.B, (int)(col.A * cov));
                }
            tex.SetData(data);
            return tex;
        }

        // Brand backdrop: the website hero gradient (navy-0 → navy-1 → navy-2 on a ~140° diagonal)
        // with a soft teal accent glow behind the avatar. Ordered-dithered ~1 LSB so the shallow
        // navy gradient reconstructs band-free when the low-res texture is bilinearly stretched to
        // fill the window (the interpolation between dithered texels effectively lifts bit depth).
        private static readonly Vector3 Navy0 = new Vector3(0x16, 0x31, 0x4F) / 255f;
        private static readonly Vector3 Navy1 = new Vector3(0x0E, 0x21, 0x38) / 255f;
        private static readonly Vector3 Navy2 = new Vector3(0x0A, 0x16, 0x26) / 255f;
        private static readonly Vector3 TealGlow = new Vector3(0x3F, 0xD9, 0xD0) / 255f;
        private static readonly int[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        private static Texture2D BrandBackdrop(GraphicsDevice gd, int w, int h)
        {
            var data = new Color[w * h];
            var dir = Vector2.Normalize(new Vector2(0.66f, 0.75f)); // top-left → bottom-right (~140°)
            float dirNorm = Vector2.Dot(Vector2.One, dir);
            var glowC = new Vector2(0.30f, 0.34f); // teal glow centre in uv (behind the avatar)
            float glowR = 0.62f;
            float aspect = w / (float)h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var uv = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                    // navy diagonal gradient: two-stop lerp (navy-1 at 55%), projected onto dir
                    float t = MathHelper.Clamp(Vector2.Dot(uv, dir) / dirNorm, 0f, 1f);
                    Vector3 col = (t < 0.55f)
                        ? Vector3.Lerp(Navy0, Navy1, t / 0.55f)
                        : Vector3.Lerp(Navy1, Navy2, (t - 0.55f) / 0.45f);
                    // soft teal glow (aspect-corrected so it stays roughly circular)
                    float gdist = new Vector2((uv.X - glowC.X) * aspect, uv.Y - glowC.Y).Length() / glowR;
                    float glow = MathHelper.Clamp(1f - gdist, 0f, 1f);
                    glow = glow * glow * (3f - 2f * glow); // smoothstep
                    col += TealGlow * (glow * 0.16f);
                    // ordered dither ~1 LSB
                    float dth = (Bayer4[(y & 3) * 4 + (x & 3)] / 16f - 0.46875f) * (2.2f / 255f);
                    col += new Vector3(dth);
                    data[y * w + x] = new Color(col);
                }
            var tex = new Texture2D(gd, w, h);
            tex.SetData(data);
            return tex;
        }

        // ---- Procedural glassy controls (frosted rounded buttons + glyph icons) ----

        // Non-premultiplied src-over of one pixel.
        private static void Blend(Color[] data, int idx, Color src)
        {
            if (src.A == 0) return;
            if (src.A == 255) { data[idx] = src; return; }
            var dst = data[idx];
            float sa = src.A / 255f, da = dst.A / 255f;
            float oa = sa + da * (1 - sa);
            if (oa <= 0.0001f) { data[idx] = Color.Transparent; return; }
            float r = (src.R * sa + dst.R * da * (1 - sa)) / oa;
            float g = (src.G * sa + dst.G * da * (1 - sa)) / oa;
            float b = (src.B * sa + dst.B * da * (1 - sa)) / oa;
            data[idx] = new Color((int)r, (int)g, (int)b, (int)(oa * 255));
        }

        // Fill a rounded rect at ox,oy with a frosted fill, a top sheen and a coloured border.
        private static void FillGlass(Color[] data, int stride, int ox, int oy, int w, int h, int r,
                                      Color fill, Color border, int borderW)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int cx = (x < r) ? r : (x >= w - r ? w - r - 1 : x);
                    int cy = (y < r) ? r : (y >= h - r ? h - r - 1 : y);
                    float dx = x - cx, dy = y - cy;
                    float dist = (float)System.Math.Sqrt(dx * dx + dy * dy);
                    float cov = MathHelper.Clamp(r - dist + 0.5f, 0f, 1f); // soft outer edge
                    if (cov <= 0f) continue;

                    float sheen = MathHelper.Clamp(1f - (float)y / h, 0f, 1f);
                    var f = fill;
                    f = new Color(
                        (int)MathHelper.Clamp(f.R + sheen * 26, 0, 255),
                        (int)MathHelper.Clamp(f.G + sheen * 26, 0, 255),
                        (int)MathHelper.Clamp(f.B + sheen * 30, 0, 255),
                        f.A);

                    float edge = r - dist; // distance inside the rounded edge
                    var col = (edge <= borderW) ? border : f;
                    col = new Color(col.R, col.G, col.B, (int)(col.A * cov));
                    Blend(data, (oy + y) * stride + (ox + x), col);
                }
        }

        private static void Disc(Color[] data, int stride, float cx, float cy, float rad, Color color)
        {
            int x0 = (int)(cx - rad - 1), x1 = (int)(cx + rad + 1), y0 = (int)(cy - rad - 1), y1 = (int)(cy + rad + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = (float)System.Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float cov = MathHelper.Clamp(rad - d + 0.5f, 0f, 1f);
                    if (cov <= 0f) continue;
                    Blend(data, y * stride + x, new Color(color.R, color.G, color.B, (int)(color.A * cov)));
                }
        }

        private static void Ring(Color[] data, int stride, float cx, float cy, float rOut, float rIn, Color color)
        {
            int x0 = (int)(cx - rOut - 1), x1 = (int)(cx + rOut + 1), y0 = (int)(cy - rOut - 1), y1 = (int)(cy + rOut + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = (float)System.Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float cov = MathHelper.Clamp(System.Math.Min(rOut - d, d - rIn) + 0.5f, 0f, 1f);
                    if (cov <= 0f) continue;
                    Blend(data, y * stride + x, new Color(color.R, color.G, color.B, (int)(color.A * cov)));
                }
        }

        // Thick line segment (rounded caps) of half-width `half`.
        private static void Seg(Color[] data, int stride, float x0, float y0, float x1, float y1, float half, Color color)
        {
            int minX = (int)(System.Math.Min(x0, x1) - half - 1), maxX = (int)(System.Math.Max(x0, x1) + half + 1);
            int minY = (int)(System.Math.Min(y0, y1) - half - 1), maxY = (int)(System.Math.Max(y0, y1) + half + 1);
            float vx = x1 - x0, vy = y1 - y0; float len2 = vx * vx + vy * vy;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float t = len2 <= 0.0001f ? 0f : MathHelper.Clamp(((x - x0) * vx + (y - y0) * vy) / len2, 0f, 1f);
                    float px = x0 + t * vx, py = y0 + t * vy;
                    float d = (float)System.Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                    float cov = MathHelper.Clamp(half - d + 0.5f, 0f, 1f);
                    if (cov <= 0f) continue;
                    Blend(data, y * stride + x, new Color(color.R, color.G, color.B, (int)(color.A * cov)));
                }
        }

        // 4-state frosted-glass button strip (normal / selected+hover / down / disabled). The optional
        // `glyph` callback stamps an icon in each frame with the per-state accent colour.
        private static Texture2D GlassStrip(GraphicsDevice gd, int w, int h, int r,
            System.Action<Color[], int, int, int, Color> glyph = null)
        {
            var fills = new[]
            {
                new Color(26, 32, 42, 180), // normal
                new Color(28, 46, 54, 214), // selected / hover
                new Color(26, 56, 54, 220), // down
                new Color(22, 26, 34, 110), // disabled
            };
            var borders = new[]
            {
                new Color(74, 86, 104, 150),
                Accent,                        // teal highlight when selected
                Accent,
                new Color(52, 60, 72, 90),
            };
            var accents = new[]
            {
                new Color(206, 216, 228, 235),
                new Color(140, 236, 210, 255), // fills stay dark (shared with grid cells), so the
                new Color(110, 210, 186, 255), // selected/down glyph goes bright mint, not dark
                new Color(120, 130, 144, 160),
            };
            var tex = new Texture2D(gd, w * 4, h);
            var data = new Color[w * 4 * h];
            for (int s = 0; s < 4; s++)
            {
                FillGlass(data, w * 4, s * w, 0, w, h, r, fills[s], borders[s], 2);
                glyph?.Invoke(data, w * 4, s * w, 0, accents[s]);
            }
            tex.SetData(data);
            return tex;
        }

        // Gender glyph (Venus / Mars) centred in a frame.
        private Texture2D GenderIcon(GraphicsDevice gd, int w, int h, bool male)
        {
            return GlassStrip(gd, w, h, 10, (data, stride, ox, oy, accent) =>
            {
                float cx = ox + w * 0.46f, cy = oy + h * 0.46f, rr = w * 0.17f;
                Ring(data, stride, cx, cy, rr + 1.6f, rr - 1.6f, accent);
                if (male)
                {
                    // arrow to the upper-right
                    float ax = cx + rr * 0.72f, ay = cy - rr * 0.72f;
                    float ex = ax + w * 0.18f, ey = ay - h * 0.18f;
                    Seg(data, stride, ax, ay, ex, ey, 1.6f, accent);
                    Seg(data, stride, ex, ey, ex - w * 0.10f, ey, 1.6f, accent);
                    Seg(data, stride, ex, ey, ex, ey + h * 0.10f, 1.6f, accent);
                }
                else
                {
                    // cross below
                    float sy = cy + rr + 1f, ey = sy + h * 0.20f;
                    Seg(data, stride, cx, sy, cx, ey, 1.6f, accent);
                    float my = (sy + ey) / 2f;
                    Seg(data, stride, cx - w * 0.09f, my, cx + w * 0.09f, my, 1.6f, accent);
                }
            });
        }

        // Page-arrow chevron centred in a frame.
        private Texture2D ArrowIcon(GraphicsDevice gd, int w, int h, bool left)
        {
            return GlassStrip(gd, w, h, 10, (data, stride, ox, oy, accent) =>
            {
                float cx = ox + w * 0.5f, cy = oy + h * 0.5f, ext = w * 0.16f;
                float px = cx + (left ? -ext : ext);        // chevron point
                float bx = cx + (left ? ext : -ext) * 0.5f; // arm base
                Seg(data, stride, bx, cy - ext * 1.5f, px, cy, 1.8f, accent);
                Seg(data, stride, bx, cy + ext * 1.5f, px, cy, 1.8f, accent);
            });
        }

        // Skin-tone swatch (filled disc) centred in a frame.
        private Texture2D SkinIcon(GraphicsDevice gd, int w, int h, Color skin)
        {
            return GlassStrip(gd, w, h, 10, (data, stride, ox, oy, accent) =>
            {
                float cx = ox + w * 0.5f, cy = oy + h * 0.5f, rr = w * 0.24f;
                Ring(data, stride, cx, cy, rr + 2.4f, rr + 0.6f, accent);
                Disc(data, stride, cx, cy, rr, skin);
            });
        }
    }
}
