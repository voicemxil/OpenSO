using System;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using FSO.Files.Utils;
using System.IO;
using Microsoft.Xna.Framework;
using FSO.Common.Utils;
using FSO.Common.Rendering;
using FSO.Common;
using System.Threading;

namespace FSO.Files.Formats.IFF.Chunks
{
    /// <summary>
    /// This chunk type holds a number of paletted sprites that may have z-buffer and/or alpha channels.
    /// </summary>
    public class SPR2 : IffChunk
    {
        public SPR2Frame[] Frames = new SPR2Frame[0];
        public uint DefaultPaletteID;
        public bool SpritePreprocessed;

        private bool _ZAsAlpha;
        public bool ZAsAlpha
        {
            get
            {
                return _ZAsAlpha;
            }
            set
            {
                if (value && !_ZAsAlpha)
                {
                    foreach (var frame in Frames)
                    {
                        if (frame.Decoded && frame.PixelData != null) frame.CopyZToAlpha();
                    }
                }
                _ZAsAlpha = value;

            }
        }

        private int _FloorCopy;
        public int FloorCopy
        {
            get
            {
                return _FloorCopy;
            }
            set
            {
                if (value > 0 && _FloorCopy == 0)
                {
                    foreach (var frame in Frames)
                    {
                        if (frame.Decoded && frame.PixelData != null)
                        {
                            if (value == 1) frame.FloorCopy();
                            if (value == 2) frame.FloorCopyWater();
                        }
                    }
                }
                _FloorCopy = value;
            }
        }

        /// <summary>
        /// Reads a SPR2 chunk from a stream.
        /// </summary>
        /// <param name="iff">An Iff instance.</param>
        /// <param name="stream">A Stream object holding a SPR2 chunk.</param>
        public override void Read(IffFile iff, Stream stream)
        {
            using (var io = IoBuffer.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                var version = io.ReadUInt32();
                uint spriteCount = 0;

                if (version == 1000)
                {
                    spriteCount = io.ReadUInt32();
                    DefaultPaletteID = io.ReadUInt32();
                    var offsetTable = new uint[spriteCount];
                    for (var i = 0; i < spriteCount; i++)
                    {
                        offsetTable[i] = io.ReadUInt32();
                    }

                    Frames = new SPR2Frame[spriteCount];
                    for (var i = 0; i < spriteCount; i++)
                    {
                        var frame = new SPR2Frame(this);
                        io.Seek(SeekOrigin.Begin, offsetTable[i]);

                        var guessedSize = ((i + 1 < offsetTable.Length) ? offsetTable[i + 1] : (uint)stream.Length) - offsetTable[i];

                        frame.Read(version, io, guessedSize);
                        Frames[i] = frame;
                    }
                }
                else if (version == 1001)
                {
                    DefaultPaletteID = io.ReadUInt32();
                    spriteCount = io.ReadUInt32();

                    Frames = new SPR2Frame[spriteCount];
                    for (var i = 0; i < spriteCount; i++)
                    {
                        var frame = new SPR2Frame(this);
                        frame.Read(version, io, 0);
                        Frames[i] = frame;
                    }
                }
            }
        }

        public override bool Write(IffFile iff, Stream stream)
        {
            using (var io = IoWriter.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                if (IffFile.TargetTS1)
                {
                    io.WriteUInt32(1000);
                    uint length = 0;
                    if (Frames != null) length = (uint)Frames.Length;
                    io.WriteUInt32(length);
                    DefaultPaletteID = Frames?.FirstOrDefault()?.PaletteID ?? DefaultPaletteID;
                    io.WriteUInt32(DefaultPaletteID);
                    // begin offset table
                    var offTableStart = stream.Position;
                    for (int i = 0; i < length; i++) io.WriteUInt32(0); //filled in later
                    var offsets = new uint[length];
                    int offInd = 0;
                    if (Frames != null)
                    {
                        foreach (var frame in Frames)
                        {
                            offsets[offInd++] = (uint)stream.Position;
                            frame.Write(io, true);
                        }
                    }
                    io.Seek(SeekOrigin.Begin, offTableStart);
                    foreach (var off in offsets) io.WriteUInt32(off);
                    io.Seek(SeekOrigin.End, 0);
                }
                else
                {
                    io.WriteUInt32(1001);
                    io.WriteUInt32(DefaultPaletteID);
                    if (Frames == null) io.WriteUInt32(0);
                    else
                    {
                        io.WriteUInt32((uint)Frames.Length);
                        foreach (var frame in Frames)
                        {
                            frame.Write(io, false);
                        }
                    }
                }
                return true;
            }
        }

        public void CopyZToAlpha()
        {
            foreach (var frame in Frames)
            {
                frame.CopyZToAlpha();
            }
        }

        public override void Dispose()
        {
            if (Frames == null) return;
            foreach (var frame in Frames)
            {
                var palette = ChunkParent.Get<PALT>(frame.PaletteID);
                if (palette != null) palette.References--;
            }
        }
    }

    /// <summary>
    /// The frame (I.E sprite) of a SPR2 chunk.
    /// </summary>
    public class SPR2Frame : ITextureProvider, IWorldTextureProvider
    {
        public Color[] PixelData;
        public byte[] ZBufferData;
        public byte[] PalData;

        private WeakReference<Texture2D> ZCache = new WeakReference<Texture2D>(null);
        private WeakReference<Texture2D> PixelCache = new WeakReference<Texture2D>(null);
        private Texture2D PermaRefZ;
        private Texture2D PermaRefP;

        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public uint Flags { get; internal set; }
        public ushort PaletteID { get; set; }
        public ushort TransparentColorIndex { get; internal set; }
        public Vector2 Position { get; internal set; }
        
        private SPR2 Parent;
        private uint Version;
        private byte[] ToDecode;
        private PALT Palette;
        public bool Decoded
        {
            get
            {
                return ToDecode == null;
            }
        }
        public bool ContainsNothing = false;
        public bool ContainsNoZ => (Flags & 0x02) != 0x02;

        public SPR2Frame(SPR2 parent)
        {
            this.Parent = parent;
        }

        /// <summary>
        /// Reads a SPR2 chunk from a stream.
        /// </summary>
        /// <param name="version">Version of the SPR2 that this frame belongs to.</param>
        /// <param name="stream">A IOBuffer object used to read a SPR2 chunk.</param>
        public void Read(uint version, IoBuffer io, uint guessedSize)
        {
            Version = version;
            if (version == 1001)
            {
                var spriteVersion = io.ReadUInt32();
                var spriteSize = io.ReadUInt32();
                ReadHead(version, io);
                if (IffFile.RETAIN_CHUNK_DATA) ReadDeferred(1001, io);
                else ToDecode = io.ReadBytes(spriteSize - 10);
            } else
            {
                ReadHead(version, io);
                if (IffFile.RETAIN_CHUNK_DATA) ReadDeferred(1000, io);
                else ToDecode = io.ReadBytes(guessedSize - 10);
            }
        }

        private void ReadHead(uint version, IoBuffer io)
        {
            // Useful to read these early for async loading
            this.Width = io.ReadUInt16();
            this.Height = io.ReadUInt16();
            this.Flags = io.ReadUInt32();
            this.PaletteID = io.ReadUInt16();

            if (version == 1000 || this.PaletteID == 0 || this.PaletteID == 0xA3A3)
            {
                this.PaletteID = (ushort)Parent.DefaultPaletteID;
            }

            UpdatePalette();
        }

        private void UpdatePalette()
        {
            Palette = Parent.ChunkParent.Get<PALT>(this.PaletteID);
            if (Palette == null) Palette = new PALT() { Colors = new Color[256] };
        }

        private PALT EnsurePalette()
        {
            if (Palette == null)
            {
                UpdatePalette();
            }

            return Palette;
        }

        public void ReadDeferred(uint version, IoBuffer io)
        {
            TransparentColorIndex = io.ReadUInt16();

            var y = io.ReadInt16();
            var x = io.ReadInt16();
            this.Position = new Vector2(x, y);

            this.Decode(io);
        }

        private int _decoding = 0;

        public void DecodeIfRequired(bool z)
        {
            if (ToDecode != null && ((!ContainsNoZ && ZBufferData == null) || (!z && PixelData == null)))
            {
                if (Interlocked.CompareExchange(ref _decoding, 1, 0) > 0)
                {
                    // If another thread is already decoding the sprite, spin until it's done.
                    SpinWait w = default;
                    while (Volatile.Read(ref _decoding) > 0)
                    {
                        w.SpinOnce();
                    }

                    DecodeIfRequired(z);
                }
                else
                {
                    using (IoBuffer buf = IoBuffer.FromStream(new MemoryStream(ToDecode), ByteOrder.LITTLE_ENDIAN))
                    {
                        ReadDeferred(Version, buf);
                    }

                    if (TimedReferenceController.CurrentType == CacheType.PERMANENT) ToDecode = null;

                    Interlocked.Exchange(ref _decoding, 0);
                }
            }
        }

        public void Write(IoWriter io, bool ts1)
        {
            using (var sprStream = new MemoryStream())
            {
                var sprIO = IoWriter.FromStream(sprStream, ByteOrder.LITTLE_ENDIAN);
                sprIO.WriteUInt16((ushort)Width);
                sprIO.WriteUInt16((ushort)Height);
                sprIO.WriteUInt32(Flags);
                sprIO.WriteUInt16(PaletteID);
                sprIO.WriteUInt16(TransparentColorIndex);
                sprIO.WriteUInt16((ushort)Position.Y);
                sprIO.WriteUInt16((ushort)Position.X);
                SPR2FrameEncoder.WriteFrame(this, sprIO);

                var data = sprStream.ToArray();
                if (!ts1)
                {
                    io.WriteUInt32(1001);
                    io.WriteUInt32((uint)data.Length);
                }
                io.WriteBytes(data);
            }
        }

        /// <summary>
        /// Decodes this SPR2Frame.
        /// </summary>
        /// <param name="io">An IOBuffer instance used to read a SPR2Frame.</param>
        private void Decode(IoBuffer io)
        {
            var y = 0;
            var endmarker = false;

            var hasPixels = (this.Flags & 0x01) == 0x01;
            var hasZBuffer = (this.Flags & 0x02) == 0x02;
            var hasAlpha = (this.Flags & 0x04) == 0x04;

            var numPixels = this.Width * this.Height;
            var ow = Width;
            var fc = Parent.FloorCopy;
            if (fc > 0 && Width % 2 != 0)
            {
                numPixels += Height;
                Width++;
            }

            Color[] pixelData = null;
            byte[] palData = null;
            byte[] zData = null;

            if (hasPixels){
                pixelData = new Color[numPixels];
                palData = new byte[numPixels];
            }
            if (hasZBuffer){
                zData = new byte[numPixels];
            }

            var palette = EnsurePalette();
            palette.References++;
            var transparentPixel = palette.Colors[TransparentColorIndex];
            transparentPixel.A = 0;

            while (!endmarker && io.HasMore)
            {
                var marker = io.ReadUInt16();
                var command = marker >> 13;
                var count = marker & 0x1FFF;

                switch (command)
                {
                    /** Fill with pixel data **/
                    case 0x00:
                        var bytes = count;
                        bytes -= 2;

                        var x = 0;

                        while (bytes > 0)
                        {
                            var pxMarker = io.ReadUInt16();
                            var pxCommand = pxMarker >> 13;
                            var pxCount = pxMarker & 0x1FFF;
                            bytes -= 2;

                            switch (pxCommand)
                            {
                                case 0x01:
                                case 0x02:
                                    var pxWithAlpha = pxCommand == 0x02;
                                    for (var col = 0; col < pxCount; col++)
                                    {
                                        var zValue = io.ReadByte();
                                        var pxValue = io.ReadByte();
                                        bytes -= 2;

                                        var pxColor = palette.Colors[pxValue];
                                        if (pxWithAlpha)
                                        {
                                            var alpha = io.ReadByte();
                                            pxColor.A = (byte)(alpha * 8.2258064516129032258064516129032);
                                            bytes--;
                                        }
                                        //this mode draws the transparent colour as solid for some reason.
                                        //fixes backdrop theater
                                        var offset = (y * Width) + x;
                                        pixelData[offset] = pxColor;
                                        palData[offset] = pxValue;
                                        zData[offset] = zValue;
                                        x++;
                                    }
                                    if (pxWithAlpha)
                                    {
                                        /** Padding? **/
                                        if ((pxCount * 3) % 2 != 0){
                                            bytes--;
                                            io.ReadByte();
                                        }
                                    }
                                    break;
                                case 0x03:
                                    for (var col = 0; col < pxCount; col++)
                                    {
                                        var offset = (y * Width) + x;
                                        pixelData[offset] = transparentPixel;
                                        palData[offset] = (byte)TransparentColorIndex;
                                        pixelData[offset].A = 0;
                                        if (hasZBuffer){
                                            zData[offset] = 255;
                                        }
                                        x++;
                                    }
                                    break;
                                case 0x06:
                                    for (var col = 0; col < pxCount; col++)
                                    {
                                        var pxIndex = io.ReadByte();
                                        bytes--;
                                        var offset = (y * Width) + x;
                                        var pxColor = palette.Colors[pxIndex];
                                        byte z = 0;

                                        //not sure if this should happen
                                        /*if (pxIndex == TransparentColorIndex)
                                        {
                                            pxColor.A = 0;
                                            z = 255;
                                        }*/
                                        pixelData[offset] = pxColor;
                                        palData[offset] = pxIndex;
                                        if (hasZBuffer)
                                        {
                                            zData[offset] = z;
                                        }
                                        x++;
                                    }
                                    if (pxCount % 2 != 0)
                                    {
                                        bytes--;
                                        io.ReadByte();
                                    }
                                    break;
                            }
                        }

                        /** If row isnt filled in, the rest is transparent **/
                        while (x < ow)
                        {
                            var offset = (y * Width) + x;
                            if (hasZBuffer)
                            {
                                zData[offset] = 255;
                            }
                            x++;
                        }
                        break;
                    /**  Leave the next count rows in the color channel filled with the transparent color, 
                     * in the z-buffer channel filled with 255, and in the alpha channel filled with 0. **/
                    case 0x04:
                        for (var row = 0; row < count; row++)
                        {
                            for (var col = 0; col < Width; col++)
                            {
                                var offset = ((y+row) * Width) + col;
                                if (hasPixels) 
                                {
                                    pixelData[offset] = transparentPixel;
                                    palData[offset] = (byte)TransparentColorIndex;
                                }
                                if (hasAlpha)
                                {
                                    pixelData[offset].A = 0;
                                }
                                if (hasZBuffer)
                                {
                                    zData[offset] = 255;
                                }
                            }
                        }
                        y += count - 1;
                        break;
                    case 0x05:
                        endmarker = true;
                        break;
                }
                y++;
            }

            this.PixelData = pixelData;
            this.PalData = palData;
            this.ZBufferData = zData;

            if (!IffFile.RETAIN_CHUNK_DATA) PalData = null;
            if (Parent.ZAsAlpha) CopyZToAlpha();
            if (Parent.FloorCopy == 1) FloorCopy();
            if (Parent.FloorCopy == 2) FloorCopyWater();
        }

        /// <summary>
        /// Gets a pixel from this SPR2Frame.
        /// </summary>
        /// <param name="x">X position of pixel.</param>
        /// <param name="y">Y position of pixel.</param>
        /// <returns>A Color instance with color of pixel.</returns>
        public Color GetPixel(int x, int y)
        {
            return PixelData[(y * Width) + x];
        }

        /// <summary>
        /// Gets a pixel from this SPR2Frame.
        /// </summary>
        /// <param name="x">X position of pixel.</param>
        /// <param name="y">Y position of pixel.</param>
        public void SetPixel(int x, int y, Color color)
        {
            PixelData[(y * Width) + x] = color;
        }

        /// <summary>
        /// Copies the Z buffer into the current sprite's alpha channel. Used by water tile.
        /// </summary>
        public void CopyZToAlpha()
        {
            for (int i=0; i<PixelData.Length; i++)
            {
                PixelData[i].A = (ZBufferData[i] < 32)?(byte)0:ZBufferData[i];
            }
        }

        public void FloorCopy()
        {
            if (Width%2 != 0)
            {
                var target = new Color[(Width + 1) * Height];
                for (int y=0; y<Height; y++)
                {
                    Array.Copy(PixelData, y * Width, target, y * (Width + 1), Width);
                }
                PixelData = target;
                Width += 1;
            }
            var ndat = new Color[PixelData.Length];
            int hw = (Width) / 2;
            int hh = (Height) / 2;
            int idx = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var xp = (x + hw) % Width;
                    var yp = (y + hh) % Height;
                    var rep = PixelData[xp + yp * Width];
                    if (rep.A >= 254) ndat[idx] = rep;
                    else ndat[idx] = PixelData[idx];
                    idx++;
                }
            }
            PixelData = ndat;
        }

        public void FloorCopyWater()
        {
            if (Width % 2 != 0)
            {
                var target = new Color[(Width + 1) * Height];
                for (int y = 0; y < Height; y++)
                {
                    Array.Copy(PixelData, y * Width, target, y * (Width + 1), Width);
                }
                PixelData = target;
                Width += 1;
            }
            var ndat = new Color[PixelData.Length];
            int hw = (Width) / 2;
            int hh = (Height) / 2;
            int idx = 0;

            var palette = Parent.ChunkParent.Get<PALT>(this.PaletteID);
            var transparentPixel = palette.Colors[TransparentColorIndex];
            transparentPixel.A = 0;

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var dat = PixelData[x + y * Width];
                    if (dat.PackedValue == 0 || dat.PackedValue == transparentPixel.PackedValue)
                    {
                        if (x < hw)
                        {
                            for (int j = x; j < Width; j++)
                            {
                                var rep = PixelData[j + y * Width];
                                if (!(rep.PackedValue == 0 || rep.PackedValue == transparentPixel.PackedValue))
                                {
                                    ndat[idx] = rep;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            for (int j = x; j >= 0; j--)
                            {
                                var rep = PixelData[j + y * Width];
                                if (!(rep.PackedValue == 0 || rep.PackedValue == transparentPixel.PackedValue))
                                {
                                    ndat[idx] = rep;
                                    break;
                                }
                            }
                        }
                    } else
                    {
                        ndat[idx] = PixelData[idx];
                    }
                    idx++;
                }
            }
            PixelData = ndat;
        }

        /// <summary>
        /// Gets a texture representing this SPR2Frame.
        /// </summary>
        /// <param name="device">GraphicsDevice instance used for drawing.</param>
        /// <returns>A Texture2D instance holding the texture data.</returns>
        public Texture2D GetTexture(GraphicsDevice device)
        {
            return GetTexture(device, true);
        }

        private Texture2D GetTexture(GraphicsDevice device, bool onlyThis)
        {
            if (ContainsNothing) return null;
            Texture2D result = null;
            if (!PixelCache.TryGetTarget(out result) || ((CachableTexture2D)result).BeingDisposed || result.IsDisposed)
            {
                if (this.Width == 0 || this.Height == 0)
                {
                    ContainsNothing = true;
                    return null;
                }

                var effectiveWidth = Parent.FloorCopy > 0 ? ((Width + 1) & ~1) : Width;

                var tc = FSOEnvironment.TexCompress;
                // Mip chains serve 3D billboard minification and are DirectX-only now: on the GL
                // backend, mipped sprite textures only exist when launched 3D-enabled (never in the
                // pure-2D client, which renders correctly) and are implicated in hybrid-2D sprite
                // artifacts on GL. GL loses a little smoothness on far 3D billboards.
                var mip = FSOEnvironment.Enable3D && FSOEnvironment.DirectX && (FSOEnvironment.EnableNPOTMip || (effectiveWidth == 128 && Height == 64));
                if (mip && TextureUtils.OverrideCompression(effectiveWidth, Height)) tc = false;
                if (tc)
                {
                    
                    result = new CachableTexture2D(device, ((effectiveWidth + 3)/4)*4, ((Height + 3) / 4) * 4, mip, SurfaceFormat.Dxt5);

                    AssetStreaming.LoadTexture(result, AssetStreamingMode.Lot,
                        () => {
                            DecodeIfRequired(false);
                            TextureData<byte>[] data;
                            if (mip) data = TextureUtils.GenerateDXT5WithMips(result, effectiveWidth, Height, this.PixelData);
                            else
                            {
                                data = new TextureData<byte>[]
                                {
                                    new TextureData<byte>(0, TextureUtils.DXT5Compress(this.PixelData, effectiveWidth, this.Height).Item1, 1)
                                };
                            }

                            if (!IffFile.RETAIN_CHUNK_DATA)
                            {
                                PixelData = null;
                            }

                            return data;
                        });
                }
                else
                {
                    result = new CachableTexture2D(device, effectiveWidth, this.Height, mip, SurfaceFormat.Color);
                    AssetStreaming.LoadTexture(result, AssetStreamingMode.Lot,
                        () =>
                        {
                            DecodeIfRequired(false);

                            TextureData<Color>[] data;
                            if (mip) data = TextureUtils.GenerateMips(result, this.PixelData);
                            else
                            {
                                data = new TextureData<Color>[]
                                {
                                    new TextureData<Color>(0, this.PixelData)
                                };
                            }

                            if (!IffFile.RETAIN_CHUNK_DATA)
                            {
                                PixelData = null;
                            }

                            return data;
                        });
                }

                result.Tag = new TextureInfo(result, effectiveWidth, Height);
                PixelCache = new WeakReference<Texture2D>(result);
                if (TimedReferenceController.CurrentType == CacheType.PERMANENT) PermaRefP = result;
            }
            if (TimedReferenceController.CurrentType != CacheType.PERMANENT) TimedReferenceController.KeepAlive(result, KeepAliveType.ACCESS);
            return result;
        }

        public Texture2D TryGetCachedZ()
        {
            Texture2D result = null;
            if (ContainsNothing || ContainsNoZ) return null;
            if (!ZCache.TryGetTarget(out result) || ((CachableTexture2D)result).BeingDisposed || result.IsDisposed)
                return null;
            return result;
        }

        /// <summary>
        /// Gets a z-texture representing this SPR2Frame.
        /// </summary>
        /// <param name="device">GraphicsDevice instance used for drawing.</param>
        /// <returns>A Texture2D instance holding the texture data.</returns>
        public Texture2D GetZTexture(GraphicsDevice device)
        {
            return GetZTexture(device, true);
        }

        private Texture2D GetZTexture(GraphicsDevice device, bool onlyThis)
        {
            Texture2D result = null;
            if (ContainsNothing || ContainsNoZ) return null;
            if (!ZCache.TryGetTarget(out result) || ((CachableTexture2D)result).BeingDisposed || result.IsDisposed)
            {
                if (this.Width == 0 || this.Height == 0)
                {
                    ContainsNothing = true;
                    return null;
                }
                if (ContainsNoZ)
                {
                    return null;
                }

                var effectiveWidth = Parent.FloorCopy > 0 ? ((Width + 1) & ~1) : Width;

                // The z texture's padding must MATCH the colour texture's: the sprite's UVs are
                // computed from the colour texture's dimensions and sample both. Mirror GetTexture's
                // effective compression decision - padding z whenever global TexCompress is on (the
                // old behavior) desynced the two grids for mipped/override sprites, shifting every
                // per-pixel depth read near the right/bottom edges.
                var zTc = FSOEnvironment.TexCompress;
                var zMip = FSOEnvironment.Enable3D && FSOEnvironment.DirectX && (FSOEnvironment.EnableNPOTMip || (effectiveWidth == 128 && Height == 64));
                if (zMip && TextureUtils.OverrideCompression(effectiveWidth, Height)) zTc = false;
                if (zTc)
                {
                    result = new CachableTexture2D(device, ((effectiveWidth+3)/4)*4, ((Height+3)/4)*4, false, SurfaceFormat.Alpha8);
                    AssetStreaming.LoadTexture(result, AssetStreamingMode.Lot,
                        () =>
                        {
                            DecodeIfRequired(true);

                            var tempZ = new byte[result.Width * result.Height];
                            var dind = 0;
                            var sind = 0;
                            for (int i = 0; i < Height; i++)
                            {
                                Array.Copy(ZBufferData, sind, tempZ, dind, effectiveWidth);
                                sind += effectiveWidth;
                                dind += result.Width;
                            }

                            if (!IffFile.RETAIN_CHUNK_DATA)
                            {
                                if (!FSOEnvironment.Enable3D) ZBufferData = null;
                            }

                            return new TextureData<byte>[] { new TextureData<byte>(0, tempZ) };
                        });
                }
                else
                {
                    result = new CachableTexture2D(device, effectiveWidth, this.Height, false, SurfaceFormat.Alpha8);

                    AssetStreaming.LoadTexture(result, AssetStreamingMode.Lot,
                        () =>
                        {
                            DecodeIfRequired(true);

                            return new TextureData<byte>[] { new TextureData<byte>(0, this.ZBufferData) };
                        });
                }
                ZCache = new WeakReference<Texture2D>(result);
                if (TimedReferenceController.CurrentType == CacheType.PERMANENT) PermaRefZ = result;
                if (!IffFile.RETAIN_CHUNK_DATA)
                {
                    //if (!FSOEnvironment.Enable3D) ZBufferData = null; disabled right now til we get a clean way of getting this post-world-texture for ultra lighting
                    if (onlyThis) PixelData = null;
                }
            }
            if (TimedReferenceController.CurrentType != CacheType.PERMANENT) TimedReferenceController.KeepAlive(result, KeepAliveType.ACCESS);
            return result;
        }

        #region IWorldTextureProvider Members

        public WorldTexture GetWorldTexture(GraphicsDevice device)
        {
            var result = new WorldTexture
            {
                Pixel = this.GetTexture(device, false)
            };
            result.ZBuffer = this.GetZTexture(device, false);
            return result;
        }

        #endregion

        public Color[] SetData(Color[] px, byte[] zpx, Rectangle rect)
        {
            PixelCache = null; //can't exactly dispose this.. it's likely still in use!
            ZCache = null;
            PixelData = px;
            ZBufferData = zpx;
            Position = new Vector2(rect.X, rect.Y);

            Width = rect.Width;
            Height = rect.Height;
            Flags = 7;
            TransparentColorIndex = 255;

			var colors = SPR2FrameEncoder.QuantizeFrame(this, out PalData);

            var palt = new Color[256];
            int i = 0;
            foreach (var c in colors)
                palt[i++] = new Color(c.R, c.G, c.B, (byte)255);

            return palt;
        }

        public void SetPalt(PALT p)
        {
            if (this.PaletteID != 0)
            {
                var old = Parent.ChunkParent.Get<PALT>(this.PaletteID);
                if (old != null) old.References--;
            }
            PaletteID = p.ChunkID;
            Palette = p;

            p.References++;
        }
    }
}
