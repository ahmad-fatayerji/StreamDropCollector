using Core.Enums;
using Core.Interfaces;
using Core.Logging;
using Core.Models;

namespace Core.Services
{
    /// <summary>
    /// Provides methods for retrieving active drops campaigns from supported streaming platforms.
    /// </summary>
    /// <remarks>This service currently supports retrieving campaigns from Kick. Support for additional
    /// platforms, such as Twitch, may be added in the future.</remarks>
    public class DropsService
    {
        private readonly KickDropsProvider _kickProvider = new();

        /// <summary>
        /// Retrieves all active drops campaigns from connected Kick and Twitch hosts asynchronously.
        /// </summary>
        /// <remarks>If both Kick and Twitch hosts are connected, campaigns from both sources are combined
        /// into a single list. The method returns quickly with an empty list if neither host is connected.</remarks>
        /// <param name="kickHost">The Kick web view host used to query active campaigns. Must not be null if Kick is connected.</param>
        /// <param name="kickStatus">The connection status of the Kick host. If set to <see langword="Connected"/>, Kick campaigns will be
        /// included.</param>
        /// <param name="twitchHost">The Twitch web view host used to query active campaigns. Must not be null if Twitch is connected.</param>
        /// <param name="twitchStatus">The connection status of the Twitch host. If set to <see langword="Connected"/>, Twitch campaigns will be
        /// included.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A read-only list containing all active drops campaigns from the specified connected hosts. The list will be
        /// empty if neither host is connected or no campaigns are found.</returns>
        public async Task<IReadOnlyList<DropsCampaign>> GetAllActiveCampaignsAsync(
            IWebViewHost kickHost,
            ConnectionStatus? kickStatus,
            IWebViewHost twitchHost,
            ConnectionStatus? twitchStatus,
            IGqlService? gqlService,
            CancellationToken ct = default,
            Action<IReadOnlyList<DropsCampaign>>? platformCampaignsFetched = null)
        {
            Task<IReadOnlyList<DropsCampaign>>? kickFetchTask = null;
            Task<IReadOnlyList<DropsCampaign>>? twitchFetchTask = null;

            if (kickStatus == ConnectionStatus.Connected)
                kickFetchTask = FetchPlatformCampaignsAsync("Kick", () => _kickProvider.GetActiveCampaignsAsync(kickHost, ct, platformCampaignsFetched), ct, platformCampaignsFetched);

            if (twitchStatus == ConnectionStatus.Connected)
            {
                TwitchDropsProvider twitchProvider = new(gqlService!);
                twitchFetchTask = FetchPlatformCampaignsAsync("Twitch", () => twitchProvider.GetActiveCampaignsAsync(twitchHost, ct, platformCampaignsFetched), ct, platformCampaignsFetched);
            }

            List<Task<IReadOnlyList<DropsCampaign>>> fetchTasks = new();

            if (kickFetchTask != null)
                fetchTasks.Add(kickFetchTask);

            if (twitchFetchTask != null)
                fetchTasks.Add(twitchFetchTask);

            if (fetchTasks.Count == 0)
                return Array.Empty<DropsCampaign>().AsReadOnly();

            IReadOnlyList<DropsCampaign>[] results = await Task.WhenAll(fetchTasks);
            return results.SelectMany(x => x).ToList().AsReadOnly();
        }

        private static async Task<IReadOnlyList<DropsCampaign>> FetchPlatformCampaignsAsync(
            string platform,
            Func<Task<IReadOnlyList<DropsCampaign>>> fetchAsync,
            CancellationToken ct,
            Action<IReadOnlyList<DropsCampaign>>? platformCampaignsFetched)
        {
            try
            {
                await Task.Yield();

                AppLogger.Info("DropsService", $"{platform} campaign fetch started.");
                IReadOnlyList<DropsCampaign> campaigns = await fetchAsync();
                AppLogger.Info("DropsService", $"{platform} campaign fetch completed. count={campaigns.Count}");
                platformCampaignsFetched?.Invoke(campaigns);
                return campaigns;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("DropsService", $"{platform} campaign fetch failed.", ex);
                return Array.Empty<DropsCampaign>();
            }
        }
    }
}
