using System.Collections.Generic;

namespace EQ_Zip
{
    public interface IArchive
    {
        string FilePath { get; set; }
        string Filename { get; set; }
        SortedList<string, EQArchiveFile> Files { get; }
        bool IsDirty { get; set; }
        uint SizeOnDisk { get; set; }
        Result Status { get; set; }

        EQArchiveFile FindFile(string filename);
        EQArchiveFile FindFileOrSimilarImage(string filename);
        Result Add(EQArchiveFile file, bool replaceSimilarImage);
        Result Remove(EQArchiveFile file);
        Result Remove(string filename);
        Result Save();                 // Save to current Filename
        Result Save(string filename);  // Save As...
    }
}
