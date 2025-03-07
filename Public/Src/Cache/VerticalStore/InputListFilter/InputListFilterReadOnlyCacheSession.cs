// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.Interfaces;
using BuildXL.Utilities;

namespace BuildXL.Cache.InputListFilter
{
    internal class InputListFilterReadOnlyCacheSession : ICacheReadOnlySession
    {
        private readonly ICacheReadOnlySession m_session;
        protected readonly InputListFilterCache Cache;

        internal InputListFilterReadOnlyCacheSession(ICacheReadOnlySession session, InputListFilterCache cache)
        {
            Cache = cache;
            m_session = session;
        }

        public CacheId CacheId => m_session.CacheId;

        public string CacheSessionId => m_session.CacheSessionId;

        public bool IsClosed => m_session.IsClosed;

        public bool StrictMetadataCasCoupling => m_session.StrictMetadataCasCoupling;

        public Task<Possible<string, Failure>> CloseAsync(Guid activityId)
        {
            return m_session.CloseAsync(activityId);
        }

        public IEnumerable<Task<Possible<StrongFingerprint, Failure>>> EnumerateStrongFingerprints(WeakFingerprintHash weak, OperationHints hints, Guid activityId)
        {
            return m_session.EnumerateStrongFingerprints(weak, hints, activityId);
        }

        public Task<Possible<CasEntries, Failure>> GetCacheEntryAsync(StrongFingerprint strong, OperationHints hints, Guid activityId)
        {
            return m_session.GetCacheEntryAsync(strong, hints, activityId);
        }

        public Task<Possible<CacheSessionStatistics[], Failure>> GetStatisticsAsync(Guid activityId)
        {
            return m_session.GetStatisticsAsync(activityId);
        }

        public Task<Possible<ValidateContentStatus, Failure>> ValidateContentAsync(CasHash hash, UrgencyHint urgencyHint, Guid activityId)
        {
            return m_session.ValidateContentAsync(hash, urgencyHint, activityId);
        }

        public Task<Possible<StreamWithLength, Failure>> GetStreamAsync(CasHash hash, OperationHints hints, Guid activityId)
        {
            return m_session.GetStreamAsync(hash, hints, activityId);
        }

        public Task<Possible<string, Failure>[]> PinToCasAsync(CasEntries hashes, CancellationToken cancellationToken, OperationHints hints, Guid activityId)
        {
            return m_session.PinToCasAsync(hashes, cancellationToken, hints, activityId);
        }

        public Task<Possible<string, Failure>> PinToCasAsync(CasHash hash, CancellationToken cancellationToken, OperationHints hints, Guid activityId)
        {
            return m_session.PinToCasAsync(hash, cancellationToken, hints, activityId);
        }

        public Task<Possible<string, Failure>> ProduceFileAsync(CasHash hash, string filename, FileState fileState, OperationHints hints, Guid activityId, CancellationToken cancellationToken)
        {
            return m_session.ProduceFileAsync(hash, filename, fileState, hints, activityId, cancellationToken);
        }
    }
}
