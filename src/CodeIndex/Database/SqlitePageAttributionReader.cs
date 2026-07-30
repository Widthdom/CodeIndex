using System.Buffers.Binary;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class SqlitePageAttributionReader
{
    private const long MaxWalFrameCount = 1_000_000;

    internal sealed record ObjectSize(
        string Name,
        string ObjectType,
        bool NameRedactedOrTruncated,
        long PageBytes,
        long PayloadBytes,
        long UnusedBytes);

    internal sealed record Result(
        long ObjectCount,
        long AllocatedObjectBytes,
        long TableBytes,
        long IndexBytes,
        long OtherObjectBytes,
        long InternalPageBytes,
        long LeafPageBytes,
        long OverflowPageBytes,
        long OtherPageBytes,
        long PayloadBytes,
        long UnusedBytes,
        IReadOnlyList<ObjectSize> TopObjects);

    internal static Result Read(
        SqliteConnection connection,
        string databasePath,
        long pageCount,
        long pageSize,
        CancellationToken cancellationToken)
    {
        if (pageCount is < 0 or > DbReader.DatabaseSizeAttributionPageLimit)
            throw new InvalidDataException("SQLite page count exceeds the bounded attribution scan.");
        if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0)
            throw new InvalidDataException("SQLite page size is invalid.");

        using var source = new PageSource(
            databasePath,
            pageCount,
            checked((int)pageSize),
            cancellationToken);
        var visitedPages = new HashSet<long>();
        var aggregate = new MutableObjectSize();
        var topObjects = new List<ObjectSize>(DbReader.DatabaseSizeAttributionTopObjectLimit);
        long objectCount = 0;

        AddObject(
            "sqlite_schema",
            "table",
            rootPage: 1,
            source,
            pageCount,
            pageSize,
            visitedPages,
            aggregate,
            topObjects,
            ref objectCount,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type, rootpage
            FROM sqlite_schema
            WHERE type IN ('table', 'index')
              AND rootpage > 0
            ORDER BY name COLLATE BINARY
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                source,
                pageCount,
                pageSize,
                visitedPages,
                aggregate,
                topObjects,
                ref objectCount,
                cancellationToken);
        }

        topObjects.Sort(CompareObjectSizes);
        return new Result(
            objectCount,
            aggregate.PageBytes,
            aggregate.TableBytes,
            aggregate.IndexBytes,
            aggregate.OtherObjectBytes,
            aggregate.InternalPageBytes,
            aggregate.LeafPageBytes,
            aggregate.OverflowPageBytes,
            aggregate.OtherPageBytes,
            aggregate.PayloadBytes,
            aggregate.UnusedBytes,
            topObjects);
    }

    internal static Result ReadConnectionSnapshot(
        SqliteConnection connection,
        long pageCount,
        long pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotDirectory = DataDirectorySecurity.CreateSensitiveTempDirectory(
            "cdidx-attribution-snapshot-");
        var snapshotPath = Path.Combine(snapshotDirectory.FullName, "snapshot.db");
        try
        {
            using (var destination = new SqliteConnection(
                       new SqliteConnectionStringBuilder
                       {
                           DataSource = snapshotPath,
                           Mode = SqliteOpenMode.ReadWriteCreate,
                           Pooling = false,
                       }.ToString()))
            {
                destination.Open();
                DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
                CopyConnectionSnapshot(
                    connection,
                    destination,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Read(
                connection,
                snapshotPath,
                pageCount,
                pageSize,
                cancellationToken);
        }
        finally
        {
            TryDeleteConnectionSnapshot(snapshotDirectory.FullName, snapshotPath);
        }
    }

    private static void CopyConnectionSnapshot(
        SqliteConnection source,
        SqliteConnection destination,
        CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(
                static state =>
                {
                    var connections = ((SqliteConnection Source, SqliteConnection Destination))state!;
                    SQLitePCL.raw.sqlite3_interrupt(connections.Source.Handle);
                    SQLitePCL.raw.sqlite3_interrupt(connections.Destination.Handle);
                },
                (source, destination))
            : default;
        using var backup = SQLitePCL.raw.sqlite3_backup_init(
            destination.Handle,
            "main",
            source.Handle,
            "main");
        if (backup.IsInvalid)
            throw new InvalidDataException("SQLite snapshot backup could not be initialized.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = SQLitePCL.raw.sqlite3_backup_step(backup, 256);
            if (result == SQLitePCL.raw.SQLITE_DONE)
                break;
            if (result != SQLitePCL.raw.SQLITE_OK)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidDataException(
                    $"SQLite snapshot backup failed with result code {result}.");
            }
        }

        var finishResult = SQLitePCL.raw.sqlite3_backup_finish(backup);
        if (finishResult != SQLitePCL.raw.SQLITE_OK)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException(
                $"SQLite snapshot backup finalization failed with result code {finishResult}.");
        }
    }

    private static void TryDeleteConnectionSnapshot(
        string snapshotDirectory,
        string snapshotPath)
    {
        try
        {
            foreach (var path in new[]
                     {
                         snapshotPath,
                         snapshotPath + "-journal",
                         snapshotPath + "-wal",
                         snapshotPath + "-shm",
                     })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

            if (Directory.Exists(snapshotDirectory))
                Directory.Delete(snapshotDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GlobalToolLog.Error(
                $"database_attribution_snapshot_cleanup_failed error={ex.GetType().Name}");
        }
    }

    private static void AddObject(
        string rawName,
        string objectType,
        long rootPage,
        PageSource source,
        long pageCount,
        long pageSize,
        HashSet<long> visitedPages,
        MutableObjectSize aggregate,
        List<ObjectSize> topObjects,
        ref long objectCount,
        CancellationToken cancellationToken)
    {
        objectCount = checked(objectCount + 1);
        if (objectCount > DbReader.DatabaseSizeAttributionObjectLimit)
            throw new InvalidDataException("SQLite object count exceeds the bounded attribution scan.");

        var size = ReadObject(
            rootPage,
            source,
            pageCount,
            pageSize,
            visitedPages,
            cancellationToken);
        aggregate.Add(size, objectType);

        var safeName = DiagnosticSanitizer.ForMessage(
            rawName,
            DbReader.DatabaseSizeAttributionObjectNameLimit - 3);
        topObjects.Add(new ObjectSize(
            safeName,
            NormalizeObjectType(objectType),
            !string.Equals(rawName, safeName, StringComparison.Ordinal),
            size.PageBytes,
            size.PayloadBytes,
            size.UnusedBytes));
        topObjects.Sort(CompareObjectSizes);
        if (topObjects.Count > DbReader.DatabaseSizeAttributionTopObjectLimit)
            topObjects.RemoveAt(topObjects.Count - 1);
    }

    private static MutableObjectSize ReadObject(
        long rootPage,
        PageSource source,
        long pageCount,
        long pageSize,
        HashSet<long> visitedPages,
        CancellationToken cancellationToken)
    {
        var size = new MutableObjectSize();
        var stack = new Stack<long>();
        var page = new byte[checked((int)pageSize)];
        var overflowPageBuffer = new byte[checked((int)pageSize)];
        stack.Push(rootPage);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = stack.Pop();
            AddVisitedPage(pageNumber, pageCount, visitedPages);
            source.ReadPage(pageNumber, page);

            var headerOffset = pageNumber == 1 ? 100 : 0;
            if (headerOffset + 12 > source.UsableSize)
                throw new InvalidDataException("SQLite b-tree page header is outside the usable page.");

            var pageType = page[headerOffset];
            var interior = pageType is 2 or 5;
            var leaf = pageType is 10 or 13;
            if (!interior && !leaf)
                throw new InvalidDataException("SQLite b-tree page type is invalid.");

            size.PageBytes = checked(size.PageBytes + pageSize);
            if (interior)
                size.InternalPageBytes = checked(size.InternalPageBytes + pageSize);
            else
                size.LeafPageBytes = checked(size.LeafPageBytes + pageSize);

            var headerSize = interior ? 12 : 8;
            var cellCount = ReadUInt16(page, headerOffset + 3);
            var cellPointerEnd = checked(headerOffset + headerSize + (cellCount * 2));
            if (cellPointerEnd > source.UsableSize)
                throw new InvalidDataException("SQLite cell pointer array is outside the usable page.");

            var cellContentStart = ReadUInt16(page, headerOffset + 5);
            if (cellContentStart == 0 && pageSize == 65536)
                cellContentStart = 65536;
            if (cellContentStart < cellPointerEnd || cellContentStart > source.UsableSize)
                throw new InvalidDataException("SQLite cell content boundary is invalid.");

            var freeblockBytes = ReadFreeblockBytes(
                page,
                ReadUInt16(page, headerOffset + 1),
                source.UsableSize);
            var fragmentedBytes = page[headerOffset + 7];
            size.UnusedBytes = checked(
                size.UnusedBytes
                + (cellContentStart - cellPointerEnd)
                + freeblockBytes
                + fragmentedBytes);

            List<long>? childPages = interior ? new List<long>(cellCount + 1) : null;

            for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                var pointerOffset = checked(headerOffset + headerSize + (cellIndex * 2));
                var cellOffset = ReadUInt16(page, pointerOffset);
                if (cellOffset < cellContentStart || cellOffset >= source.UsableSize)
                    throw new InvalidDataException("SQLite cell offset is invalid.");

                var cursor = cellOffset;
                if (interior)
                {
                    childPages!.Add(ReadPageNumber(page, cursor, pageCount));
                    cursor = checked(cursor + 4);
                }

                if (pageType == 5)
                    continue;

                var payloadBytes = ReadVarint(page, cursor, source.UsableSize, out var payloadVarintBytes);
                cursor = checked(cursor + payloadVarintBytes);
                if (pageType == 13)
                {
                    _ = ReadVarint(page, cursor, source.UsableSize, out var rowIdVarintBytes);
                    cursor = checked(cursor + rowIdVarintBytes);
                }

                if (payloadBytes > checked(pageCount * source.UsableSize))
                    throw new InvalidDataException("SQLite cell payload exceeds the bounded database size.");

                size.PayloadBytes = checked(size.PayloadBytes + payloadBytes);
                var localPayloadBytes = ComputeLocalPayloadBytes(
                    payloadBytes,
                    source.UsableSize,
                    tableLeaf: pageType == 13);
                var overflowPointerOffset = checked(cursor + checked((int)localPayloadBytes));
                if (overflowPointerOffset > source.UsableSize)
                    throw new InvalidDataException("SQLite local payload exceeds the usable page.");

                if (payloadBytes <= localPayloadBytes)
                    continue;
                if (overflowPointerOffset + 4 > source.UsableSize)
                    throw new InvalidDataException("SQLite overflow pointer is outside the usable page.");

                var overflowPage = ReadPageNumber(page, overflowPointerOffset, pageCount);
                ReadOverflowChain(
                    overflowPage,
                    payloadBytes - localPayloadBytes,
                    source,
                    pageCount,
                    pageSize,
                    visitedPages,
                    size,
                    overflowPageBuffer,
                    cancellationToken);
            }

            if (childPages != null)
            {
                childPages.Add(ReadPageNumber(page, headerOffset + 8, pageCount));
                for (var childIndex = childPages.Count - 1; childIndex >= 0; childIndex--)
                    stack.Push(childPages[childIndex]);
            }
        }

        return size;
    }

    private static void ReadOverflowChain(
        long firstPage,
        long payloadBytes,
        PageSource source,
        long pageCount,
        long pageSize,
        HashSet<long> visitedPages,
        MutableObjectSize size,
        byte[] page,
        CancellationToken cancellationToken)
    {
        var pageNumber = firstPage;
        var remainingPayloadBytes = payloadBytes;
        var overflowPayloadCapacity = source.UsableSize - 4L;
        if (overflowPayloadCapacity <= 0)
            throw new InvalidDataException("SQLite overflow payload capacity is invalid.");

        while (remainingPayloadBytes > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddVisitedPage(pageNumber, pageCount, visitedPages);
            source.ReadPage(pageNumber, page);
            size.PageBytes = checked(size.PageBytes + pageSize);
            size.OverflowPageBytes = checked(size.OverflowPageBytes + pageSize);

            var usedBytes = Math.Min(remainingPayloadBytes, overflowPayloadCapacity);
            size.UnusedBytes = checked(
                size.UnusedBytes
                + (overflowPayloadCapacity - usedBytes));
            remainingPayloadBytes -= usedBytes;

            var nextPage = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(0, 4));
            if (remainingPayloadBytes == 0)
            {
                if (nextPage != 0)
                    throw new InvalidDataException("SQLite overflow chain exceeds the declared payload.");
                break;
            }

            if (nextPage == 0)
                throw new InvalidDataException("SQLite overflow chain ended before the declared payload.");
            pageNumber = nextPage;
        }
    }

    private static long ComputeLocalPayloadBytes(long payloadBytes, int usableSize, bool tableLeaf)
    {
        var maxLocal = tableLeaf
            ? usableSize - 35L
            : ((usableSize - 12L) * 64 / 255) - 23;
        if (payloadBytes <= maxLocal)
            return payloadBytes;

        var minLocal = ((usableSize - 12L) * 32 / 255) - 23;
        var candidate = minLocal + ((payloadBytes - minLocal) % (usableSize - 4L));
        return candidate <= maxLocal ? candidate : minLocal;
    }

    private static long ReadVarint(
        byte[] page,
        int offset,
        int usableSize,
        out int bytesRead)
    {
        ulong value = 0;
        for (var index = 0; index < 9; index++)
        {
            var position = checked(offset + index);
            if (position >= usableSize)
                throw new InvalidDataException("SQLite varint exceeds the usable page.");

            var current = page[position];
            if (index == 8)
            {
                value = (value << 8) | current;
                bytesRead = 9;
                return value <= long.MaxValue
                    ? checked((long)value)
                    : throw new InvalidDataException("SQLite varint exceeds Int64.");
            }

            value = (value << 7) | (uint)(current & 0x7f);
            if ((current & 0x80) == 0)
            {
                bytesRead = index + 1;
                return checked((long)value);
            }
        }

        throw new InvalidDataException("SQLite varint is invalid.");
    }

    private static int ReadFreeblockBytes(byte[] page, int firstOffset, int usableSize)
    {
        var total = 0;
        var offset = firstOffset;
        var previousOffset = 0;
        while (offset != 0)
        {
            if (offset <= previousOffset
                || offset + 4 > usableSize)
            {
                throw new InvalidDataException("SQLite freeblock chain is invalid.");
            }

            var nextOffset = ReadUInt16(page, offset);
            var blockSize = ReadUInt16(page, offset + 2);
            if (blockSize < 4 || offset + blockSize > usableSize)
                throw new InvalidDataException("SQLite freeblock size is invalid.");
            total = checked(total + blockSize);
            previousOffset = offset;
            offset = nextOffset;
        }

        return total;
    }

    private static int ReadUInt16(byte[] page, int offset)
    {
        if (offset < 0 || offset + 2 > page.Length)
            throw new InvalidDataException("SQLite UInt16 field is outside the page.");
        return BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(offset, 2));
    }

    private static long ReadPageNumber(byte[] page, int offset, long pageCount)
    {
        if (offset < 0 || offset + 4 > page.Length)
            throw new InvalidDataException("SQLite page number is outside the page.");
        var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset, 4));
        if (pageNumber == 0 || pageNumber > pageCount)
            throw new InvalidDataException("SQLite page number is outside the database.");
        return pageNumber;
    }

    private static void AddVisitedPage(
        long pageNumber,
        long pageCount,
        HashSet<long> visitedPages)
    {
        if (pageNumber <= 0 || pageNumber > pageCount)
            throw new InvalidDataException("SQLite page number is outside the database.");
        if (!visitedPages.Add(pageNumber))
            throw new InvalidDataException("SQLite page is attributed more than once.");
        if (visitedPages.Count > DbReader.DatabaseSizeAttributionPageLimit)
            throw new InvalidDataException("SQLite attribution scan exceeded its page limit.");
    }

    private static int CompareObjectSizes(ObjectSize left, ObjectSize right)
    {
        var sizeComparison = right.PageBytes.CompareTo(left.PageBytes);
        return sizeComparison != 0
            ? sizeComparison
            : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    private static string NormalizeObjectType(string objectType)
        => objectType switch
        {
            "table" => "table",
            "index" => "index",
            _ => "other",
        };

    private sealed class MutableObjectSize
    {
        public long PageBytes { get; set; }
        public long PayloadBytes { get; set; }
        public long UnusedBytes { get; set; }
        public long InternalPageBytes { get; set; }
        public long LeafPageBytes { get; set; }
        public long OverflowPageBytes { get; set; }
        public long OtherPageBytes { get; set; }
        public long TableBytes { get; set; }
        public long IndexBytes { get; set; }
        public long OtherObjectBytes { get; set; }

        public void Add(MutableObjectSize value, string objectType)
        {
            PageBytes = checked(PageBytes + value.PageBytes);
            PayloadBytes = checked(PayloadBytes + value.PayloadBytes);
            UnusedBytes = checked(UnusedBytes + value.UnusedBytes);
            InternalPageBytes = checked(InternalPageBytes + value.InternalPageBytes);
            LeafPageBytes = checked(LeafPageBytes + value.LeafPageBytes);
            OverflowPageBytes = checked(OverflowPageBytes + value.OverflowPageBytes);
            OtherPageBytes = checked(OtherPageBytes + value.OtherPageBytes);
            switch (NormalizeObjectType(objectType))
            {
                case "table":
                    TableBytes = checked(TableBytes + value.PageBytes);
                    break;
                case "index":
                    IndexBytes = checked(IndexBytes + value.PageBytes);
                    break;
                default:
                    OtherObjectBytes = checked(OtherObjectBytes + value.PageBytes);
                    break;
            }
        }
    }

    private sealed class PageSource : IDisposable
    {
        private const uint WalMagicLittleEndianChecksums = 0x377f0682;
        private const uint WalMagicBigEndianChecksums = 0x377f0683;
        private const int WalHeaderSize = 32;
        private const int WalFrameHeaderSize = 24;

        private readonly FileStream _main;
        private readonly FileStream? _wal;
        private readonly Dictionary<long, long> _walPageOffsets;
        private readonly int _pageSize;

        public PageSource(
            string databasePath,
            long pageCount,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pageSize = pageSize;
            var main = OpenReadStream(databasePath);
            FileStream? wal = null;
            try
            {
                ValidateDatabaseHeader(main, pageSize);
                var walOverlay = OpenWalOverlay(
                    databasePath,
                    pageCount,
                    pageSize,
                    cancellationToken);
                wal = walOverlay.Stream;
                var usableSize = ReadUsableSize(main, pageSize);
                _main = main;
                _wal = wal;
                _walPageOffsets = walOverlay.PageOffsets;
                UsableSize = usableSize;
            }
            catch
            {
                wal?.Dispose();
                main.Dispose();
                throw;
            }
        }

        public int UsableSize { get; }

        public void ReadPage(long pageNumber, byte[] destination)
        {
            if (destination.Length != _pageSize)
                throw new ArgumentException("SQLite page buffer size does not match.", nameof(destination));

            if (_wal != null && _walPageOffsets.TryGetValue(pageNumber, out var walOffset))
            {
                ReadExactly(_wal, walOffset, destination);
                return;
            }

            ReadExactly(_main, checked((pageNumber - 1) * _pageSize), destination);
        }

        public void Dispose()
        {
            _wal?.Dispose();
            _main.Dispose();
        }

        private static FileStream OpenReadStream(string path)
            => new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);

        private static void ValidateDatabaseHeader(FileStream stream, int expectedPageSize)
        {
            var header = new byte[100];
            ReadExactly(stream, 0, header);
            if (!header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidDataException("SQLite database header is invalid.");

            var encodedPageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
            var pageSize = encodedPageSize == 1 ? 65536 : encodedPageSize;
            if (pageSize != expectedPageSize)
                throw new InvalidDataException("SQLite file and PRAGMA page sizes differ.");
        }

        private static int ReadUsableSize(FileStream stream, int pageSize)
        {
            var header = new byte[21];
            ReadExactly(stream, 0, header);
            var reservedBytes = header[20];
            var usableSize = pageSize - reservedBytes;
            return usableSize >= 480
                ? usableSize
                : throw new InvalidDataException("SQLite usable page size is invalid.");
        }

        private static (FileStream? Stream, Dictionary<long, long> PageOffsets) OpenWalOverlay(
            string databasePath,
            long pageCount,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var walPath = databasePath + "-wal";
            if (!File.Exists(walPath))
                return (null, []);

            var wal = OpenReadStream(walPath);
            try
            {
                if (wal.Length < WalHeaderSize)
                {
                    wal.Dispose();
                    return (null, []);
                }

                var header = new byte[WalHeaderSize];
                ReadExactly(wal, 0, header);
                var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                if (magic is not WalMagicBigEndianChecksums and not WalMagicLittleEndianChecksums)
                    throw new InvalidDataException("SQLite WAL header magic is invalid.");
                var checksumWordsAreLittleEndian = magic == WalMagicLittleEndianChecksums;
                uint checksum1 = 0;
                uint checksum2 = 0;
                AccumulateWalChecksum(
                    header.AsSpan(0, 24),
                    checksumWordsAreLittleEndian,
                    ref checksum1,
                    ref checksum2);
                if (checksum1 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4))
                    || checksum2 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4)))
                {
                    throw new InvalidDataException("SQLite WAL header checksum is invalid.");
                }

                var walPageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
                if (walPageSize != pageSize)
                    throw new InvalidDataException("SQLite WAL and database page sizes differ.");

                var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
                var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
                var frameSize = checked(WalFrameHeaderSize + pageSize);
                var frameCount = (wal.Length - WalHeaderSize) / frameSize;
                if (frameCount > MaxWalFrameCount)
                    throw new InvalidDataException("SQLite WAL exceeds the bounded attribution scan.");

                var frameHeader = new byte[WalFrameHeaderSize];
                var framePage = new byte[pageSize];
                long lastCommitFrame = -1;
                long lastCommitPageCount = -1;
                for (long frame = 0; frame < frameCount; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frameOffset = checked(WalHeaderSize + (frame * frameSize));
                    ReadExactly(wal, frameOffset, frameHeader);
                    var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8, 4));
                    var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12, 4));
                    if (frameSalt1 != salt1 || frameSalt2 != salt2)
                        break;

                    ReadExactly(wal, checked(frameOffset + WalFrameHeaderSize), framePage);
                    AccumulateWalChecksum(
                        frameHeader.AsSpan(0, 8),
                        checksumWordsAreLittleEndian,
                        ref checksum1,
                        ref checksum2);
                    AccumulateWalChecksum(
                        framePage,
                        checksumWordsAreLittleEndian,
                        ref checksum1,
                        ref checksum2);
                    if (checksum1 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(16, 4))
                        || checksum2 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(20, 4)))
                    {
                        break;
                    }

                    var commitPageCount = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4, 4));
                    if (commitPageCount > 0)
                    {
                        lastCommitFrame = frame;
                        lastCommitPageCount = commitPageCount;
                    }
                }

                if (lastCommitFrame < 0)
                {
                    wal.Dispose();
                    return (null, []);
                }
                if (lastCommitPageCount != pageCount)
                    throw new InvalidDataException("SQLite WAL commit and PRAGMA page counts differ.");

                var pageOffsets = new Dictionary<long, long>();
                for (long frame = 0; frame <= lastCommitFrame; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frameOffset = checked(WalHeaderSize + (frame * frameSize));
                    ReadExactly(wal, frameOffset, frameHeader);
                    var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(0, 4));
                    if (pageNumber == 0)
                        throw new InvalidDataException("SQLite WAL frame page number is invalid.");
                    if (pageNumber > pageCount)
                        continue;
                    pageOffsets[pageNumber] = checked(frameOffset + WalFrameHeaderSize);
                }

                return (wal, pageOffsets);
            }
            catch
            {
                wal.Dispose();
                throw;
            }
        }

        private static void AccumulateWalChecksum(
            ReadOnlySpan<byte> bytes,
            bool littleEndianWords,
            ref uint checksum1,
            ref uint checksum2)
        {
            if ((bytes.Length & 7) != 0)
                throw new InvalidDataException("SQLite WAL checksum input is not word-paired.");

            for (var offset = 0; offset < bytes.Length; offset += 8)
            {
                var first = littleEndianWords
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
                var second = littleEndianWords
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4));
                unchecked
                {
                    checksum1 += first + checksum2;
                    checksum2 += second + checksum1;
                }
            }
        }

        private static void ReadExactly(FileStream stream, long offset, byte[] destination)
        {
            stream.Position = offset;
            stream.ReadExactly(destination);
        }
    }
}
