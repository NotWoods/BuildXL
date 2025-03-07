// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Native.IO;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Storage.FileContentTableAccessor;
using BuildXL.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Tracing;
using Microsoft.Win32.SafeHandles;

namespace BuildXL.Storage
{
    /// <summary>
    /// A <see cref="FileContentTable" /> provides a durable (disk-persisted) cached mapping of files to their content hashes.
    /// This class is thread-safe, but concurrent calls to <see cref="RecordContentHash(FileStream, ContentHash, bool?)"/>
    /// or <see cref="TryGetKnownContentHash(FileStream)"/>
    /// for the same file may fail due to file locking.
    /// </summary>
    /// <remarks>
    /// This table functions by querying the versioned file identity from the OS. For Windows, this table queries the the NTFS / ReFS change journal records for requested files. 
    /// This requires that the change journal is enabled for the volume containing each file.
    /// In the event that the change journal is disabled (or has only recently been enabled), this implementation will fail to store or retrieve any entries.
    /// Each table entry is a mapping of file identity -> (last seen version, last seen content hash). The last seen content hash is
    /// invalidated if the last seen version does not match the current one.
    /// Note that the file identity for a path must be obtained by actually opening a handle for that path. This is not wasted work,
    /// since that handle is also needed to obtain the current version.
    /// (that's the same operation, in fact).
    /// For Windows, the file identity is a tuple of volume ID and file ID, and, for Unix, it is a tuple of device ID and inode.
    /// For Windows, the file version is the USN obtained from the change journal, while for Unix, it is the file timestamp.
    /// This table has a simple TTL based eviction policy; if an entry is not accessed within some N save / load roundtrips, it is evicted.
    /// 
    /// Windows only:
    /// In the future, most of those handle creations can be avoided by reading the change journal itself (rather than per-file
    /// current USNs) from some start cursor. This would require co-operation
    /// from the caller; the caller would ideally hash and record files with changes and visit relatively few (related) files
    /// in this table.
    /// Alternatively this table could be paired with a persisted <see cref="PathTable" /> in which case queries could be made
    /// directly by path; that would add quite a bit of complexity,
    /// in particular because renames, creates, and deletes would have to be effectively replayed from the journal (to kill
    /// stale absolute path -> (file ID, USN, hash) mappings).
    /// See http://msdn.microsoft.com/en-us/library/windows/desktop/aa363803(v=vs.85).aspx
    /// </remarks>
    public sealed class FileContentTable : IFileChangeTrackingObserver
    {
        private static readonly FileEnvelope s_fileEnvelope = new FileEnvelope(name: "FileContentTable." + ContentHashingUtilities.HashInfo.Name, version: 19);

        /// <summary>
        /// Default time-to-live (TTL) for new entries to a <see cref="FileContentTable"/>.
        /// The TTL of an entry is the number of save / load round-trips until eviction (assuming it is not accessed within that time).
        /// </summary>
        public const ushort DefaultTimeToLive = byte.MaxValue;

        /// <summary>
        /// These are the (global file ID) -> (USN, hash) mappings recorded or retrieved in this session.
        /// </summary>
        private readonly ConcurrentDictionary<FileIdAndVolumeId, Entry> m_entries = new ConcurrentDictionary<FileIdAndVolumeId, Entry>();

        /// <summary>
        /// In the event a volume has a disabled change journal or not all files have USNs (journal enabled after last write to a
        /// file),
        /// we emit a warning about perf. Rather than spamming this warning possibly for every file, we emit it at most once via
        /// this latching flag.
        /// </summary>
        /// <remarks>
        /// This is a bool in disguise. We want to use <see cref="Interlocked.CompareExchange(ref int,int,int)" />
        /// </remarks>
        private int m_changeJournalWarningLogged = 0;

        /// <summary>
        /// Counters for <see cref="FileContentTable"/>.
        /// </summary>
        public CounterCollection<FileContentTableCounters> Counters { get; } = new CounterCollection<FileContentTableCounters>();

        private readonly ObserverData m_observerData = new ObserverData();

        private readonly LoggingContext m_loggingContext;

        /// <summary>
        /// Creates a table that can durably store file -> content hash mappings. The table is initially empty.
        /// </summary>
        private FileContentTable(LoggingContext loggingContext, bool isStub = false, ushort entryTimeToLive = DefaultTimeToLive)
        {
            Contract.Requires(entryTimeToLive > 0);
            m_loggingContext = loggingContext;
            IsStub = isStub;
            EntryTimeToLive = entryTimeToLive;
        }

        /// <summary>
        /// Creates a <see cref="FileContentTable"/> which is permanently empty. The table acts as if all change journals are disabled,
        /// but does not log a user-facing warning about misconfiguration.
        /// </summary>
        public static FileContentTable CreateStub(LoggingContext loggingContext)
        {
            return new FileContentTable(loggingContext, isStub: true);
        }

        /// <summary>
        /// Creates a new instance of <see cref="FileContentTable"/>.
        /// </summary>
        public static FileContentTable CreateNew(LoggingContext loggingContext, ushort entryTimeToLive = DefaultTimeToLive)
        {
            return new FileContentTable(loggingContext, isStub: false, entryTimeToLive: entryTimeToLive);
        }

        /// <summary>
        /// Creates a new <see cref="FileContentTable"/> from another instance, with the new configuration.
        /// This essentially mimics saving the FileContentTable and creating a new one from that file,
        /// in the sense that TTLs and evictions are affected as if that were the case.
        /// </summary>
        /// <remarks>
        /// </remarks>
        public static FileContentTable CreateFromTable(FileContentTable other, LoggingContext loggingContext, ushort? newEntryTimeToLive = null)
        {
            Contract.Requires(other != null);

            var sw = Stopwatch.StartNew();
            var fct = new FileContentTable(other, loggingContext, newEntryTimeToLive ?? other.EntryTimeToLive);

            fct.Counters.AddToCounter(FileContentTableCounters.LoadDuration, sw.Elapsed);
            fct.Counters.AddToCounter(FileContentTableCounters.NumEntries, fct.Count);

            return fct;
        }

        private FileContentTable(FileContentTable other, LoggingContext loggingContext, ushort entryTimeToLive = DefaultTimeToLive)
        {
            Contract.Requires(other != null);
            Contract.Requires(entryTimeToLive > 0);

            m_loggingContext = loggingContext;
            IsStub = other.IsStub;
            EntryTimeToLive = entryTimeToLive;

            // Copy entries, discarding the stale ones and decrementing TTLs
            foreach (KeyValuePair<FileIdAndVolumeId, Entry> kvp in other.m_entries)
            {
                var entry = kvp.Value;
                if (entry.TimeToLive > 0)
                {
                    ushort newTimeToLive = (ushort)(Math.Min(entryTimeToLive, entry.TimeToLive - 1));
                    m_entries[kvp.Key] = entry.WithTimeToLive(newTimeToLive);
                }
            }
        }

        /// <summary>
        /// Returns the number of distinct files in this table.
        /// </summary>
        /// <remarks>
        /// This count is not consistent unless the table is completely quiescent (no concurrent. calls to
        /// <see cref="TryGetKnownContentHash(string)"/> or <see cref="RecordContentHash(FileStream, ContentHash, bool?)"/>).
        /// </remarks>
        public int Count => m_entries.Count;

        /// <summary>
        /// Indicates if this table was created via <see cref="CreateStub"/>.
        /// </summary>
        public bool IsStub { get; }

        /// <summary>
        /// Returns the number of save / load roundtrips without use allowed for an entry before it is evicted.
        /// </summary>
        public ushort EntryTimeToLive { get; }

        #region Content hash retrieval

        /// <summary>
        /// Retrieves an already-known <see cref="ContentHash" /> for the given path. If no such hash is available (such as if the
        /// file has been modified since a hash was last recorded), null is returned instead.
        /// </summary>
        /// <remarks>
        /// Note that this results in a small amount of I/O (e.g., on Windows, a file open and USN query), but never hashes the file or reads its contents.
        /// </remarks>
        /// <exception cref="BuildXLException">Thrown if the given path could not be opened for reading.</exception>
        public VersionedFileIdentityAndContentInfo? TryGetKnownContentHash(string path)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(path));

            try
            {
                using (
                    FileStream stream = FileUtilities.CreateFileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete))
                {
                    return TryGetKnownContentHash(stream.Name, stream.SafeFileHandle);
                }
            }
            catch (BuildXLException)
            {
                return null;
            }
        }

        /// <summary>
        /// Retrieves an already-known <see cref="ContentHash" /> for the given file handle. If no such hash is available (such as
        /// if the file has been modified since a hash was last recorded), null is returned instead.
        /// </summary>
        /// <remarks>
        /// Note that this results in a small amount of I/O (e.g., on Windows, a file open and USN query), but never hashes the file or reads its contents.
        /// </remarks>
        public VersionedFileIdentityAndContentInfo? TryGetKnownContentHash(FileStream stream)
        {
            Contract.Requires(stream != null);
            Contract.Requires(stream.SafeFileHandle != null);
            Contract.Requires(!string.IsNullOrWhiteSpace(stream.Name));

            return TryGetKnownContentHash(stream.Name, stream.SafeFileHandle);
        }

        /// <summary>
        /// Retrieves an already-known <see cref="ContentHash" /> for the given file handle. If no such hash is available (such as
        /// if the file has been modified since a hash was last recorded), null is returned instead.
        /// </summary>
        /// <remarks>
        /// Note that this results in a small amount of I/O (e.g., on Windows, a file open and USN query), but never hashes the file or reads its contents.
        /// </remarks>
        public VersionedFileIdentityAndContentInfo? TryGetKnownContentHash(string path, SafeFileHandle handle)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(path));
            Contract.Requires(handle != null);

            using (Counters.StartStopwatch(FileContentTableCounters.GetContentHashDuration))
            {
                Possible<VersionedFileIdentity, Failure<VersionedFileIdentity.IdentityUnavailabilityReason>> possibleVersionedIdentity =
                    TryQueryWeakIdentity(handle);

                if (!possibleVersionedIdentity.Succeeded)
                {
                    // We fail quietly for disabled journals on the query side; instead attempting to record a hash will fail.
                    Contract.Assume(
                        possibleVersionedIdentity.Failure.Content == VersionedFileIdentity.IdentityUnavailabilityReason.NotSupported);
                    Tracing.Logger.Log.StorageVersionedFileIdentityNotSupportedMiss(m_loggingContext, path);
                    return null;
                }

                VersionedFileIdentity identity = possibleVersionedIdentity.Result;
                var fileIdInfo = new FileIdAndVolumeId(identity.VolumeSerialNumber, identity.FileId);

                // We have a valid identity, but that identity is 'weak' and may correspond to an intermediate record (one without 'close' set).
                // We cannot discard such records here since we can't obtain a real 'Reason' field for a file's current USN record.
                // But we do know that any intermediate record will be a miss below, since we only record 'close' records (strong identities)
                // (see RecordContentHashAsync).
                Entry knownEntry;
                bool foundEntry = m_entries.TryGetValue(fileIdInfo, out knownEntry);

                if (!foundEntry)
                {
                    Counters.IncrementCounter(FileContentTableCounters.NumFileIdMismatch);
                    Tracing.Logger.Log.StorageUnknownFileMiss(
                        m_loggingContext,
                        path,
                        identity.FileId.High,
                        identity.FileId.Low,
                        identity.VolumeSerialNumber,
                        identity.Usn.Value);

                    return null;
                }

                var staleUsn = identity.Usn != knownEntry.Usn;

                if (staleUsn)
                {
                    if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                    {
                        Tracing.Logger.Log.StorageUnknownUsnMiss(
                            m_loggingContext,
                            path,
                            identity.FileId.High,
                            identity.FileId.Low,
                            identity.VolumeSerialNumber,
                            readUsn: identity.Usn.Value,
                            knownUsn: knownEntry.Usn.Value,
                            knownContentHash: knownEntry.Hash.ToHex());
                    }

                    return null;
                }

                MarkEntryAccessed(fileIdInfo, knownEntry);
                Counters.IncrementCounter(FileContentTableCounters.NumHit);

                if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                {
                    Tracing.Logger.Log.StorageKnownUsnHit(
                        m_loggingContext,
                        path,
                        identity.FileId.High,
                        identity.FileId.Low,
                        identity.VolumeSerialNumber,
                        usn: knownEntry.Usn.Value,
                        contentHash: knownEntry.Hash.ToHex());
                }

                // Note that we return a 'strong' version of the weak identity; since we matched an entry in the table, we know that the USN
                // actually corresponds to a strong identity (see RecordContentHashAsync).
                return new VersionedFileIdentityAndContentInfo(
                    new VersionedFileIdentity(
                        identity.VolumeSerialNumber,
                        identity.FileId,
                        identity.Usn,
                        VersionedFileIdentity.IdentityKind.StrongUsn),
                    new FileContentInfo(knownEntry.Hash, knownEntry.Length));
            }
        }

        #endregion Content hash retrieval

        #region Content hash recording

        /// <summary>
        /// Records a <see cref="ContentHash" /> for the given file handle. This hash mapping will be persisted to disk if the
        /// table is saved with <see cref="SaveAsync" />. The given file handle should be opened with at most Read sharing
        /// (having the handle should ensure the file is not being written).
        /// This returns a <see cref="VersionedFileIdentityAndContentInfo"/>:
        /// - The identity has the kind <see cref="VersionedFileIdentity.IdentityKind.StrongUsn"/> if a USN-based identity was successfully established;
        ///   the identity may have kind <see cref="VersionedFileIdentity.IdentityKind.Anonymous"/> if such an identity was unavailable.
        /// - Regardless, the contained <see cref="FileContentInfo"/> contains the actual length of the stream corresponding to <paramref name="hash"/>.
        /// </summary>
        /// <remarks>
        /// An overload taking a file path is intentionally not provided. This should be called after hashing or writing a file,
        /// but before closing the handle. This way, there is no race between establishing the file's hash, some unrelated writer,
        /// and recording its file version (e.g., USN) to hash mapping.
        /// Note that this results in a small amount of I/O (e.g., on Windows, a file open and USN query), but never hashes the file or reads its contents.
        /// The <paramref name="strict"/> corresponds to the <c>flush</c> parameter of <see cref="VersionedFileIdentity.TryEstablishStrong"/>
        /// </remarks>
        public VersionedFileIdentityAndContentInfo RecordContentHash(
            FileStream stream, 
            ContentHash hash,
            bool? strict = default)
        {
            Contract.Requires(stream != null);

            strict = strict ?? stream.CanWrite;

            Contract.AssertDebug(stream.SafeFileHandle != null && stream.Name != null);
            long length = stream.Length;
            VersionedFileIdentity identity = RecordContentHash(
                stream.Name, 
                stream.SafeFileHandle, 
                hash, 
                length,
                strict: strict);
            return new VersionedFileIdentityAndContentInfo(identity, new FileContentInfo(hash, length));
        }

        /// <nodoc />
        public VersionedFileIdentityAndContentInfo RecordContentHash(
            SafeFileHandle handle,
            string path,
            ContentHash hash,
            long length,
            bool? strict = default)
        {
            VersionedFileIdentity identity =
                RecordContentHash(
                    path,
                    handle,
                    hash, 
                    length,
                    strict: strict);
            return new VersionedFileIdentityAndContentInfo(identity, new FileContentInfo(hash, length));
        }

        /// <summary>
        /// Records a <see cref="ContentHash" /> for the given file handle. This hash mapping will be persisted to disk if the
        /// table is saved with <see cref="SaveAsync" />. The given file handle should be opened with at most Read sharing
        /// (having the handle should ensure the file is not being written).
        /// This returns a <see cref="VersionedFileIdentityAndContentInfo"/>:
        /// - The identity has the kind <see cref="VersionedFileIdentity.IdentityKind.StrongUsn"/> if a USN-based identity was successfully established;
        ///   the identity may have kind <see cref="VersionedFileIdentity.IdentityKind.Anonymous"/> if such an identity was unavailable.
        /// - Regardless, the contained <see cref="FileContentInfo"/> contains the actual length of the stream corresponding to <paramref name="hash"/>.
        /// </summary>
        /// <remarks>
        /// An overload taking a file path is intentionally not provided. This should be called after hashing or writing a file,
        /// but before closing the handle. This way, there is no race between establishing the file's hash, some unrelated writer,
        /// and recording its file version (e.g., USN) to hash mapping.
        /// Note that this results in a small amount of I/O (e.g., on Windows, a file open and USN query), but never hashes the file or reads its contents.
        /// The <paramref name="strict"/> corresponds to the <c>flush</c> parameter of <see cref="VersionedFileIdentity.TryEstablishStrong"/>
        /// </remarks>
        public VersionedFileIdentity RecordContentHash(
            string path, 
            SafeFileHandle handle, 
            ContentHash hash, 
            long length,
            bool? strict = default)
        {
            Contract.Requires(handle != null);
            Contract.Requires(!string.IsNullOrWhiteSpace(path));

            using (Counters.StartStopwatch(FileContentTableCounters.RecordContentHashDuration))
            {
                // TODO: The contract below looks very nice but breaks tons of UT
                // Fix the tests and enable the contract.
                // Contract.Requires(FileContentInfo.IsValidLength(length, hash));
                // Here we write a new change journal record for this file to get a 'strong' identity. This means that the USN -> hash table
                // only ever contains USNs whose records have the 'close' reason set. Recording USNs without that
                // reason set would not be correct; it would be possible that multiple separate changes (e.g. writes)
                // were represented with the same USN, and so intermediate USNs do not necessarily correspond to exactly
                // one snapshot of a file. See http://msdn.microsoft.com/en-us/library/windows/desktop/aa363803(v=vs.85).aspx
                Possible<VersionedFileIdentity, Failure<VersionedFileIdentity.IdentityUnavailabilityReason>> possibleVersionedIdentity =
                    TryEstablishStrongIdentity(handle, flush: strict == true);

                if (!possibleVersionedIdentity.Succeeded)
                {
                    if (Interlocked.CompareExchange(ref m_changeJournalWarningLogged, 1, 0) == 0)
                    {
                        Tracing.Logger.Log.StorageFileContentTableIgnoringFileSinceVersionedFileIdentityIsNotSupported(
                            m_loggingContext,
                            path,
                            possibleVersionedIdentity.Failure.DescribeIncludingInnerFailures());
                    }

                    return VersionedFileIdentity.Anonymous;
                }

                VersionedFileIdentity identity = possibleVersionedIdentity.Result;

                var newEntry = new Entry(identity.Usn, hash, length, EntryTimeToLive);

                // We allow concurrent update attempts with different observed USNs.
                // This is useful and relevant for two reasons:
                // - Querying a 'strong' identity (TryEstablishStrongIdentity) generates a new CLOSE record every time.
                // - Creating hardlinks generates 'hardlink change' records.
                // So, concurrently creating and recording (or even just recording) different links is possible, and
                // keeping the last stored entry (rather than highest-USN entry) can introduce false positives.
                var fileIdAndVolumeId = new FileIdAndVolumeId(identity.VolumeSerialNumber, identity.FileId);

                m_entries.AddOrUpdate(
                    new FileIdAndVolumeId(identity.VolumeSerialNumber, identity.FileId),
                    newEntry,
                    updateValueFactory: (key, existingEntry) =>
                    {
                        if (existingEntry.Usn > newEntry.Usn)
                        {
                            return existingEntry;
                        }

                        if (newEntry.Hash == existingEntry.Hash)
                        {
                            Counters.IncrementCounter(FileContentTableCounters.NumUsnMismatch);

                            if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                            {
                                Tracing.Logger.Log.StorageUsnMismatchButContentMatch(
                                    m_loggingContext,
                                    path,
                                    existingEntry.Usn.Value,
                                    newEntry.Usn.Value,
                                    existingEntry.Hash.ToHex());
                            }
                        }
                        else
                        {
                        // Stale USN.
                        Counters.IncrementCounter(FileContentTableCounters.NumContentMismatch);
                        }

                        return newEntry;
                    });

                if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                {
                    Tracing.Logger.Log.StorageRecordNewKnownUsn(
                        m_loggingContext,
                        path,
                        identity.FileId.High,
                        identity.FileId.Low,
                        identity.VolumeSerialNumber,
                        identity.Usn.Value,
                        hash.ToHex());
                }

                return identity;
            }
        }

        #endregion Content hash recording

        private Possible<VersionedFileIdentity, Failure<VersionedFileIdentity.IdentityUnavailabilityReason>> TryQueryWeakIdentity(SafeFileHandle handle)
        {
            if (IsStub)
            {
                return new Failure<VersionedFileIdentity.IdentityUnavailabilityReason>(
                    VersionedFileIdentity.IdentityUnavailabilityReason.NotSupported);
            }

            return VersionedFileIdentity.TryQuery(handle);
        }

        private Possible<VersionedFileIdentity, Failure<VersionedFileIdentity.IdentityUnavailabilityReason>> TryEstablishStrongIdentity(
            SafeFileHandle handle,
            bool flush)
        {
            if (IsStub)
            {
                return new Failure<VersionedFileIdentity.IdentityUnavailabilityReason>(
                    VersionedFileIdentity.IdentityUnavailabilityReason.NotSupported);
            }

            return VersionedFileIdentity.TryEstablishStrong(handle, flush: flush);
        }

        private void MarkEntryAccessed(FileIdAndVolumeId id, Entry entry)
        {
            if (entry.TimeToLive == EntryTimeToLive)
            {
                return; // TTL is already at max; don't bother poking the dictionary.
            }

            Entry newEntry = entry.WithTimeToLive(EntryTimeToLive);

            // We use TryUpdate here since it is possible that a new entry (also with TTL at m_entryTimeToLive)
            // was recorded with a new USN. No retries are needed since all changes after load set TTL at m_entryTimeToLive.
            Analysis.IgnoreResult(m_entries.TryUpdate(id, newEntry, comparisonValue: entry));
        }

        /// <summary>
        /// Visits each entry in this table that is up-to-date (i.e., <see cref="TryGetKnownContentHash(string)"/>
        /// would return a known content hash). The visitor <c>(file id and volume id, file handle, path, known usn, known hash) => bool</c> returns a bool indicating if visitation
        /// should continue. The file handle given to the visitor is opened for <c>GENERIC_READ</c> access.
        /// </summary>
        /// <remarks>
        /// This is intended as a diagnostic function rather than a course of normal operation. One can use this to e.g. validate that all content
        /// hashes are accurate, a known set of entries are contained in the table, etc.
        /// </remarks>
        public bool VisitKnownFiles(
            IFileContentTableAccessor accessor,
            FileShare fileShare,
            Func<FileIdAndVolumeId, SafeFileHandle, string, Usn, ContentHash, bool> visitor)
        {
            Contract.Requires(accessor != null);
            Contract.Requires(visitor != null);

            foreach (KeyValuePair<FileIdAndVolumeId, Entry> entry in m_entries)
            {
                if (accessor.TryGetFileHandleAndPathFromFileIdAndVolumeId(entry.Key, fileShare, out SafeFileHandle handle, out string path))
                {
                    using (handle)
                    {
                        Possible<VersionedFileIdentity, Failure<VersionedFileIdentity.IdentityUnavailabilityReason>> possibleActualIdentity =
                            TryQueryWeakIdentity(handle);

                        // These cases should mirror parts of TryGetKnownContentHashAsync (but note that here we already have a known file ID, since
                        // we found it via a table entry rather than via some arbitrary handle).
                        if (possibleActualIdentity.Succeeded)
                        {
                            VersionedFileIdentity actualIdentity = possibleActualIdentity.Result;
                            if (actualIdentity.Usn != entry.Value.Usn)
                            {
                                if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                                {
                                    Tracing.Logger.Log.StorageUnknownUsnMiss(
                                        m_loggingContext,
                                        path,
                                        entry.Key.FileId.High,
                                        entry.Key.FileId.Low,
                                        entry.Key.VolumeSerialNumber,
                                        readUsn: actualIdentity.Usn.Value,
                                        knownUsn: entry.Value.Usn.Value,
                                        knownContentHash: entry.Value.Hash.ToHex());
                                }
                            }
                            else
                            {
                                if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                                {
                                    Tracing.Logger.Log.StorageKnownUsnHit(
                                        m_loggingContext,
                                        path,
                                        entry.Key.FileId.High,
                                        entry.Key.FileId.Low,
                                        entry.Key.VolumeSerialNumber,
                                        usn: entry.Value.Usn.Value,
                                        contentHash: entry.Value.Hash.ToHex());
                                }

                                bool shouldContinue = visitor(entry.Key, handle, path, entry.Value.Usn, entry.Value.Hash);

                                if (!shouldContinue)
                                {
                                    return false;
                                }
                            }
                        }
                        else
                        {
                            Contract.Assume(
                                possibleActualIdentity.Failure.Content == VersionedFileIdentity.IdentityUnavailabilityReason.NotSupported);
                            Tracing.Logger.Log.StorageVersionedFileIdentityNotSupportedMiss(m_loggingContext, path);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Loads a file content table from the specified path. In the event of a recoverable failure (such as the absence of a
        /// table at the
        /// specified path, or a table that could not be deserialized), an empty table is returned (the recoverable failure is not
        /// re-thrown).
        /// </summary>
        /// <returns>A loaded table (possibly empty), or a newly created table (in the event of a load failure).</returns>
        public static Task<FileContentTable> LoadOrCreateAsync(LoggingContext loggingContext, string fileContentTablePath, ushort entryTimeToLive = DefaultTimeToLive)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));
            Contract.Requires(entryTimeToLive > 0);

            return LoadAsync(loggingContext, fileContentTablePath, entryTimeToLive)
                .ContinueWith(a => a.Result ?? CreateNew(loggingContext, entryTimeToLive: entryTimeToLive));
        }

        /// <summary>
        /// Loads a file content table from the specified path.
        /// </summary>
        /// <returns>A loaded table (it is empty only if the table on disk was valid but empty).</returns>
        /// <exception cref="BuildXLException">
        /// Thrown in the event of a recoverable I/O exception, including the absence of the
        /// specified table or a deserialization failure.
        /// </exception>
        public static Task<FileContentTable> LoadAsync(
            LoggingContext loggingContext,
            string fileContentTablePath,
            ushort entryTimeToLive = DefaultTimeToLive)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));
            Contract.Requires(entryTimeToLive > 0);

            return Task.Run(() =>
            {
                LoadResult loadResult = TryLoadInternal(loggingContext, fileContentTablePath, entryTimeToLive);
                loadResult.Log(loggingContext);

                if (!loadResult.Succeeded)
                {
                    return null;
                }

                return loadResult.LoadedFileContentTable;
            });
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        private static LoadResult TryLoadInternal(LoggingContext loggingContext, string fileContentTablePath, ushort entryTimeToLive)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));
            Contract.Requires(entryTimeToLive > 0);

            if (!FileUtilities.FileExistsNoFollow(fileContentTablePath))
            {
                return LoadResult.FileNotFound(fileContentTablePath);
            }

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                using (FileStream stream = FileUtilities.CreateFileStream(
                        fileContentTablePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        // Ok to evict the file from standby since the file will be overwritten and never reread from disk after this point.
                        FileOptions.SequentialScan))
                {
                    try
                    {
                        Analysis.IgnoreResult(s_fileEnvelope.ReadHeader(stream));
                    }
                    catch (BuildXLException ex)
                    {
                        return LoadResult.InvalidFormat(fileContentTablePath, ex.LogEventMessage, sw.ElapsedMilliseconds);
                    }

                    using (var reader = new BuildXLReader(debug: false, stream: stream, leaveOpen: true))
                    {
                        var loadedTable = new FileContentTable(loggingContext);

                        uint numberOfEntries = reader.ReadUInt32();
                        int hashLength = ContentHashingUtilities.HashInfo.ByteLength;
                        var hashBuffer = new byte[hashLength];

                        // Adding to the m_entries dictionary is about half of the work (in terms of wall clock time) of
                        // deserialization for large FileContentTables. Perform that on another thread to get out of the
                        // way of reading in the file as quickly as possible
                        ConcurrentQueue<KeyValuePair<FileIdAndVolumeId, Entry>> itemsToInsert = new ConcurrentQueue<KeyValuePair<FileIdAndVolumeId, Entry>>();
                        bool completeReadingFile = false;
                        Exception deserializationHelperException = null;
                        Thread deserializationHelper = new Thread(() =>
                        {
                            try
                            {
                                while (true)
                                {
                                    KeyValuePair<FileIdAndVolumeId, Entry> item;
                                    if (itemsToInsert.TryDequeue(out item))
                                    {
                                        bool added = loadedTable.m_entries.TryAdd(item.Key, item.Value);
                                        Contract.Assume(added);
                                    }
                                    else
                                    {
                                        if (completeReadingFile && !itemsToInsert.TryPeek(out item))
                                        {
                                            break;
                                        }

                                        // The amount of sleep time for this thread doesn't noticeably change the end to end performance.
                                        Thread.Sleep(5);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                deserializationHelperException = ex;
                            }
                        });
                        deserializationHelper.Name = "FileContentTable deserialization helper";
                        deserializationHelper.Start();

                        for (uint i = 0; i < numberOfEntries; i++)
                        {
                            // Key: Volume and file ID
                            var fileIdAndVolumeId = FileIdAndVolumeId.Deserialize(reader);

                            // Entry: USN, hash, length, time to live.
                            Usn usn = new Usn(reader.ReadUInt64());

                            int hashBytesRead = 0;
                            while (hashBytesRead != hashLength)
                            {
                                int thisRead = reader.Read(hashBuffer, hashBytesRead, hashLength - hashBytesRead);
                                if (thisRead == 0)
                                {
                                    return LoadResult.InvalidFormat(fileContentTablePath, "Unexpected end of stream", sw.ElapsedMilliseconds);
                                }

                                hashBytesRead += thisRead;
                                Contract.Assert(hashBytesRead <= hashLength);
                            }

                            long length = reader.ReadInt64();

                            ushort thisEntryTimeToLive = reader.ReadUInt16();
                            if (thisEntryTimeToLive == 0)
                            {
                                return LoadResult.InvalidFormat(fileContentTablePath, "TTL value must be positive", sw.ElapsedMilliseconds);
                            }

                            thisEntryTimeToLive = Math.Min(thisEntryTimeToLive, entryTimeToLive);
                            Contract.Assert(thisEntryTimeToLive > 0);

                            // We've loaded this entry just now and so clearly haven't used it yet. Tentatively decrement the TTL
                            // for the in-memory table; if the table is saved again without using this entry, the TTL will stay at this
                            // lower value.
                            thisEntryTimeToLive--;

                            var observedVersionAndHash = new Entry(usn, ContentHashingUtilities.CreateFrom(hashBuffer), length, thisEntryTimeToLive);
                            itemsToInsert.Enqueue(new KeyValuePair<FileIdAndVolumeId, Entry>(fileIdAndVolumeId, observedVersionAndHash));
                        }

                        completeReadingFile = true;
                        deserializationHelper.Join();

                        // Allow exceptions within the deserializationHelper thread to be handled the same as main thread
                        // exception handling.
                        if (deserializationHelperException != null)
                        {
                            ExceptionDispatchInfo.Capture(deserializationHelperException).Throw();
                        }

                        loadedTable.Counters.AddToCounter(FileContentTableCounters.NumEntries, loadedTable.Count);
                        loadedTable.Counters.AddToCounter(FileContentTableCounters.LoadDuration, sw.Elapsed);

                        return LoadResult.Success(fileContentTablePath, loadedTable, sw.ElapsedMilliseconds);
                    }
                }
            }
            catch (Exception ex)
            {
                return LoadResult.Exception(fileContentTablePath, ex, sw.ElapsedMilliseconds);
            }
        }

        private class LoadResult
        {
            private enum Kind
            {
                Success,
                FileNotFound,
                InvalidFormat,
                Exception,
            }

            private readonly string m_fileContentTablePath;
            private readonly Kind m_kind;
            private readonly long m_loadedDurationMs;
            private readonly string m_reason;
            private readonly string m_stackTrace;
            public readonly FileContentTable LoadedFileContentTable;

            public bool Succeeded => m_kind == Kind.Success;

            private LoadResult(string fileContentTablePath, Kind kind, long loadedDurationMs, FileContentTable loadedFileContentTable = null, string reason = null, string stackTrace = null)
            {
                Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));

                m_fileContentTablePath = fileContentTablePath;
                m_kind = kind;
                m_loadedDurationMs = loadedDurationMs;
                m_reason = reason;
                m_stackTrace = stackTrace;
                LoadedFileContentTable = loadedFileContentTable;
            }

            public static LoadResult Success(string fileContentTablePath, FileContentTable loadedFileContentTable, long loadedDurationMs)
            {
                Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));
                Contract.Requires(loadedFileContentTable != null);

                return new LoadResult(fileContentTablePath, Kind.Success, loadedDurationMs, loadedFileContentTable);
            }

            public static LoadResult FileNotFound(string fileContentTablePath)
            {
                Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));

                return new LoadResult(fileContentTablePath, Kind.FileNotFound, 0);
            }

            public static LoadResult InvalidFormat(string fileContentTablePath, string reason, long loadedDurationMs)
            {
                Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));

                return new LoadResult(fileContentTablePath, Kind.InvalidFormat, loadedDurationMs, reason: reason);
            }

            public static LoadResult Exception(string fileContentTablePath, Exception exception, long loadedDurationMs)
            {
                Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));
                Contract.Requires(exception != null);

                return new LoadResult(fileContentTablePath, Kind.Exception, loadedDurationMs, reason: exception.Message, stackTrace: exception.StackTrace);
            }

            public void Log(LoggingContext loggingContext)
            {
                Tracing.Logger.Log.StorageLoadFileContentTable(
                    loggingContext, 
                    m_fileContentTablePath, 
                    m_kind.ToString(), 
                    m_reason ?? string.Empty, 
                    m_loadedDurationMs, 
                    m_stackTrace == null ? string.Empty : Environment.NewLine + m_stackTrace);
            }
        }

        /// <summary>
        /// Saves this file content table to the specified path so that it may later be loaded by <see cref="LoadAsync" /> or
        /// <see cref="LoadOrCreateAsync" />.
        /// </summary>
        /// <returns>A loaded table (it is empty only if the table on disk was valid but empty).</returns>
        /// <exception cref="BuildXLException">
        /// Thrown in the event of a recoverable I/O exception (assume the table was not fully
        /// saved and is invalid)
        /// </exception>
        public Task SaveAsync(string fileContentTablePath)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));

            return Task.Run(() => SaveInternal(fileContentTablePath));
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        private void SaveInternal(string fileContentTablePath)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(fileContentTablePath));

            ExceptionUtilities.HandleRecoverableIOException(
                () =>
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    int numEvicted = 0;

                    Directory.CreateDirectory(Path.GetDirectoryName(fileContentTablePath));

                    // Note that we are using a non-async file stream here. That's because we're doing lots of tiny writes for simplicity,
                    // but tiny writes on an async stream end up blocking anyway while adding silly overhead.
                    using (FileStream stream = FileUtilities.CreateFileStream(
                        fileContentTablePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Delete,
                        // Do not write the file with SequentialScan since it will be reread in the subsequent build
                        FileOptions.None))
                    {
                        // We don't have anything in particular to correlate this file to,
                        // so we are simply creating a unique correlation id that is used as part
                        // of the header consistency check.
                        FileEnvelopeId correlationId = FileEnvelopeId.Create();
                        s_fileEnvelope.WriteHeader(stream, correlationId);

                        using (var writer = new BuildXLWriter(debug: false, stream: stream, leaveOpen: true, logStats: false))
                        {
                            long numberOfEntriesPosition = writer.BaseStream.Position;
                            writer.Write(0U);

                            uint entriesWritten = 0;
                            var hashBuffer = new byte[ContentHashingUtilities.HashInfo.ByteLength];

                            foreach (var fileAndEntryPair in m_entries)
                            {
                                // Skip saving anything with a TTL of zero. These entries were loaded
                                // with a TTL of one (immediately decremented) and were not used since load.
                                // See class remarks.
                                if (fileAndEntryPair.Value.TimeToLive == 0)
                                {
                                    numEvicted++;
                                    continue;
                                }

                                // Key: Volume and File ID
                                fileAndEntryPair.Key.Serialize(writer);

                                // Entry: USN, hash, time to live.
                                writer.Write(fileAndEntryPair.Value.Usn.Value);
                                fileAndEntryPair.Value.Hash.SerializeHashBytes(hashBuffer, 0);
                                writer.Write(hashBuffer);
                                writer.Write(fileAndEntryPair.Value.Length);
                                writer.Write(fileAndEntryPair.Value.TimeToLive);

                                entriesWritten++;
                            }

                            var endPosition = writer.BaseStream.Position;
                            writer.BaseStream.Position = numberOfEntriesPosition;
                            writer.Write(entriesWritten);
                            writer.BaseStream.Position = endPosition;
                        }

                        s_fileEnvelope.FixUpHeader(stream, correlationId);
                    }

                    Counters.AddToCounter(FileContentTableCounters.NumEvicted, numEvicted);
                    Counters.AddToCounter(FileContentTableCounters.SaveDuration, sw.Elapsed);

                    Tracing.Logger.Log.StorageFinishedSavingFileContentTable(m_loggingContext, fileContentTablePath);
                    return Unit.Void;
                },
                ex => { throw new BuildXLException("Failure writing file content table", ex); });
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Entry : IEquatable<Entry>
        {
            public readonly ContentHash Hash;

            public readonly ushort TimeToLive;

            public readonly Usn Usn;

            public readonly long Length;

            public Entry(Usn usn, ContentHash hash, long length, ushort timeToLive)
            {
                Hash = hash;
                TimeToLive = timeToLive;
                Usn = usn;
                Length = length;
            }

            public Entry WithTimeToLive(ushort newTimeToLive)
            {
                return new Entry(usn: Usn, hash: Hash, length: Length, timeToLive: newTimeToLive);
            }

            public Entry WithNewUsn(Usn usn)
            {
                return usn <= Usn ? this : new Entry(usn: usn, hash: Hash, length: Length, timeToLive: TimeToLive);
            }

            /// <inheritdoc />
            public bool Equals(Entry other)
            {
                return other.Usn == Usn && other.TimeToLive == TimeToLive && other.Hash == Hash && other.Length == Length;
            }

            /// <inheritdoc />
            public override bool Equals(object obj)
            {
                return StructUtilities.Equals(this, obj);
            }

            /// <inheritdoc />
            public override int GetHashCode()
            {
                return HashCodeHelper.Combine(Hash.GetHashCode(), TimeToLive.GetHashCode(), Usn.GetHashCode(), Length.GetHashCode());
            }

            /// <nodoc />
            public static bool operator ==(Entry left, Entry right)
            {
                return left.Equals(right);
            }

            /// <nodoc />
            public static bool operator !=(Entry left, Entry right)
            {
                return !left.Equals(right);
            }
        }

        #region Observer

        private readonly HashSet<FileIdAndVolumeId> m_updatedFileId = new HashSet<FileIdAndVolumeId>();

        /// <inheritdoc />
        public void OnNext(ChangedPathInfo value)
        {
        }

        /// <inheritdoc />
        public void OnNext(ChangedFileIdInfo value)
        {
            Entry entry;
            if (m_entries.TryGetValue(value.FileIdAndVolumeId, out entry))
            {
                if (value.UsnRecord.Usn > entry.Usn)
                {
                    switch (value.UsnRecord.Reason.LinkImpact())
                    {
                        case LinkImpact.None:
                        case LinkImpact.SingleLink:
                            // Update with new USN only if entry's USN matches previously tracked USN or the file id's entry was updated before.
                            // We track whether file id's entry was updated or not because some operations can have more than one USN records.
                            // For example, file renaming has at least two records, "RenameOldName" followed by "RenameNewName".
                            // Another example is timestamp modification can come with "BasicInfoChange" followed by "BasicInfoChange|Close".
                            // If the entry is updated by the first record, and we don't keep track that fact, then the new record will
                            // invalidate the updated entry because it will have more up-to-date USN.
                            if (m_updatedFileId.Contains(value.FileIdAndVolumeId) || value.LastTrackedUsn == entry.Usn)
                            {
                                m_entries.TryUpdate(value.FileIdAndVolumeId, entry.WithNewUsn(value.UsnRecord.Usn), entry);
                                m_updatedFileId.Add(value.FileIdAndVolumeId);
                                ++m_observerData.UpdatedUsnEntryByJournalScanningCount;
                            }
                            break;
                        case LinkImpact.AllLinks:
                            // Remove from mapping.
                            m_entries.TryRemove(value.FileIdAndVolumeId, out _);
                            ++m_observerData.RemovedEntryByJournalScanningCount;
                            break;
                    }
                }
            }
        }

        /// <inheritdoc />
        void IObserver<ChangedFileIdInfo>.OnError(Exception error)
        {
        }

        /// <inheritdoc />
        void IObserver<ChangedFileIdInfo>.OnCompleted()
        {
        }

        /// <inheritdoc />
        void IObserver<ChangedPathInfo>.OnError(Exception error)
        {
        }

        /// <inheritdoc />
        void IObserver<ChangedPathInfo>.OnCompleted()
        {
        }

        /// <inheritdoc />
        public void OnInit()
        {
            m_observerData.Init();
        }

        /// <inheritdoc />
        public void OnCompleted(ScanningJournalResult result)
        {
            m_updatedFileId.Clear();
            m_observerData.UpdateCounters(Counters);
        }

        private class ObserverData
        {
            public int UpdatedUsnEntryByJournalScanningCount = 0;
            public int RemovedEntryByJournalScanningCount = 0;

            public void Init()
            {
                RemovedEntryByJournalScanningCount = 0;
                UpdatedUsnEntryByJournalScanningCount = 0;
            }

            public void UpdateCounters(CounterCollection<FileContentTableCounters> counters)
            {
                counters.AddToCounter(FileContentTableCounters.NumUpdatedUsnEntriesByJournalScanning, UpdatedUsnEntryByJournalScanningCount);
                counters.AddToCounter(FileContentTableCounters.NumRemovedEntriesByJournalScanning, RemovedEntryByJournalScanningCount);
            }
        }

        #endregion Observer
    }
}
