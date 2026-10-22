using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SinmaiAlpha.Artwork
{
    // GIF87a/89a, managed-only for Mono and IL2CPP. Keep compressed frames and
    // two compositing canvases, not one full-resolution texture per frame.
    // Format reference: https://www.w3.org/Graphics/GIF/spec-gif89a.txt
    internal sealed class GifAnimation
    {
        internal const int MaxFileBytes = 64 * 1024 * 1024;
        private sealed class Frame
        {
            internal int X, Y, Width, Height, Disposal, Transparent = -1, CodeSize;
            internal float Delay = .1f;
            internal bool Interlaced;
            internal byte[] Palette, Data;
        }
        private readonly List<Frame> frames = new List<Frame>();
        private readonly ushort[] prefixes = new ushort[4096];
        private readonly byte[] suffixes = new byte[4096];
        private readonly byte[] stack = new byte[4097];
        private readonly byte[] indices;
        private byte[] restore;
        private readonly byte[] background = new byte[4];
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal byte[] Pixels { get; private set; } // Unity's bottom-up RGBA layout.
        internal int FrameIndex { get; private set; } = -1;
        internal int FrameCount { get { return frames.Count; } }
        internal float Delay { get { return frames[FrameIndex].Delay; } }

        internal GifAnimation(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxFileBytes) throw new InvalidDataException("GIF exceeds 64 MiB.");
            using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
            {
                string magic = Encoding.ASCII.GetString(Read(reader, 6));
                if (magic != "GIF87a" && magic != "GIF89a") throw new InvalidDataException("Invalid GIF signature.");
                Width = reader.ReadUInt16(); Height = reader.ReadUInt16();
                if (Width == 0 || Height == 0 || Width > 4096 || Height > 4096 || (long)Width * Height > 4194304)
                    throw new InvalidDataException("GIF canvas exceeds 4096 per edge / 4 million pixels.");
                int packed = reader.ReadByte(), backgroundIndex = reader.ReadByte();
                reader.ReadByte();
                byte[] globalPalette = (packed & 128) != 0 ? Read(reader, 3 * (1 << ((packed & 7) + 1))) : null;
                int disposal = 0, transparent = -1;
                float delay = .1f;
                bool finished = false;
                while (!finished)
                {
                    int block = reader.ReadByte();
                    switch (block)
                    {
                        case 0x3b: finished = true; break;
                        case 0x21:
                            int label = reader.ReadByte();
                            if (label == 0xf9)
                            {
                                if (reader.ReadByte() != 4) throw new InvalidDataException("Invalid GIF control block.");
                                int flags = reader.ReadByte();
                                disposal = (flags >> 2) & 7;
                                int centiseconds = reader.ReadUInt16();
                                delay = centiseconds == 0 ? .1f : Math.Max(.02f, centiseconds / 100f);
                                int color = reader.ReadByte();
                                transparent = (flags & 1) != 0 ? color : -1;
                                if (reader.ReadByte() != 0 || disposal > 3) throw new InvalidDataException("Unsupported GIF control block.");
                            }
                            else
                            {
                                ReadBlocks(reader);
                                // Plain-text is a rendering block and consumes its GCE.
                                if (label == 1) { disposal = 0; transparent = -1; delay = .1f; }
                            }
                            break;
                        case 0x2c:
                            var frame = new Frame { X = reader.ReadUInt16(), Y = reader.ReadUInt16(),
                                Width = reader.ReadUInt16(), Height = reader.ReadUInt16(),
                                Disposal = disposal, Transparent = transparent, Delay = delay };
                            if (frame.Width == 0 || frame.Height == 0 || frame.X + frame.Width > Width || frame.Y + frame.Height > Height)
                                throw new InvalidDataException("GIF frame lies outside canvas.");
                            int imageFlags = reader.ReadByte();
                            frame.Interlaced = (imageFlags & 64) != 0;
                            frame.Palette = (imageFlags & 128) != 0 ? Read(reader, 3 * (1 << ((imageFlags & 7) + 1))) : globalPalette;
                            if (frame.Palette == null) throw new InvalidDataException("Missing GIF color table.");
                            frame.CodeSize = reader.ReadByte();
                            if (frame.CodeSize < 2 || frame.CodeSize > 8) throw new InvalidDataException("Invalid GIF LZW size.");
                            frame.Data = ReadBlocks(reader);
                            frames.Add(frame);
                            if (frames.Count > 4096) throw new InvalidDataException("GIF exceeds 4096 frames.");
                            disposal = 0; transparent = -1; delay = .1f;
                            break;
                        default: throw new InvalidDataException("Unknown GIF block.");
                    }
                }
                if (frames.Count == 0) throw new InvalidDataException("GIF has no image frames.");
                // Transparent artwork keeps its transparent canvas. Opaque GIFs
                // restore the logical-screen background color for disposal 2.
                if (frames[0].Transparent < 0 && globalPalette != null && backgroundIndex * 3 + 2 < globalPalette.Length)
                {
                    Array.Copy(globalPalette, backgroundIndex * 3, background, 0, 3); background[3] = 255;
                }
            }
            Pixels = new byte[Width * Height * 4];
            indices = new byte[Width * Height];
            Advance();
        }

        internal void Advance()
        {
            if (FrameIndex >= 0)
            {
                Frame previous = frames[FrameIndex];
                if (previous.Disposal == 2) Clear(previous.X, previous.Y, previous.Width, previous.Height, previous.Transparent >= 0);
                else if (previous.Disposal == 3 && restore != null) Buffer.BlockCopy(restore, 0, Pixels, 0, Pixels.Length);
            }
            FrameIndex = (FrameIndex + 1) % frames.Count;
            if (FrameIndex == 0) Clear(0, 0, Width, Height, frames[0].Transparent >= 0);
            Frame frame = frames[FrameIndex];
            if (frame.Disposal == 3)
            {
                if (restore == null) restore = new byte[Pixels.Length];
                Buffer.BlockCopy(Pixels, 0, restore, 0, Pixels.Length);
            }
            Decode(frame);
            int sourceRow = 0;
            if (frame.Interlaced)
            {
                DrawPass(frame, 0, 8, ref sourceRow); DrawPass(frame, 4, 8, ref sourceRow);
                DrawPass(frame, 2, 4, ref sourceRow); DrawPass(frame, 1, 2, ref sourceRow);
            }
            else DrawPass(frame, 0, 1, ref sourceRow);
        }

        private void Clear(int x, int y, int width, int height, bool transparent)
        {
            for (int row = y; row < y + height; row++)
                for (int col = x; col < x + width; col++)
                {
                    int offset = ((Height - 1 - row) * Width + col) * 4;
                    for (int c = 0; c < 4; c++) Pixels[offset + c] = transparent ? (byte)0 : background[c];
                }
        }

        private void DrawPass(Frame frame, int start, int step, ref int sourceRow)
        {
            for (int y = start; y < frame.Height; y += step, sourceRow++)
                for (int x = 0; x < frame.Width; x++)
                {
                    int color = indices[sourceRow * frame.Width + x];
                    if (color == frame.Transparent) continue;
                    int palette = color * 3;
                    if (palette + 2 >= frame.Palette.Length) throw new InvalidDataException("GIF color outside palette.");
                    int dest = ((Height - 1 - frame.Y - y) * Width + frame.X + x) * 4;
                    Pixels[dest] = frame.Palette[palette]; Pixels[dest + 1] = frame.Palette[palette + 1];
                    Pixels[dest + 2] = frame.Palette[palette + 2]; Pixels[dest + 3] = 255;
                }
        }

        private void Decode(Frame frame)
        {
            int clear = 1 << frame.CodeSize, end = clear + 1, next = end + 1;
            int size = frame.CodeSize + 1, previous = -1, first = 0, bit = 0, output = 0;
            int expected = frame.Width * frame.Height;
            for (int i = 0; i < clear; i++) suffixes[i] = (byte)i;
            bool ended = false;
            while (bit + size <= frame.Data.Length * 8)
            {
                int code = 0;
                for (int i = 0; i < size; i++, bit++) code |= ((frame.Data[bit >> 3] >> (bit & 7)) & 1) << i;
                if (code == clear) { size = frame.CodeSize + 1; next = end + 1; previous = -1; continue; }
                if (code == end) { ended = true; break; }
                int inputCode = code, count = 0;
                if (code >= next)
                {
                    if (code != next || previous < 0) throw new InvalidDataException("Invalid GIF LZW code.");
                    stack[count++] = (byte)first; code = previous;
                }
                while (code >= clear)
                {
                    if (code >= next || count >= 4096) throw new InvalidDataException("Invalid GIF LZW chain.");
                    stack[count++] = suffixes[code]; code = prefixes[code];
                }
                first = suffixes[code]; stack[count++] = (byte)first;
                if (output + count > expected) throw new InvalidDataException("Too many GIF pixels.");
                while (count > 0) indices[output++] = stack[--count];
                if (previous >= 0 && next < 4096)
                {
                    prefixes[next] = (ushort)previous; suffixes[next++] = (byte)first;
                    if (next == (1 << size) && size < 12) size++;
                }
                previous = inputCode;
            }
            if (!ended || output != expected) throw new InvalidDataException("Truncated GIF image.");
        }

        private static byte[] Read(BinaryReader reader, int count)
        {
            byte[] data = reader.ReadBytes(count);
            if (data.Length != count) throw new EndOfStreamException("Truncated GIF.");
            return data;
        }
        private static byte[] ReadBlocks(BinaryReader reader)
        {
            using (var data = new MemoryStream())
            {
                int size;
                while ((size = reader.ReadByte()) != 0) { byte[] block = Read(reader, size); data.Write(block, 0, block.Length); }
                return data.ToArray();
            }
        }
    }
}
