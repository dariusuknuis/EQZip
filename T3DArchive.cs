using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EQ_Zip
{
    // Tanarus .t3d archive (simple: raw payloads, NUL-terminated names, no compression).
    public class T3DArchive : IArchive
    {
        private static readonly byte[] _Magic = { 0x02, 0x3D, 0xFF, 0xFF, 0x00, 0x57, 0x01, 0x00 };

        #region IArchive – public surface kept parallel to EQArchive
        public string FilePath { get; set; } = "";
        public string Filename
        {
            get => _Filename;
            set
            {
                if (Util.IsBlank(value)) { _Filename = "(Untitled)"; return; }
                var p = Path.GetDirectoryName(value);
                if (!string.IsNullOrEmpty(p)) FilePath = p;
                _Filename = Path.GetFileName(value);
            }
        }
        public SortedList<string, EQArchiveFile> Files { get; set; } = new SortedList<string, EQArchiveFile>();
        public bool IsDirty { get; set; }
        public uint SizeOnDisk { get; set; }
        public Result Status { get; set; } = Result.NotImplemented;
        #endregion

        private string _Filename = "(Untitled)";

        #region Load
        public static T3DArchive Load(string filePath) => Load(filePath, Util.GetFileContents(filePath));

        public static T3DArchive Load(string filename, byte[] contents)
        {
            if (Util.IsBlank(filename) || contents == null || contents.Length < 16) return null;

            using (var ms = new MemoryStream(contents, writable: false))
            using (var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true))
            {
                // 1) magic
                var magic = br.ReadBytes(_Magic.Length);
                for (int i = 0; i < _Magic.Length; i++) if (magic[i] != _Magic[i]) return null;

                var archive = new T3DArchive { Filename = filename, SizeOnDisk = (uint)contents.Length };

                try
                {
                    // 2) counts / sizes
                    uint storedCount = br.ReadUInt32();      // on-disk: count+1
                    uint count = storedCount - 1;
                    uint filenameSectionSize = br.ReadUInt32(); // not required for reading

                    // 3) table: <dataOffset:uint32, nameOffset:uint32-relative>
                    var dataOffsets = new uint[count];
                    var nameOffsetsAbs = new uint[count];

                    for (int i = 0; i < count; i++)
                    {
                        // record starts here (this is the base for the name offset)
                        uint recordBase = (uint)br.BaseStream.Position;

                        dataOffsets[i] = br.ReadUInt32();
                        uint nameOffRel = br.ReadUInt32();

                        // the reader you shared reconstructs absolute as: stored + (Position - 8)
                        // Position is now recordBase + 8, so (Position - 8) == recordBase.
                        nameOffsetsAbs[i] = nameOffRel + recordBase;
                    }

                    // 4) total file size (uint64) used to compute the last entry's size
                    ulong fileSize = br.ReadUInt64();

                    // 5) build EQArchiveFile entries (raw copy; no compression)
                    archive.Files = new SortedList<string, EQArchiveFile>((int)count);

                    for (int i = 0; i < count; i++)
                    {
                        // read NUL-terminated ASCII filename
                        br.BaseStream.Seek(nameOffsetsAbs[i], SeekOrigin.Begin);
                        var nameBytes = new List<byte>(64);
                        byte b;
                        while ((b = br.ReadByte()) != 0x00) nameBytes.Add(b);
                        string name = Encoding.ASCII.GetString(nameBytes.ToArray());

                        // compute size by diff of data offsets
                        uint start = dataOffsets[i];
                        ulong size = (i == count - 1) ? (fileSize - start) : (dataOffsets[i + 1] - start);

                        if (size > int.MaxValue) throw new Exception("Entry too large for memory buffer");

                        // copy raw bytes
                        var buf = new byte[(int)size];
                        Buffer.BlockCopy(contents, (int)start, buf, 0, (int)size);

                        var file = new EQArchiveFile { Filename = name };
                        file.SetContents(buf); // no compression; GetContents() returns buf

                        archive.Files.Add(file.Filename.ToLowerInvariant(), file);
                    }

                    archive.Status = Result.OK;
                }
                catch
                {
                    archive.Status = Result.MalformedFile;
                }

                return archive;
            }
        }

        #endregion

        #region Save – writes valid .t3d (names block + raw payloads)
        public Result Save(string filename)
        {
            this.Filename = filename;
            return Save();
        }

        public Result Save()
        {
            if (string.IsNullOrEmpty(FilePath) || Filename == "(Untitled)")
                return Result.InvalidArgument;

            try
            {
                // snapshot ordered files (SortedList is name-ordered; that’s fine)
                var entries = new List<EQArchiveFile>(Files.Values);
                int n = entries.Count;

                using (var fs = File.Create(Path.Combine(FilePath, Filename)))
                using (var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true))
                {
                    // prebuild names block
                    var nameOffsetsAbs = new uint[n];
                    using (var namesMs = new MemoryStream())
                    {
                        foreach (var f in entries)
                        {
                            nameOffsetsAbs[entries.IndexOf(f)] = 0; // fill later once base is known
                            var nameBytes = Encoding.ASCII.GetBytes(f.Filename);
                            namesMs.Write(nameBytes, 0, nameBytes.Length);
                            namesMs.WriteByte(0x00);
                        }
                        uint namesSize = (uint)namesMs.Length;

                        // compute where things will live
                        // [magic:8][count:4][nameSectSize:4][pairs: n*8][fileSize:8][names:namesSize][payloads...]
                        uint pairsSize = (uint)(n * 8);
                        uint headerBeforeNames = (uint)(8 + 4 + 4 + pairsSize + 8);
                        uint dataRegionStart = headerBeforeNames + namesSize;

                        // precompute payload offsets & sizes
                        var dataOffsets = new uint[n];
                        var sizes = new uint[n];
                        uint cursor = dataRegionStart;
                        for (int i = 0; i < n; i++)
                        {
                            dataOffsets[i] = cursor;
                            var bytes = entries[i].GetContents() ?? Array.Empty<byte>();
                            sizes[i] = (uint)bytes.Length;
                            cursor += sizes[i];
                        }
                        ulong totalFileSize = cursor;

                        // now that we know the absolute start of names block, compute absolute name offsets
                        uint namesBaseAbs = headerBeforeNames;
                        uint rolling = 0;
                        for (int i = 0; i < n; i++)
                        {
                            nameOffsetsAbs[i] = namesBaseAbs + rolling;
                            rolling += (uint)Encoding.ASCII.GetByteCount(entries[i].Filename) + 1; // + NUL
                        }

                        // 1) magic
                        bw.Write(_Magic);

                        // 2) count on disk is n + 1 (matches reader’s ReadUInt32() - 1)
                        bw.Write((uint)(n + 1));

                        // 3) filename section size
                        bw.Write(namesSize);

                        // 4) write pairs: <dataOffset, nameOffsetRelativeToRecordStart>
                        for (int i = 0; i < n; i++)
                        {
                            long recordBase = bw.BaseStream.Position; // start of this pair
                            bw.Write(dataOffsets[i]);
                            uint rel = (uint)(nameOffsetsAbs[i] - recordBase);
                            bw.Write(rel);
                        }

                        // 5) placeholder for total file size (patch later)
                        long fileSizePos = bw.BaseStream.Position;
                        bw.Write((ulong)0);

                        // 6) names block
                        namesMs.Position = 0;
                        namesMs.CopyTo(bw.BaseStream);

                        // 7) payloads (raw)
                        for (int i = 0; i < n; i++)
                        {
                            var bytes = entries[i].GetContents() ?? Array.Empty<byte>();
                            bw.Write(bytes);
                        }

                        // 8) patch file size
                        bw.BaseStream.Seek(fileSizePos, SeekOrigin.Begin);
                        bw.Write(totalFileSize);

                        bw.Flush();
                        SizeOnDisk = (uint)bw.BaseStream.Length;
                        IsDirty = false;
                        Status = Result.OK;
                        return Result.OK;
                    }
                }
            }
            catch
            {
                return Result.FileWriteError;
            }
        }

        #endregion

        #region Helpers mirroring EQArchive (so UI code works unchanged)
        public EQArchiveFile FindFile(string filename)
        {
            try { return Files[Path.GetFileName(filename).ToLowerInvariant()]; }
            catch { return null; }
        }

        public EQArchiveFile FindFileOrSimilarImage(string filename)
        {
            if (!Util.IsImage(filename)) return FindFile(filename);
            filename = Path.GetFileName(filename);
            var prefix = Path.GetFileNameWithoutExtension(filename);
            foreach (var f in Files.Values)
            {
                if (Util.IsImage(f.Filename) &&
                    prefix.Equals(Path.GetFileNameWithoutExtension(f.Filename), StringComparison.CurrentCultureIgnoreCase))
                    return f;
            }
            return null;
        }

        public Result Add(EQArchiveFile file, bool replaceSimilarImage)
        {
            if (file == null) return Result.InvalidArgument;

            var existing = replaceSimilarImage
                ? FindFileOrSimilarImage(file.Filename)
                : FindFile(file.Filename);

            if (existing != null)
            {
                Files.RemoveAt(Files.IndexOfValue(existing));
                file.Filename = existing.Filename;
            }

            Files[file.Filename.ToLowerInvariant()] = file;
            IsDirty = true;
            return file.Status;
        }

        public Result Remove(EQArchiveFile file)
        {
            if (file == null) return Result.FileNotFound;
            Files.RemoveAt(Files.IndexOfValue(file));
            IsDirty = true;
            return Result.OK;
        }

        public Result Remove(string filename) => Remove(FindFile(filename));
        #endregion
    }
}
