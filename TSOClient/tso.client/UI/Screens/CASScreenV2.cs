using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using FSO.Common.Utils;
using FSO.Common.Rendering.Framework.IO;

namespace FSO.Client.UI.Screens
{
    /// <summary>
    /// Modern Create-A-Sim (OpenSO) — behind the GlobalSettings.ModernCAS feature flag.
    ///
    /// Built from scratch: the legacy sprite UI is hidden entirely and replaced with a full-window perspective
    /// sim (left, TS4-style), a rounded frosted glass panel, and a Head / Body / Bio tab bar (dark-studio theme:
    /// near-black panel, teal accent). Only the underlying DATA is reused — the inherited head/body collection
    /// viewers (selection holders, shown one at a time) and the name/description text edits (read by
    /// PersonSelectionEditController) — so the avatar-create save path / server PDU is untouched.
    /// </summary>
    public class CASScreenV2 : PersonSelectionEdit
    {
        // Panel geometry in the 1024x768 design space.
        private const float PX = 470, PY = 70, PW = 545, PH = 662;
        private const int IconSize = 42;                 // gender/skin glyph button size
        private const int HeadCellW = 72, HeadCellH = 72;  // head thumbs are native ~square (33x33)
        private const int BodyCellW = 72, BodyCellH = 139; // body thumbs are native tall (33x70)
        private const int CellOff = 6, CellR = 12;         // image inset + corner radius
        private static readonly Color Accent = new Color(46, 196, 150); // teal (dark-studio accent)

        private int _Tab;
        private readonly List<UIImage> _TabBg = new List<UIImage>();
        private readonly List<UILabel> _TabLabel = new List<UILabel>();
        private Texture2D _TabActive, _TabInactive;
        private TextStyle _LightText, _DarkText;

        public CASScreenV2() : base()
        {
            var gw = GlobalSettings.Default.GraphicsWidth;
            var gh = GlobalSettings.Default.GraphicsHeight;
            var gd = GameFacade.GraphicsDevice;

            // ---- Full-window perspective sim (shifted left), layered behind the UI ----
            SimBox.AutoRotate = true;
            SimBox.Interactive = true;
            SimBox.Size = new Vector2(gw, gh);
            SimBox.SetPerspective(true, gw, gh);
            SimBox.Position = new Vector2((gw - 1024) / -2f, (gh - 768) / -2f);
            this.AddAt(1, SimBox);
            if (DialogBackground != null) this.Remove(DialogBackground);

            m_HeadSkinBrowser.OnChange += (e) => SimBox.FocusHead();
            m_BodySkinBrowser.OnChange += (e) => SimBox.FocusBody();

            // ---- Hide ALL legacy controls; we rebuild the menu from scratch ----
            foreach (var child in new List<UIElement>(Children))
            {
                if (child == Background || child == SimBox) continue;
                child.Visible = false;
            }

            // ---- Custom dark-studio gradient backdrop (replaces the legacy blue background) ----
            if (Background != null) Background.Visible = false;
            var backdrop = new UIImage(RadialGradient(gd, 480, 300, new Vector2(0.30f, 0.40f),
                new Color(48, 56, 72), new Color(7, 9, 14), 0.98f));
            backdrop.SetSize(gw, gh);
            backdrop.Position = new Vector2((gw - 1024) / -2f, (gh - 768) / -2f);
            this.AddAt(1, backdrop); // behind the sim (sim shifts to index 2)

            // ---- Rounded frosted glass panel — placed BEHIND the controls (above the sim) ----
            var panel = new UIImage(RoundedRect(gd, (int)PW, (int)PH, 18, new Color(19, 23, 30, 222))) { X = PX, Y = PY };
            this.AddAt(3, panel);

            // ---- Studio lighting is driven by UISim's directional technique; keep AmbientLight neutral so it doesn't
            //      uniformly scale (and re-flatten / clip) the directional result. ----
            if (SimBox.Avatar != null) SimBox.Avatar.AmbientLight = Vector4.One;

            // ---- Head / Body / Bio tab bar ----
            _TabActive = RoundedRect(gd, 150, 40, 11, Accent);
            _TabInactive = RoundedRect(gd, 150, 40, 11, new Color(38, 44, 56, 210));
            _LightText = TextStyle.Create(new Color(216, 224, 234), 13);
            _DarkText = TextStyle.Create(new Color(6, 22, 17), 13);
            string[] tabs = { "Head", "Body", "Bio" };
            for (int i = 0; i < tabs.Length; i++)
            {
                float tx = PX + 18 + i * 162, ty = PY + 16;
                var bg = new UIImage(_TabInactive) { X = tx, Y = ty };
                this.Add(bg);
                _TabBg.Add(bg);
                var lbl = new UILabel { Caption = tabs[i], X = tx + 56, Y = ty + 11, CaptionStyle = _LightText };
                this.Add(lbl);
                _TabLabel.Add(lbl);
                int idx = i;
                // Register the click on the tab IMAGE itself (local 0,0,w,h region), the way UIButton/UIImage do.
                bg.ListenForMouse((type, state) => { if (type == UIMouseEventType.MouseUp) SetTab(idx); });
            }

            // ---- Reposition the reused interactive widgets onto the panel ----
            Place(FemaleButton,        PX + 26,  PY + 78);
            Place(MaleButton,          PX + 74,  PY + 78);
            Place(SkinLightButton,     PX + 160, PY + 78);
            Place(SkinMediumButton,    PX + 208, PY + 78);
            Place(SkinDarkButton,      PX + 256, PY + 78);

            // ---- Re-skin the gender / skin pickers with custom glassy glyph icons ----
            ReskinButton(FemaleButton,     GenderIcon(gd, IconSize, IconSize, false));
            ReskinButton(MaleButton,       GenderIcon(gd, IconSize, IconSize, true));
            ReskinButton(SkinLightButton,  SkinIcon(gd, IconSize, IconSize, new Color(242, 211, 181)));
            ReskinButton(SkinMediumButton, SkinIcon(gd, IconSize, IconSize, new Color(198, 138, 91)));
            ReskinButton(SkinDarkButton,   SkinIcon(gd, IconSize, IconSize, new Color(120, 78, 52)));

            // ---- Expand both thumbnail grids to fill the panel, with frosted-glass cell frames. Head and body
            //      thumbnails have different native aspects (~1:1 vs ~33:70), so each grid gets its own cell shape;
            //      the image area is ThumbSize - 2*offset, so it must match the native aspect to avoid stretching. ----
            // HEAD: square cells (image area 60x60) → ~6 columns.
            m_HeadSkinBrowser.Size            = new Vector2(PW - 44, PH - 230);
            m_HeadSkinBrowser.ThumbSize       = new Vector2(HeadCellW, HeadCellH);
            m_HeadSkinBrowser.ThumbMargins    = new Vector2(8, 8);
            m_HeadSkinBrowser.ThumbImageOffsets = new Vector2(CellOff, CellOff);
            m_HeadSkinBrowser.ThumbButtonImage = GlassStrip(gd, HeadCellW, HeadCellH, CellR);
            m_HeadSkinBrowser.Relayout();
            // BODY: tall cells (image area 60x127) preserve the full-body portrait aspect.
            m_BodySkinBrowser.Size            = new Vector2(PW - 44, PH - 230);
            m_BodySkinBrowser.ThumbSize       = new Vector2(BodyCellW, BodyCellH);
            m_BodySkinBrowser.ThumbMargins    = new Vector2(6, 6);
            m_BodySkinBrowser.ThumbImageOffsets = new Vector2(CellOff, CellOff);
            m_BodySkinBrowser.ThumbButtonImage = GlassStrip(gd, BodyCellW, BodyCellH, CellR);
            m_BodySkinBrowser.Relayout();

            Place(m_HeadSkinBrowser,   PX + 22,  PY + 150);
            Place(m_BodySkinBrowser,   PX + 22,  PY + 150);

            Place(NameTextEdit,        PX + 40,  PY + 90);
            Place(DescriptionTextEdit, PX + 40,  PY + 170);
            Place(DescriptionSlider,   PX + 480, PY + 170);
            Place(DescriptionScrollUpButton,   PX + 478, PY + 164);
            Place(DescriptionScrollDownButton, PX + 478, PY + 430);

            Place(AcceptButton,        PX + 330, PY + 600);
            Place(CancelButton,        PX + 170, PY + 600);

            SetTab(0);
        }

        private void Place(UIElement e, float x, float y)
        {
            if (e == null) return;
            e.Visible = true;
            e.Position = new Vector2(x, y);
        }

        // Swap a reused gender/skin UIButton onto a generated 4-state glass icon. Frame width == IconSize so the
        // button's horizontal 9-slice is a no-op; ImageStates re-syncs the click region to the new texture.
        private void ReskinButton(UIButton btn, Texture2D tex)
        {
            if (btn == null) return;
            btn.Texture = tex;
            btn.ImageStates = 4;
            btn.Width = IconSize;
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
            foreach (var b in new[] { FemaleButton, MaleButton, SkinLightButton, SkinMediumButton, SkinDarkButton })
                if (b != null) b.Visible = !bio;
            foreach (UIElement b in new UIElement[] { NameTextEdit, DescriptionTextEdit, DescriptionSlider,
                                                      DescriptionScrollUpButton, DescriptionScrollDownButton })
                if (b != null) b.Visible = bio;

            if (head) SimBox.FocusHead();
            else if (body) SimBox.FocusBody();
        }

        // Solid rounded-rectangle texture (transparent outside the radius). Color carries its own alpha.
        private static Texture2D RoundedRect(GraphicsDevice gd, int w, int h, int r, Color color)
        {
            var tex = new Texture2D(gd, w, h);
            var data = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int cx = (x < r) ? r : (x >= w - r ? w - r - 1 : x);
                    int cy = (y < r) ? r : (y >= h - r ? h - r - 1 : y);
                    float dx = x - cx, dy = y - cy;
                    data[y * w + x] = (dx * dx + dy * dy > (float)r * r) ? Color.Transparent : color;
                }
            tex.SetData(data);
            return tex;
        }

        // Radial gradient texture: inner colour at centerFrac fading (smoothstep) to outer at radiusFrac of width.
        private static Texture2D RadialGradient(GraphicsDevice gd, int w, int h, Vector2 centerFrac, Color inner, Color outer, float radiusFrac)
        {
            var tex = new Texture2D(gd, w, h);
            var data = new Color[w * h];
            float cx = centerFrac.X * w, cy = centerFrac.Y * h, maxR = radiusFrac * w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float d = MathHelper.Clamp((float)System.Math.Sqrt(dx * dx + dy * dy) / maxR, 0f, 1f);
                    d = d * d * (3f - 2f * d); // smoothstep
                    data[y * w + x] = Color.Lerp(inner, outer, d);
                }
            tex.SetData(data);
            return tex;
        }

        // ---------------------------------------------------------------------------------------------------------
        //  Procedural glassy controls (frosted rounded buttons + simple glyph icons) for the dark-studio theme.
        // ---------------------------------------------------------------------------------------------------------

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

        // Fill a rounded rect (frame at ox,oy of size w x h in a `stride`-wide buffer) with a frosted fill,
        // a vertical top→bottom sheen, and a coloured border.
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

                    // top sheen: lighten the upper portion of the fill for a glassy look
                    float sheen = MathHelper.Clamp(1f - (float)y / h, 0f, 1f);
                    var f = fill;
                    f = new Color(
                        (int)MathHelper.Clamp(f.R + sheen * 26, 0, 255),
                        (int)MathHelper.Clamp(f.G + sheen * 26, 0, 255),
                        (int)MathHelper.Clamp(f.B + sheen * 30, 0, 255),
                        f.A);

                    // border: within borderW of the rounded edge
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

        // 4-state frosted-glass button strip (normal / selected+hover / down / disabled), each frame w x h.
        // The optional `glyph` callback stamps an icon centred in each frame; it receives the per-state accent colour.
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
                new Color(8, 26, 22, 255),     // dark glyph reads on the teal highlight
                new Color(8, 26, 22, 255),
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
