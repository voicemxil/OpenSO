using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Runtime.InteropServices;

namespace FSO.TAALab
{
    /// <summary>
    /// MonoGame renderer/input backend for Dear ImGui — the canonical ~300-line implementation from the
    /// ImGui.NET MonoGame sample (ImGui.NET.SampleProgram.XNA), lightly adapted: modern AddKeyEvent /
    /// AddMouse*Event input (ImGui 1.87+ API; package pinned to ImGui.NET 1.89.5 which matches it).
    /// Usage: BeforeLayout(gameTime) -> ImGui.* calls -> AfterLayout(), inside Game.Draw.
    /// </summary>
    public class ImGuiRenderer
    {
        private readonly Game _game;
        private readonly GraphicsDevice _graphicsDevice;
        private BasicEffect _effect;
        private readonly RasterizerState _rasterizerState;

        private byte[] _vertexData;
        private VertexBuffer _vertexBuffer;
        private int _vertexBufferSize;

        private byte[] _indexData;
        private IndexBuffer _indexBuffer;
        private int _indexBufferSize;

        private readonly Dictionary<IntPtr, Texture2D> _loadedTextures = new Dictionary<IntPtr, Texture2D>();
        private int _textureId;
        private IntPtr? _fontTextureId;
        private int _scrollWheelValue;
        private readonly Keys[] _allKeys = Enum.GetValues<Keys>();

        public ImGuiRenderer(Game game)
        {
            var context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            _game = game ?? throw new ArgumentNullException(nameof(game));
            _graphicsDevice = game.GraphicsDevice;

            _rasterizerState = new RasterizerState
            {
                CullMode = CullMode.None,
                DepthBias = 0,
                FillMode = FillMode.Solid,
                MultiSampleAntiAlias = false,
                ScissorTestEnable = true,
                SlopeScaleDepthBias = 0
            };

            game.Window.TextInput += (s, a) =>
            {
                if (a.Character == '\t') return;
                ImGui.GetIO().AddInputCharacter(a.Character);
            };
        }

        /// <summary>Builds the ImGui font atlas texture and binds it. Call once after construction.</summary>
        public unsafe void RebuildFontAtlas()
        {
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out byte* pixelData, out int width, out int height, out int bytesPerPixel);

            var pixels = new byte[width * height * bytesPerPixel];
            Marshal.Copy(new IntPtr(pixelData), pixels, 0, pixels.Length);

            var tex2d = new Texture2D(_graphicsDevice, width, height, false, SurfaceFormat.Color);
            tex2d.SetData(pixels);

            if (_fontTextureId.HasValue) UnbindTexture(_fontTextureId.Value);
            _fontTextureId = BindTexture(tex2d);
            io.Fonts.SetTexID(_fontTextureId.Value);
            io.Fonts.ClearTexData();
        }

        public IntPtr BindTexture(Texture2D texture)
        {
            var id = new IntPtr(_textureId++);
            _loadedTextures.Add(id, texture);
            return id;
        }

        public void UnbindTexture(IntPtr textureId)
        {
            _loadedTextures.Remove(textureId);
        }

        /// <summary>Sets up ImGui for a new frame: feed input, call NewFrame. Draw UI after this.</summary>
        public void BeforeLayout(GameTime gameTime)
        {
            ImGui.GetIO().DeltaTime = Math.Max((float)gameTime.ElapsedGameTime.TotalSeconds, 1f / 1000f);
            UpdateInput();
            ImGui.NewFrame();
        }

        /// <summary>Ends the ImGui frame and renders the draw data.</summary>
        public void AfterLayout()
        {
            ImGui.Render();
            RenderDrawData(ImGui.GetDrawData());
        }

        private void UpdateInput()
        {
            if (!_game.IsActive) return;
            var io = ImGui.GetIO();

            var mouse = Mouse.GetState();
            var keyboard = Keyboard.GetState();

            io.AddMousePosEvent(mouse.X, mouse.Y);
            io.AddMouseButtonEvent(0, mouse.LeftButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(1, mouse.RightButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(2, mouse.MiddleButton == ButtonState.Pressed);
            io.AddMouseWheelEvent(0, (mouse.ScrollWheelValue - _scrollWheelValue) / 120f);
            _scrollWheelValue = mouse.ScrollWheelValue;

            foreach (var key in _allKeys)
            {
                if (TryMapKey(key, out var imKey)) io.AddKeyEvent(imKey, keyboard.IsKeyDown(key));
            }

            io.DisplaySize = new System.Numerics.Vector2(
                _graphicsDevice.PresentationParameters.BackBufferWidth,
                _graphicsDevice.PresentationParameters.BackBufferHeight);
            io.DisplayFramebufferScale = new System.Numerics.Vector2(1f, 1f);
        }

        private static bool TryMapKey(Keys key, out ImGuiKey imKey)
        {
            static ImGuiKey Shift(Keys k, Keys start, ImGuiKey imStart) => imStart + (k - start);

            imKey = key switch
            {
                Keys.Back => ImGuiKey.Backspace,
                Keys.Tab => ImGuiKey.Tab,
                Keys.Enter => ImGuiKey.Enter,
                Keys.CapsLock => ImGuiKey.CapsLock,
                Keys.Escape => ImGuiKey.Escape,
                Keys.Space => ImGuiKey.Space,
                Keys.PageUp => ImGuiKey.PageUp,
                Keys.PageDown => ImGuiKey.PageDown,
                Keys.End => ImGuiKey.End,
                Keys.Home => ImGuiKey.Home,
                Keys.Left => ImGuiKey.LeftArrow,
                Keys.Right => ImGuiKey.RightArrow,
                Keys.Up => ImGuiKey.UpArrow,
                Keys.Down => ImGuiKey.DownArrow,
                Keys.PrintScreen => ImGuiKey.PrintScreen,
                Keys.Insert => ImGuiKey.Insert,
                Keys.Delete => ImGuiKey.Delete,
                >= Keys.D0 and <= Keys.D9 => Shift(key, Keys.D0, ImGuiKey._0),
                >= Keys.A and <= Keys.Z => Shift(key, Keys.A, ImGuiKey.A),
                >= Keys.NumPad0 and <= Keys.NumPad9 => Shift(key, Keys.NumPad0, ImGuiKey.Keypad0),
                >= Keys.F1 and <= Keys.F12 => Shift(key, Keys.F1, ImGuiKey.F1),
                Keys.NumLock => ImGuiKey.NumLock,
                Keys.Scroll => ImGuiKey.ScrollLock,
                Keys.LeftShift or Keys.RightShift => ImGuiKey.ModShift,
                Keys.LeftControl or Keys.RightControl => ImGuiKey.ModCtrl,
                Keys.LeftAlt or Keys.RightAlt => ImGuiKey.ModAlt,
                Keys.OemSemicolon => ImGuiKey.Semicolon,
                Keys.OemPlus => ImGuiKey.Equal,
                Keys.OemComma => ImGuiKey.Comma,
                Keys.OemMinus => ImGuiKey.Minus,
                Keys.OemPeriod => ImGuiKey.Period,
                Keys.OemQuestion => ImGuiKey.Slash,
                Keys.OemTilde => ImGuiKey.GraveAccent,
                Keys.OemOpenBrackets => ImGuiKey.LeftBracket,
                Keys.OemCloseBrackets => ImGuiKey.RightBracket,
                Keys.OemPipe => ImGuiKey.Backslash,
                Keys.OemQuotes => ImGuiKey.Apostrophe,
                _ => ImGuiKey.None
            };
            return imKey != ImGuiKey.None;
        }

        private void RenderDrawData(ImDrawDataPtr drawData)
        {
            var lastViewport = _graphicsDevice.Viewport;
            var lastScissorBox = _graphicsDevice.ScissorRectangle;
            var lastBlend = _graphicsDevice.BlendState;
            var lastDepth = _graphicsDevice.DepthStencilState;
            var lastRaster = _graphicsDevice.RasterizerState;

            _graphicsDevice.BlendFactor = Color.White;
            _graphicsDevice.BlendState = BlendState.NonPremultiplied;
            _graphicsDevice.RasterizerState = _rasterizerState;
            _graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

            drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

            _graphicsDevice.Viewport = new Viewport(0, 0,
                _graphicsDevice.PresentationParameters.BackBufferWidth,
                _graphicsDevice.PresentationParameters.BackBufferHeight);

            UpdateBuffers(drawData);
            RenderCommandLists(drawData);

            _graphicsDevice.Viewport = lastViewport;
            _graphicsDevice.ScissorRectangle = lastScissorBox;
            _graphicsDevice.BlendState = lastBlend;
            _graphicsDevice.DepthStencilState = lastDepth;
            _graphicsDevice.RasterizerState = lastRaster;
        }

        private unsafe void UpdateBuffers(ImDrawDataPtr drawData)
        {
            if (drawData.TotalVtxCount == 0) return;

            if (drawData.TotalVtxCount > _vertexBufferSize)
            {
                _vertexBuffer?.Dispose();
                _vertexBufferSize = (int)(drawData.TotalVtxCount * 1.5f);
                _vertexBuffer = new VertexBuffer(_graphicsDevice, DrawVertDeclaration.Declaration, _vertexBufferSize, BufferUsage.None);
                _vertexData = new byte[_vertexBufferSize * DrawVertDeclaration.Size];
            }

            if (drawData.TotalIdxCount > _indexBufferSize)
            {
                _indexBuffer?.Dispose();
                _indexBufferSize = (int)(drawData.TotalIdxCount * 1.5f);
                _indexBuffer = new IndexBuffer(_graphicsDevice, IndexElementSize.SixteenBits, _indexBufferSize, BufferUsage.None);
                _indexData = new byte[_indexBufferSize * sizeof(ushort)];
            }

            int vtxOffset = 0, idxOffset = 0;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdListsRange[n];
                fixed (void* vtxDstPtr = &_vertexData[vtxOffset * DrawVertDeclaration.Size])
                fixed (void* idxDstPtr = &_indexData[idxOffset * sizeof(ushort)])
                {
                    Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vtxDstPtr,
                        _vertexData.Length, cmdList.VtxBuffer.Size * DrawVertDeclaration.Size);
                    Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, idxDstPtr,
                        _indexData.Length, cmdList.IdxBuffer.Size * sizeof(ushort));
                }
                vtxOffset += cmdList.VtxBuffer.Size;
                idxOffset += cmdList.IdxBuffer.Size;
            }

            _vertexBuffer.SetData(_vertexData, 0, drawData.TotalVtxCount * DrawVertDeclaration.Size);
            _indexBuffer.SetData(_indexData, 0, drawData.TotalIdxCount * sizeof(ushort));
        }

        private void RenderCommandLists(ImDrawDataPtr drawData)
        {
            if (drawData.TotalVtxCount == 0) return;
            _graphicsDevice.SetVertexBuffer(_vertexBuffer);
            _graphicsDevice.Indices = _indexBuffer;

            int vtxOffset = 0, idxOffset = 0;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdListsRange[n];
                for (int cmdi = 0; cmdi < cmdList.CmdBuffer.Size; cmdi++)
                {
                    var drawCmd = cmdList.CmdBuffer[cmdi];
                    if (drawCmd.ElemCount == 0) continue;

                    if (!_loadedTextures.TryGetValue(drawCmd.TextureId, out var texture))
                        throw new InvalidOperationException($"Could not find ImGui texture id {drawCmd.TextureId}");

                    _graphicsDevice.ScissorRectangle = new Rectangle(
                        (int)drawCmd.ClipRect.X,
                        (int)drawCmd.ClipRect.Y,
                        (int)(drawCmd.ClipRect.Z - drawCmd.ClipRect.X),
                        (int)(drawCmd.ClipRect.W - drawCmd.ClipRect.Y));

                    var effect = UpdateEffect(texture);
                    foreach (var pass in effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        _graphicsDevice.DrawIndexedPrimitives(
                            PrimitiveType.TriangleList,
                            (int)drawCmd.VtxOffset + vtxOffset,
                            (int)drawCmd.IdxOffset + idxOffset,
                            (int)drawCmd.ElemCount / 3);
                    }
                }
                vtxOffset += cmdList.VtxBuffer.Size;
                idxOffset += cmdList.IdxBuffer.Size;
            }
        }

        private Effect UpdateEffect(Texture2D texture)
        {
            _effect ??= new BasicEffect(_graphicsDevice);
            var io = ImGui.GetIO();
            _effect.World = Matrix.Identity;
            _effect.View = Matrix.Identity;
            _effect.Projection = Matrix.CreateOrthographicOffCenter(0f, io.DisplaySize.X, io.DisplaySize.Y, 0f, -1f, 1f);
            _effect.TextureEnabled = true;
            _effect.Texture = texture;
            _effect.VertexColorEnabled = true;
            return _effect;
        }
    }

    /// <summary>Vertex declaration matching ImDrawVert (pos: 2 floats, uv: 2 floats, col: RGBA8).</summary>
    public static class DrawVertDeclaration
    {
        public static readonly VertexDeclaration Declaration;
        public static readonly int Size;

        static DrawVertDeclaration()
        {
            unsafe { Size = sizeof(ImDrawVert); }
            Declaration = new VertexDeclaration(
                Size,
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
                new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
                new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0));
        }
    }
}
