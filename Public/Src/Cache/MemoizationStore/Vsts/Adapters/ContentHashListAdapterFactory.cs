// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Vsts;
using BuildXL.Cache.MemoizationStore.VstsInterfaces;

namespace BuildXL.Cache.MemoizationStore.Vsts.Adapters
{
    /// <summary>
    /// A factory class for creating contenthashlistadapters
    /// </summary>
    public sealed class ContentHashListAdapterFactory : IDisposable
    {
        private readonly IBuildCacheHttpClientFactory _httpClientFactory;

        private ContentHashListAdapterFactory(IBuildCacheHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Gets a BuildCacheHttpClient
        /// </summary>
        public IBuildCacheHttpClientCommon BuildCacheHttpClient { get; private set; }

        /// <summary>
        /// Creates an instance of the ContentHashListAdapterFactory class.
        /// </summary>
        public static async Task<ContentHashListAdapterFactory> CreateAsync(
            Context context,
            IBuildCacheHttpClientFactory httpClientFactory,
            bool useBlobContentHashLists)
        {
            var adapterFactory = new ContentHashListAdapterFactory(httpClientFactory);
            try
            {
                await adapterFactory.StartupAsync(context, useBlobContentHashLists);
            }
            catch (Exception)
            {
                adapterFactory.Dispose();
                throw;
            }

            return adapterFactory;
        }

        /// <summary>
        /// Creates a contenthashlistadapter for a particular session.
        /// </summary>
        public IContentHashListAdapter Create(IBackingContentSession contentSession, bool includeDownloadUris)
        {
            ItemBuildCacheHttpClient itemBasedClient = BuildCacheHttpClient as ItemBuildCacheHttpClient;

            if (itemBasedClient != null)
            {
                return new ItemBuildCacheContentHashListAdapter(itemBasedClient, contentSession.UriCache);
            }

            return new BlobBuildCacheContentHashListAdapter((IBlobBuildCacheHttpClient)BuildCacheHttpClient, contentSession, includeDownloadUris);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            BuildCacheHttpClient?.Dispose();
        }

        private async Task StartupAsync(Context context, bool useBlobContentHashLists)
        {
            if (useBlobContentHashLists)
            {
                BuildCacheHttpClient = await _httpClientFactory.CreateBlobBuildCacheHttpClientAsync(context).ConfigureAwait(false);
            }
            else
            {
                BuildCacheHttpClient =
                    await _httpClientFactory.CreateBuildCacheHttpClientAsync(context).ConfigureAwait(false);
            }
        }
    }
}
