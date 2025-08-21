using System;

namespace EQ_Zip
{
    public static class ArchiveLoader
    {
        public static IArchive Load(string path)
        {
            var bytes = Util.GetFileContents(path);
            if (bytes == null) return null;

            // T3D magic at offset 0
            if (bytes.Length >= 8 &&
                bytes[0] == 0x02 && bytes[1] == 0x3D && bytes[2] == 0xFF && bytes[3] == 0xFF &&
                bytes[4] == 0x00 && bytes[5] == 0x57 && bytes[6] == 0x01 && bytes[7] == 0x00)
            {
                return T3DArchive.Load(path, bytes);
            }

            // PFS/S3D/EQG magic at offset 4: 'P','F','S',' ' => 0x20534650
            if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 4) == 0x20534650)
            {
                return EQArchive.Load(path, bytes);
            }

            // unknown
            return null;
        }
    }
}
