using System.Collections.ObjectModel;
using Core.Interfaces;
using System.Windows;
using System.Timers;
using Core.Logging;
using Core.Models;
using Core.Enums;

namespace Core.Managers
{
    public sealed class DropsInventoryManager
    {
        private static readonly Lazy<DropsInventoryManager> _instance = new(() => new DropsInventoryManager());
        public static DropsInventoryManager Instance => _instance.Value;

        public ObservableCollection<DropsCampaign> ActiveCampaigns { get; } = new ObservableCollection<DropsCampaign>();

        public IWebViewHost? TwitchWebView { get; private set; }
        public IWebViewHost? KickWebView { get; private set; }

        public event Action<byte, byte>? TwitchProgressChanged;
        public event Action<byte, byte>? KickProgressChanged;
        public event Action<string>? MinerStatusChanged;
        public event Action<string>? TwitchChannelChanged;
        public event Action<string>? KickChannelChanged;

        // Currently watched campaigns
        private DropsCampaign? _currentTwitchCampaign;
        private DropsCampaign? _currentKickCampaign;
        private IGqlService? _twitchGqlService;

        private int _twitchWatchedSeconds;
        private int _kickWatchedSeconds;
        private int _twitchDropWatchedSeconds;
        private int _kickDropWatchedSeconds;

        // Timer for live ticking
        private readonly System.Timers.Timer _liveMinuteTickTimer = new(60000);
        private readonly System.Timers.Timer _liveProgressTimer = new(1000);
        private System.Timers.Timer? _recheckTimer;
        private System.Timers.Timer? _streamHealthTimer;

        private readonly SemaphoreSlim _startWatchingLock = new(1, 1);
        private CancellationTokenSource? _startWatchingCts;
        private bool _isPaused;

        private static bool IsVerboseDebugEnabled => UISettingsManager.Instance.VerboseDebugLogging;

        private static void VerboseLog(string scope, string message)
        {
            if (IsVerboseDebugEnabled)
                AppLogger.Info(scope, message);
        }

        private DropsInventoryManager()
        {
            _liveProgressTimer.Elapsed += OnLiveProgressTick;
            _liveProgressTimer.AutoReset = true;

            // Minute-by-minute progress for Inventory UI
            _liveMinuteTickTimer.Elapsed += OnLiveMinuteTick;
            _liveMinuteTickTimer.AutoReset = true;
            _liveMinuteTickTimer.Start(); // Always on – safe
        }

        /// <summary>
        /// Handles the timer event that occurs every minute to update the progress of active drops campaigns.
        /// </summary>
        /// <remarks>This method should be connected to a timer that fires once per minute. It updates the
        /// progress of rewards in active campaigns, incrementing progress for eligible rewards. The updates are
        /// performed on the application's main UI thread to ensure thread safety when modifying UI-bound
        /// collections.</remarks>
        /// <param name="sender">The source of the event, typically the timer that triggered the tick.</param>
        /// <param name="e">An ElapsedEventArgs object that contains the event data.</param>
        private void OnLiveMinuteTick(object? sender, ElapsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                bool updated = false;

                List<DropsCampaign> newCampaigns = new List<DropsCampaign>();

                foreach (DropsCampaign campaign in ActiveCampaigns)
                {
                    bool isActive = (campaign.Platform == Platform.Twitch && campaign.Id == _currentTwitchCampaign?.Id) || (campaign.Platform == Platform.Kick && campaign.Id == _currentKickCampaign?.Id);

                    if (isActive && campaign.HasProgressToMake())
                    {
                        // Create updated rewards with +1 minute on unclaimed
                        List<DropsReward> updatedRewards = new List<DropsReward>();

                        foreach (DropsReward reward in campaign.Rewards)
                        {
                            int newProgress = reward.ProgressMinutes + 1;
                            updatedRewards.Add(reward with { ProgressMinutes = newProgress });
                        }

                        VerboseLog("MinuteTick", $"campaignId={campaign.Id}, platform={campaign.Platform}, rewardsUpdated={campaign.Rewards.Count}, unclaimedRewards={campaign.Rewards.Count(r => !r.IsClaimed)}");

                        DropsCampaign updatedCampaign = campaign with { Rewards = updatedRewards };
                        newCampaigns.Add(updatedCampaign);
                        updated = true;
                    }
                    else
                    {
                        newCampaigns.Add(campaign);
                    }
                }

                if (updated)
                {
                    ActiveCampaigns.Clear();
                    foreach (DropsCampaign? c in newCampaigns.OrderBy(x => x.GameName))
                    {
                        ActiveCampaigns.Add(c);
                    }
                }

                UpdateCurrentSelectionFlags();
            });
        }
        /// <summary>
        /// Handles the timer tick event to update live progress for active Twitch and Kick campaigns.
        /// </summary>
        /// <remarks>This method increments the watched time for each active campaign and raises the
        /// corresponding progress changed events. It is intended to be used as an event handler for timer-based
        /// progress updates.</remarks>
        /// <param name="sender">The source of the event, typically the timer that triggered the tick.</param>
        /// <param name="e">An ElapsedEventArgs object that contains the event data.</param>
        private void OnLiveProgressTick(object? sender, ElapsedEventArgs e)
        {
            if (_currentTwitchCampaign != null)
            {
                _twitchWatchedSeconds++;
                _twitchDropWatchedSeconds++;
                byte twitchCampPct = CalculateLiveCampaignProgress(_currentTwitchCampaign, _twitchWatchedSeconds);
                byte twitchDropPct = CalculateLiveDropProgress(_currentTwitchCampaign, _twitchDropWatchedSeconds);
                VerboseLog("LiveProgress", $"Twitch tick campaignId={_currentTwitchCampaign.Id}, campaignWatchedSeconds={_twitchWatchedSeconds}, dropWatchedSeconds={_twitchDropWatchedSeconds}, campaignPct={twitchCampPct}, dropPct={twitchDropPct}");
                TwitchProgressChanged?.Invoke(twitchCampPct, twitchDropPct);
            }

            if (_currentKickCampaign != null)
            {
                _kickWatchedSeconds++;
                _kickDropWatchedSeconds++;
                byte kickCampPct = CalculateLiveCampaignProgress(_currentKickCampaign, _kickWatchedSeconds);
                byte kickDropPct = CalculateLiveDropProgress(_currentKickCampaign, _kickDropWatchedSeconds);
                VerboseLog("LiveProgress", $"Kick tick campaignId={_currentKickCampaign.Id}, campaignWatchedSeconds={_kickWatchedSeconds}, dropWatchedSeconds={_kickDropWatchedSeconds}, campaignPct={kickCampPct}, dropPct={kickDropPct}");
                KickProgressChanged?.Invoke(kickCampPct, kickDropPct);
            }
        }
        /// <summary>
        /// Initializes the Twitch and Kick web views using the specified hosts.
        /// </summary>
        /// <param name="twitch">The host instance to associate with the Twitch web view. Cannot be null.</param>
        /// <param name="kick">The host instance to associate with the Kick web view. Cannot be null.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="twitch"/> or <paramref name="kick"/> is null.</exception>
        public void InitializeWebViews(IWebViewHost twitch, IWebViewHost kick)
        {
            TwitchWebView = twitch ?? throw new ArgumentNullException(nameof(twitch));
            KickWebView = kick ?? throw new ArgumentNullException(nameof(kick));
        }
        /// <summary>
        /// Updates the list of active campaigns based on the specified collection.
        /// </summary>
        /// <remarks>This method clears the current active campaigns and repopulates the list with
        /// eligible campaigns from the provided collection. The update is performed on the application's UI thread.
        /// After updating, the method initiates stream watching for the active campaigns.</remarks>
        /// <param name="campaigns">A collection of <see cref="DropsCampaign"/> objects to evaluate and update as active campaigns. Only
        /// campaigns that have progress to make, have started, and have not yet ended are considered.</param>
        public void UpdateCampaigns(IEnumerable<DropsCampaign> campaigns, IGqlService? twitchGqlService, bool startWatching = true)
        {
            _twitchGqlService = twitchGqlService;

            Application.Current.Dispatcher.Invoke(() =>
            {
                ActiveCampaigns.Clear();
                foreach (DropsCampaign? c in campaigns.Where(c => c.StartsAt <= DateTimeOffset.Now && c.EndsAt > DateTimeOffset.Now).OrderBy(x => x.GameName))
                {
                    ActiveCampaigns.Add(c);
                }

                UpdateCurrentSelectionFlags();
            });

            if (startWatching && !_isPaused)
                _ = StartWatchingStreams(); // Fire and forget - will handle its own loop
        }
        /// <summary>
        /// Temporarily pauses stream watching and waits for any active watch cycle to exit.
        /// </summary>
        public async Task PauseWatchingAsync()
        {
            _isPaused = true;
            _startWatchingCts?.Cancel();

            _recheckTimer?.Stop();
            _streamHealthTimer?.Stop();
            _liveProgressTimer.Stop();

            await _startWatchingLock.WaitAsync();
            _startWatchingLock.Release();
        }
        /// <summary>
        /// Resumes stream watching if it was previously paused.
        /// </summary>
        public async Task ResumeWatchingAsync()
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            await StartWatchingStreams();
        }
        /// <summary>
        /// Calculates the overall progress percentage for unclaimed rewards in a campaign based on the total number of
        /// seconds watched.
        /// </summary>
        /// <param name="campaign">The campaign containing the rewards for which progress is being calculated. Cannot be null.</param>
        /// <param name="totalWatchedSeconds">The total number of seconds watched by the user toward earning unclaimed rewards. Must be greater than or
        /// equal to 0.</param>
        /// <returns>A value between 0 and 100 representing the percentage of progress toward all unclaimed rewards. Returns 100
        /// if all rewards are already claimed.</returns>
        private static byte CalculateLiveCampaignProgress(DropsCampaign campaign, int totalWatchedSeconds)
        {
            // Total required seconds across all unclaimed rewards
            int totalRequiredSeconds = campaign.Rewards
                .Where(r => !r.IsClaimed)
                .Sum(r => r.RequiredMinutes * 60);

            if (!campaign.Rewards.Where(r => !r.IsClaimed).Any())
                return 0; // Campaign done

            double percentage = (double)totalWatchedSeconds / totalRequiredSeconds * 100;
            return (byte)Math.Clamp((int)Math.Floor(percentage), 0, 100);
        }
        /// <summary>
        /// Calculates the progress percentage toward the next unclaimed live drop reward in the specified campaign.
        /// </summary>
        /// <param name="campaign">The drops campaign containing the list of rewards and their claim status.</param>
        /// <param name="totalWatchedSeconds">The total number of seconds the user has watched, used to determine progress toward the next reward.</param>
        /// <returns>A value between 0 and 100 representing the percentage of progress toward the next unclaimed reward. Returns
        /// 100 if all rewards have been claimed.</returns>
        private static byte CalculateLiveDropProgress(DropsCampaign campaign, int totalWatchedSeconds)
        {
            // Find the next unclaimed reward
            List<DropsReward> unclaimedRewards = [.. campaign.Rewards.Where(r => !r.IsClaimed)];
            DropsReward? nextReward = unclaimedRewards
                .Where(r => !r.IsClaimed)
                .OrderBy(r => r.RequiredMinutes)
                .FirstOrDefault();

            if (nextReward == null)
            {
                VerboseLog("RewardProgress", $"campaignId={campaign.Id}, no next unclaimed reward found; returning 0.");
                return 0; // Nothing to claim
            }

            int requiredSeconds = nextReward.RequiredMinutes * 60;

            int effectiveProgressSeconds = Math.Clamp(totalWatchedSeconds, 0, requiredSeconds);
            double percentage = (double)effectiveProgressSeconds / requiredSeconds * 100;
            byte result = (byte)Math.Clamp((int)Math.Floor(percentage), 0, 100);

            VerboseLog(
                "RewardProgress",
                $"campaignId={campaign.Id}, campaignName='{campaign.Name}', rewardsUnclaimed={unclaimedRewards.Count}, nextRewardId={nextReward.Id}, nextRewardName='{nextReward.Name}', requiredSeconds={requiredSeconds}, totalWatchedSeconds={totalWatchedSeconds}, effectiveProgressSeconds={effectiveProgressSeconds}, computedPct={result}");

            return result;
        }
        /// <summary>
        /// Initiates monitoring of active campaign streams to progress eligible rewards on supported platforms.
        /// </summary>
        /// <remarks>This method evaluates all active campaigns and begins watching streams on platforms
        /// such as Twitch and Kick if progress can be made. It periodically re-evaluates which streams to watch based
        /// on reward progress and campaign status. If no campaigns are eligible for progress, stream monitoring is
        /// stopped. The method is safe to call repeatedly; any previous monitoring timers are stopped and disposed
        /// before starting new ones.</remarks>
        /// <returns>A task that represents the asynchronous operation of starting and managing stream monitoring.</returns>
        public async Task StartWatchingStreams(bool restartedInternally = false)
        {
            await _startWatchingLock.WaitAsync();
            try
            {
                if (_isPaused)
                    return;

                _startWatchingCts?.Cancel();
                _startWatchingCts = new CancellationTokenSource();
                CancellationToken token = _startWatchingCts.Token;

                // Reset current selections and progress
                TwitchChannelChanged?.Invoke(string.Empty);
                TwitchProgressChanged?.Invoke(0, 0);

                KickChannelChanged?.Invoke(string.Empty);
                KickProgressChanged?.Invoke(0, 0);

                System.Diagnostics.Debug.WriteLine("[DropsInventoryManager] Starting stream watching process...");
                AppLogger.Info("Miner", $"StartWatchingStreams invoked. restartedInternally={restartedInternally}, activeCampaigns={ActiveCampaigns.Count}, paused={_isPaused}");
                if (!restartedInternally)
                    MinerStatusChanged?.Invoke("Starting");
                else
                    MinerStatusChanged?.Invoke("Evaluating");

                // Stop any existing timer
                _recheckTimer?.Stop();
                _streamHealthTimer?.Stop();
                _recheckTimer?.Dispose();
                _streamHealthTimer?.Dispose();

                _recheckTimer = null;
                _streamHealthTimer = null;

                if (!ActiveCampaigns.Any())
                {
                    System.Diagnostics.Debug.WriteLine("[DropsInventoryManager] No active campaigns with progress to make. Stopping stream watching.");
                    AppLogger.Info("Miner", "No active campaigns found during start; switching to Idle.");
                    MinerStatusChanged?.Invoke("Idle");
                    _currentTwitchCampaign = null;
                    _currentKickCampaign = null;
                    UpdateCurrentSelectionFlags();
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                DateTime nextCheckAt = DateTime.Now.AddHours(1); // Fallback: recheck in 1 hour

                // Get a list of ready to claim rewards, this means the reward is unclaimed and progress >= required
                List<DropsReward> readyToClaimRewards = [.. ActiveCampaigns.SelectMany(c => c.Rewards.Where(r => !r.IsClaimed && r.ProgressMinutes >= r.RequiredMinutes))];

                if (UISettingsManager.Instance.AutoClaimRewards)
                {
                    // Claim all ready rewards
                    foreach (DropsReward item in readyToClaimRewards)
                    {
                        DropsCampaign? parentCampaign = ActiveCampaigns.FirstOrDefault(c => c.Rewards.Contains(item));

                        // If Twitch, use Gql.ClaimDropAsync()
                        if (parentCampaign == null)
                            continue;

                        bool claimResult = false;
                        if (parentCampaign.Platform == Platform.Twitch && _twitchGqlService != null)
                            claimResult = await _twitchGqlService.ClaimDropAsync(parentCampaign.Id, item.Id);
                        else if (parentCampaign.Platform == Platform.Kick)
                            claimResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ClaimKickDropAsync(parentCampaign.Id, item.Id));

                        if (claimResult)
                        {
                            // Update ActiveCampaigns: mark reward as claimed and remove campaign if all rewards are claimed
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                // Create updated rewards list with the claimed reward
                                List<DropsReward> updatedRewards = new List<DropsReward>();
                                foreach (DropsReward reward in parentCampaign.Rewards)
                                {
                                    if (reward.Id == item.Id)
                                        updatedRewards.Add(reward with { IsClaimed = true });
                                    else
                                        updatedRewards.Add(reward);
                                }

                                // Check if EVERY reward in the campaign is now claimed
                                bool allRewardsClaimed = updatedRewards.All(r => r.IsClaimed);

                                // Update the campaign with the new rewards list
                                DropsCampaign updatedCampaign = parentCampaign with { Rewards = updatedRewards };

                                int index = ActiveCampaigns.IndexOf(parentCampaign);
                                if (index >= 0)
                                {
                                    ActiveCampaigns[index] = updatedCampaign;
                                }
                            });

                            if (UISettingsManager.Instance.NotifyOnAutoClaimed)
                                NotificationManager.ShowNotification("Drop Claimed", $"Successfully claimed drop reward: {item.Name}");
                        }
                        else
                        {
                            nextCheckAt = DateTime.Now.AddMinutes(1);
                            NotificationManager.ShowNotification("Drop Claim Failed", $"Failed to claim drop reward, re-trying in a minute: {item.Name}");
                        }
                    }
                }
                else if (UISettingsManager.Instance.NotifyOnReadyToClaim)
                {
                    // Notify user that there are rewards ready to claim
                    NotificationManager.ShowNotification("Drop Ready to Claim", $"You have {readyToClaimRewards.Count} drops rewards ready to claim. Please claim them manually.");
                }

                // If nothing left to progress after claiming, stop and reset
                if (!ActiveCampaigns.Any(c => c.HasProgressToMake()))
                {
                    System.Diagnostics.Debug.WriteLine("[DropsInventoryManager] No campaigns with progress to make after claim. Stopping stream watching.");
                    AppLogger.Info("Miner", "No campaigns with progress after claim pass; switching to Idle.");
                    MinerStatusChanged?.Invoke("Idle");
                    _currentTwitchCampaign = null;
                    _currentKickCampaign = null;
                    _liveProgressTimer.Stop();
                    UpdateCurrentSelectionFlags();
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                // Group campaigns by platform
                List<DropsCampaign> twitchCampaigns = [.. ActiveCampaigns.Where(c => c.Platform == Platform.Twitch && c.HasProgressToMake())];
                List<DropsCampaign> kickCampaigns = [.. ActiveCampaigns.Where(c => c.Platform == Platform.Kick && c.HasProgressToMake())];

                // Handle Twitch
                if (twitchCampaigns.Count != 0 && TwitchWebView != null)
                {
                    if (token.IsCancellationRequested)
                        return;

                    List<DropsCampaign> remainingTwitchCampaigns = [.. twitchCampaigns];

                    while (remainingTwitchCampaigns.Count != 0)
                    {
                        DropsCampaign? bestTwitch = await SelectBestCampaign(remainingTwitchCampaigns);

                        if (bestTwitch == null)
                            break;

                        if (token.IsCancellationRequested)
                            return;

                        string twitchUrl = await SelectTwitchStreamerForCampaign(bestTwitch);

                        if (token.IsCancellationRequested)
                            return;

                        if (string.IsNullOrWhiteSpace(twitchUrl))
                        {
                            AppLogger.Warn("TwitchSelection", $"Twitch campaign '{bestTwitch.Name}' produced empty streamer URL; trying next candidate.");
                            remainingTwitchCampaigns.Remove(bestTwitch);
                            continue;
                        }

                        await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.NavigateAsync(twitchUrl));
                        await Task.Delay(1500);

                        // Dismiss mature content gate if present
                        await DismissTwitchMatureContentGateAsync();

                        // Set stream to lowest quality
                        await SetTwitchStreamToLowestQualityAsync();
                        await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.ForceRefreshAsync());

                        await Task.Delay(5000);

                        _currentTwitchCampaign = bestTwitch;

                        bool twitchOnline = await IsTwitchStreamOnline();
                        bool twitchCorrectCategory = await IsTwitchStreamCategoryCorrect();

                        if (!twitchOnline || !twitchCorrectCategory)
                        {
                            AppLogger.Warn("TwitchSelection", $"Twitch campaign '{bestTwitch.Name}' failed streamer eligibility. online={twitchOnline}, categoryOk={twitchCorrectCategory}");
                            _currentTwitchCampaign = null;
                            UpdateCurrentSelectionFlags();
                            remainingTwitchCampaigns.Remove(bestTwitch);
                            continue;
                        }

                        UpdateCurrentSelectionFlags();

                        // Campaign progress baseline uses all unclaimed rewards.
                        _twitchWatchedSeconds = bestTwitch.Rewards
                            .Where(r => !r.IsClaimed)
                            .Sum(r => r.ProgressMinutes * 60);

                        // Reward progress baseline uses the next reward only.
                        DropsReward? nextTwitchReward = bestTwitch.Rewards
                            .Where(r => !r.IsClaimed)
                            .OrderBy(r => r.RequiredMinutes)
                            .FirstOrDefault();

                        _twitchDropWatchedSeconds = nextTwitchReward?.ProgressMinutes * 60 ?? 0;

                        VerboseLog("SelectionBaseline", $"Twitch campaignId={bestTwitch.Id}, campaignWatchedSecondsBaseline={_twitchWatchedSeconds}, dropWatchedSecondsBaseline={_twitchDropWatchedSeconds}, nextRewardId={nextTwitchReward?.Id ?? "none"}, unclaimedRewards={bestTwitch.Rewards.Count(r => !r.IsClaimed)}");

                        byte initialTwitchPct = CalculateLiveCampaignProgress(bestTwitch, _twitchWatchedSeconds);
                        byte initialTwitchDropPct = CalculateLiveDropProgress(bestTwitch, _twitchDropWatchedSeconds);
                        TwitchProgressChanged?.Invoke(initialTwitchPct, initialTwitchDropPct);

                        System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Watching Twitch stream: {twitchUrl}");
                        AppLogger.Info("TwitchSelection", $"Selected Twitch stream '{twitchUrl}' for campaign '{bestTwitch.Name}' ({bestTwitch.Id}).");

                        DropsReward? soonestTwitch = bestTwitch.Rewards
                            .Where(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes)
                            .OrderBy(r => (r.RequiredMinutes - r.ProgressMinutes))
                            .FirstOrDefault();

                        if (soonestTwitch != null)
                        {
                            DateTime est = DateTime.Now.AddMinutes(soonestTwitch.RequiredMinutes - soonestTwitch.ProgressMinutes);

                            if (est < nextCheckAt)
                                nextCheckAt = est;
                        }

                        break;
                    }

                    if (_currentTwitchCampaign == null)
                    {
                        AppLogger.Warn("TwitchSelection", $"No Twitch campaign passed eligibility checks. candidates={twitchCampaigns.Count}");
                    }
                }

                // Handle Kick
                if (kickCampaigns.Count != 0 && KickWebView != null)
                {
                    if (token.IsCancellationRequested)
                        return;

                    List<DropsCampaign> remainingKickCampaigns = [.. kickCampaigns];

                    while (remainingKickCampaigns.Count != 0)
                    {
                        DropsCampaign? bestKick = await SelectBestCampaign(remainingKickCampaigns);

                        if (bestKick == null)
                            break;

                        if (token.IsCancellationRequested)
                            return;

                        string kickUrl = await SelectKickStreamerForCampaign(bestKick);

                        if (token.IsCancellationRequested)
                            return;

                        if (string.IsNullOrWhiteSpace(kickUrl))
                        {
                            AppLogger.Warn("KickSelection", $"Kick campaign '{bestKick.Name}' produced empty streamer URL; trying next candidate.");
                            remainingKickCampaigns.Remove(bestKick);
                            continue;
                        }

                        await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.NavigateAsync(kickUrl));
                        await Task.Delay(1500);

                        // Dismiss mature content gate if present
                        await DismissKickMatureContentGateAsync();

                        // Set stream to lowest quality
                        await SetKickStreamToLowestQualityAsync();
                        await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ForceRefreshAsync());

                        await Task.Delay(5000);

                        _currentKickCampaign = bestKick;

                        bool kickOnline = await IsKickStreamOnline();
                        bool kickCorrectCategory = await IsKickStreamCategoryCorrect();

                        if (!kickOnline || !kickCorrectCategory)
                        {
                            AppLogger.Warn("KickSelection", $"Kick campaign '{bestKick.Name}' failed streamer eligibility. online={kickOnline}, categoryOk={kickCorrectCategory}");
                            _currentKickCampaign = null;
                            UpdateCurrentSelectionFlags();
                            remainingKickCampaigns.Remove(bestKick);
                            continue;
                        }

                        UpdateCurrentSelectionFlags();
                        _kickWatchedSeconds = bestKick.Rewards
                            .Where(r => !r.IsClaimed)
                            .Sum(r => r.ProgressMinutes * 60);

                        DropsReward? nextKickReward = bestKick.Rewards
                            .Where(r => !r.IsClaimed)
                            .OrderBy(r => r.RequiredMinutes)
                            .FirstOrDefault();

                        _kickDropWatchedSeconds = nextKickReward?.ProgressMinutes * 60 ?? 0;

                        VerboseLog("SelectionBaseline", $"Kick campaignId={bestKick.Id}, campaignWatchedSecondsBaseline={_kickWatchedSeconds}, dropWatchedSecondsBaseline={_kickDropWatchedSeconds}, nextRewardId={nextKickReward?.Id ?? "none"}, unclaimedRewards={bestKick.Rewards.Count(r => !r.IsClaimed)}");

                        byte initialKickPct = CalculateLiveCampaignProgress(bestKick, _kickWatchedSeconds);
                        byte initialKickDropPct = CalculateLiveDropProgress(bestKick, _kickDropWatchedSeconds);
                        KickProgressChanged?.Invoke(initialKickPct, initialKickDropPct);

                        System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Watching Kick stream: {kickUrl}");
                        AppLogger.Info("KickSelection", $"Selected Kick stream '{kickUrl}' for campaign '{bestKick.Name}' ({bestKick.Id}).");

                        DropsReward? soonestKick = bestKick.Rewards
                            .Where(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes)
                            .OrderBy(r => (r.RequiredMinutes - r.ProgressMinutes))
                            .FirstOrDefault();

                        if (soonestKick != null)
                        {
                            DateTime est = DateTime.Now.AddMinutes(soonestKick.RequiredMinutes - soonestKick.ProgressMinutes);

                            if (est < nextCheckAt)
                                nextCheckAt = est;
                        }

                        break;
                    }

                    if (_currentKickCampaign == null)
                    {
                        AppLogger.Warn("KickSelection", $"No Kick campaign passed eligibility checks. candidates={kickCampaigns.Count}");
                    }
                }

                if (_currentTwitchCampaign == null && _currentKickCampaign == null)
                {
                    AppLogger.Warn("Miner", "No stream selected after evaluation cycle; status may oscillate with health checks.");
                }

                // Start periodic health check (every 60 seconds)
                StartStreamHealthMonitoring();
                _liveProgressTimer.Start();

                // Set timer to re-evaluate when the next reward is expected to complete (or fallback)
                double delayMs = Math.Max((nextCheckAt - DateTime.Now).TotalMilliseconds, 60000); // At least 1 min

                _recheckTimer = new System.Timers.Timer(delayMs);
                _recheckTimer.Elapsed += async (s, e) =>
                {
                    _recheckTimer?.Stop();
                    System.Diagnostics.Debug.WriteLine("[DropsInventoryManager] Re-evaluating streams for active campaigns.");
                    AppLogger.Info("Miner", "Scheduled re-evaluation triggered.");
                    await StartWatchingStreams(true); // Re-evaluate everything
                };

                _recheckTimer.AutoReset = false;
                _recheckTimer.Start();

                System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Next stream re-evaluation in ~{delayMs / 60000:F1} minutes at {nextCheckAt:u}");
                AppLogger.Info("Miner", $"Next re-evaluation in {delayMs / 1000:F0}s at {nextCheckAt:u}. twitchSelected={_currentTwitchCampaign != null}, kickSelected={_currentKickCampaign != null}");
                MinerStatusChanged?.Invoke(_currentTwitchCampaign != null || _currentKickCampaign != null ? "Mining" : "Idle");
            }
            finally
            {
                _startWatchingLock.Release();
            }
        }
        /// <summary>
        /// Updates the selection flags for active campaigns and their rewards to reflect the current campaign and
        /// reward based on the active platform and progress.
        /// </summary>
        /// <remarks>This method must be called on the UI thread, as it updates observable collections
        /// bound to the user interface. It ensures that only one campaign and one reward per platform are marked as
        /// current at any time. If there are no active campaigns, the method exits without making changes.</remarks>
        private void UpdateCurrentSelectionFlags()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (ActiveCampaigns.Count == 0)
                    return;

                List<DropsCampaign> updatedCampaigns = new List<DropsCampaign>(ActiveCampaigns.Count);

                foreach (DropsCampaign campaign in ActiveCampaigns)
                {
                    bool isCurrentCampaign = (campaign.Platform == Platform.Twitch && campaign.Id == _currentTwitchCampaign?.Id) ||
                                             (campaign.Platform == Platform.Kick && campaign.Id == _currentKickCampaign?.Id);

                    DropsReward? currentReward = null;
                    if (isCurrentCampaign)
                    {
                        currentReward = campaign.Rewards
                            .Where(r => !r.IsClaimed)
                            .OrderBy(r => Math.Max(0, r.RequiredMinutes - r.ProgressMinutes))
                            .FirstOrDefault();
                    }

                    List<DropsReward> updatedRewards = new List<DropsReward>(campaign.Rewards.Count);
                    foreach (DropsReward reward in campaign.Rewards)
                    {
                        bool isCurrentReward = isCurrentCampaign && currentReward != null && reward.Id == currentReward.Id;
                        updatedRewards.Add(reward with { IsCurrentReward = isCurrentReward });
                    }

                    updatedCampaigns.Add(campaign with
                    {
                        IsCurrentCampaign = isCurrentCampaign,
                        Rewards = updatedRewards
                    });
                }

                ActiveCampaigns.Clear();
                foreach (DropsCampaign? c in updatedCampaigns.OrderBy(x => x.GameName))
                {
                    ActiveCampaigns.Add(c);
                }
            });
        }
        /// <summary>
        /// Begins periodic monitoring of the health status of the Twitch and Kick streams, triggering a re-evaluation
        /// if either stream is detected as offline.
        /// </summary>
        /// <remarks>This method sets up a timer to check the online status of both streams every 30
        /// seconds. If either stream is offline, monitoring is temporarily stopped and an immediate re-selection of
        /// streams is initiated. This helps ensure that the application responds promptly to changes in stream
        /// availability.</remarks>
        private void StartStreamHealthMonitoring()
        {
            _streamHealthTimer = new System.Timers.Timer(30 * 1000); // Every 30 seconds
            _streamHealthTimer.Elapsed += async (s, e) =>
            {
                // Run the entire check on the UI thread
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    bool twitchOnline = _currentTwitchCampaign != null && await IsTwitchStreamOnline();
                    bool twitchCorrectCategory = _currentTwitchCampaign != null && await IsTwitchStreamCategoryCorrect();
                    bool kickOnline = _currentKickCampaign != null && await IsKickStreamOnline();
                    bool kickCorrectCategory = _currentKickCampaign != null && await IsKickStreamCategoryCorrect();

                    System.Diagnostics.Debug.WriteLine($"[Health Check] Twitch: {(twitchOnline ? "ONLINE" : "OFFLINE")} | Kick: {(kickOnline ? "ONLINE" : "OFFLINE")}");
                    System.Diagnostics.Debug.WriteLine($"[Health Check] Twitch category correct: {twitchCorrectCategory} | Kick category correct: {kickCorrectCategory}");
                    AppLogger.Info("HealthCheck", $"Twitch online={twitchOnline}, categoryOk={twitchCorrectCategory}; Kick online={kickOnline}, categoryOk={kickCorrectCategory}");

                    // Group campaigns by platform
                    List<DropsCampaign> twitchCampaigns = [.. ActiveCampaigns.Where(c => c.Platform == Platform.Twitch && c.HasProgressToMake())];
                    List<DropsCampaign> kickCampaigns = [.. ActiveCampaigns.Where(c => c.Platform == Platform.Kick && c.HasProgressToMake())];

                    if (twitchCampaigns.Count != 0 && (!twitchOnline || !twitchCorrectCategory)
                     || kickCampaigns.Count != 0 && (!kickOnline || !kickCorrectCategory))
                    {
                        System.Diagnostics.Debug.WriteLine("[Health Check] One or both streams offline -> forcing re-evaluation");
                        AppLogger.Warn("HealthCheck", "Forcing re-evaluation due to stream health/category mismatch.");
                        _streamHealthTimer?.Stop();
                        await StartWatchingStreams(true); // This will restart everything safely
                    }
                });
            };

            _streamHealthTimer.AutoReset = true;
            _streamHealthTimer.Start();
        }
        /// <summary>
        /// Selects the most optimal campaign from the provided list based on completion percentage and proximity to the
        /// next unclaimed reward.
        /// </summary>
        /// <remarks>This method prioritizes campaigns that are furthest along in completion. If there is
        /// a tie, it selects the campaign that requires the least additional time to claim its next reward. The method
        /// assumes that the input list contains at least one campaign; otherwise, an exception may be thrown.</remarks>
        /// <param name="campaigns">A list of available campaigns to evaluate. Cannot be null or empty.</param>
        /// <returns>The campaign that has the highest completion percentage. If multiple campaigns share the highest completion
        /// percentage, the campaign closest to earning its next unclaimed reward is selected.</returns>
        private Task<DropsCampaign?> SelectBestCampaign(List<DropsCampaign> campaigns)
        {
            List<DropsCampaign> prioritizedCampaigns = [.. campaigns
                // 1) Non-general drops first (false), then general drops (true)
                .OrderBy(c => c.IsGeneralDrop)

                // 2) highest completion % -> then soonest to complete next reward
                .ThenByDescending(c => c.CompletionPercentage())
                .ThenBy(c => c.Rewards
                    .Where(r => !r.IsClaimed)
                    .Min(r => r.RequiredMinutes - r.ProgressMinutes))];

            return Task.FromResult(prioritizedCampaigns.FirstOrDefault());
        }
        /// <summary>
        /// Attempts to set the Kick stream playback quality to the lowest available option asynchronously.
        /// </summary>
        /// <remarks>This method performs a best-effort attempt to change the stream quality by executing
        /// JavaScript in the KickWebView. If KickWebView is null, the method returns immediately and no action is
        /// taken. Any errors encountered during script execution are silently ignored.</remarks>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task SetKickStreamToLowestQualityAsync()
        {
            if (KickWebView == null)
                return;

            // Open settings -> Quality -> Select lowest available (usually 160p or Audio Only)
            string js = @"
                (() => {
                    sessionStorage.setItem('stream_quality', '160');
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
                System.Diagnostics.Debug.WriteLine("[Kick] Quality set to lowest: 160p 30");
            }
            catch { /* Best effort */ }
        }
        /// <summary>
        /// Attempts to set the Twitch stream quality to the lowest available option using the embedded web view.
        /// </summary>
        /// <remarks>This method performs a best-effort attempt to change the stream quality by executing
        /// JavaScript in the Twitch web player. If the web view is not available or the required UI elements cannot be
        /// found, the operation is silently ignored. No exceptions are thrown for failures.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task completes when the quality selection attempt has
        /// finished.</returns>
        private async Task SetTwitchStreamToLowestQualityAsync()
        {
            if (TwitchWebView == null) return;

            // Open settings -> Quality -> Select lowest available (usually 160p or Audio Only)
            string js = @"
                (() => {
                    localStorage.setItem('video-quality', '{""default"":""160p30""}');
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
                System.Diagnostics.Debug.WriteLine("[Twitch] Quality set to 160p 30");
            }
            catch { /* Best effort */ }
        }
        /// <summary>
        /// Attempts to automatically dismiss the mature content gate overlay in the Kick web view, if present.
        /// </summary>
        /// <remarks>This method performs a scripted click on the mature content confirmation button
        /// within the Kick web view, if the overlay is detected. If the web view is not available or the overlay is not
        /// present, no action is taken. Exceptions during script execution are ignored, as the operation is
        /// non-critical.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task completes when the dismissal attempt has
        /// finished.</returns>
        private async Task DismissKickMatureContentGateAsync()
        {
            if (KickWebView == null)
                return;

            string js = @"
                (() => {
                    const button = document.querySelector('button[data-a-target=""player-overlay-mature-accept""]') ||
                                   document.querySelector('button:has-text(""Continue"")') ||
                                   document.querySelector('button:contains(""Continue"")');
                    if (button) {
                        button.click();
                        return true;
                    }
                    return false;
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
                if (result?.Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                    System.Diagnostics.Debug.WriteLine("[Kick] Auto-accepted mature content gate.");
            }
            catch { /* Ignore – not critical */ }
        }
        /// <summary>
        /// Attempts to automatically dismiss the mature content gate overlay in the Twitch web view by simulating a
        /// user acceptance action.
        /// </summary>
        /// <remarks>This method performs a script injection into the Twitch web view to locate and click
        /// the acceptance button for mature content. If the web view is not available or the gate is not present, no
        /// action is taken. The method is silent on failure and does not throw exceptions for script errors.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task completes when the attempt to dismiss the mature
        /// content gate has finished.</returns>
        private async Task DismissTwitchMatureContentGateAsync()
        {
            if (TwitchWebView == null) return;

            string js = @"
                (() => {
                    const button = document.querySelector('button[data-a-target=""content-classification-gate-overlay-start-watching-button""]');

                    if (button) {
                        button.click();
                        return true;
                    }

                    return false;
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
                if (result?.Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                    System.Diagnostics.Debug.WriteLine("[Twitch] Auto-accepted mature content gate.");
            }
            catch { /* Ignore – not critical */ }
        }
        /// <summary>
        /// Determines whether the Kick stream is currently online by evaluating the presence of a 'LIVE' indicator in
        /// the web view.
        /// </summary>
        /// <remarks>This method relies on the KickWebView instance to execute a script that checks for a
        /// 'LIVE' label in the page content. If KickWebView is null, the method returns <see
        /// langword="false"/>.</remarks>
        /// <returns>A <see langword="true"/> value if the Kick stream is online; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsKickStreamOnline()
        {
            if (KickWebView == null)
                return false;

            string js = @"
                (() => {
                    let headings = document.evaluate(""//span[contains(., 'LIVE')]"", document, null, XPathResult.ANY_TYPE, null );
                    let thisHeading = headings.iterateNext();
                    return thisHeading != null;
                })();
            ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
            bool isOnline = rawResult?
                .Trim()
                .Trim('"')
                .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

            System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Kick stream online status: {isOnline}");
            return isOnline;
        }
        /// <summary>
        /// Determines whether the current Kick stream category matches the expected category based on the active Kick
        /// campaign slug.
        /// </summary>
        /// <remarks>This method retrieves the category from the Kick web view and compares it to the slug
        /// of the current Kick campaign. Returns <see langword="false"/> if the web view is not initialized.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the
        /// Kick stream category matches the current campaign slug; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsKickStreamCategoryCorrect()
        {
            if (KickWebView == null)
                return false;

            string js = @"
                (() => {
                    const categoryElement = document.querySelector("".text-primary-base"");
                    const category = categoryElement ? categoryElement.innerText.trim() : '';
                    return category;
                })();
                ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
            bool isCorrect = rawResult?
                .Trim()
                .Trim('"')
                .Contains(_currentKickCampaign?.Slug ?? "", StringComparison.OrdinalIgnoreCase) ?? false;

            System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Kick stream category correct status: {isCorrect}");
            return isCorrect;
        }
        /// <summary>
        /// Determines whether the Twitch stream is currently live by evaluating the status indicator in the embedded
        /// web view.
        /// </summary>
        /// <remarks>This method relies on the presence of a specific status indicator element in the
        /// Twitch web view. If the web view is not initialized or the indicator cannot be found, the method returns
        /// <see langword="false"/>.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the Twitch
        /// stream is live; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsTwitchStreamOnline()
        {
            if (TwitchWebView == null)
                return false;

            string js = @"
                (() => {
                    const indicator = document.querySelector("".tw-channel-status-text-indicator"");
                    return indicator?.innerText?.trim() === ""LIVE"";
                })();
            ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
            bool isOnline = rawResult?
                .Trim()
                .Trim('"')
                .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

            System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Twitch stream online status: {isOnline}");
            return isOnline;
        }
        /// <summary>
        /// Determines whether the current Twitch stream category matches the expected category for the active campaign.
        /// </summary>
        /// <remarks>This method retrieves the current category from the Twitch stream by executing a
        /// JavaScript snippet in the TwitchWebView. The comparison is case-insensitive and ignores leading or trailing
        /// whitespace. Returns <see langword="false"/> if the TwitchWebView is not initialized.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the
        /// Twitch stream category matches the expected campaign category; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsTwitchStreamCategoryCorrect()
        {
            if (TwitchWebView == null)
                return false;

            string js = @"
                (() => {
                    const links = Array.from(document.querySelectorAll('[data-a-target=stream-game-link]'));
                    return links
                        .map(link => link?.href?.trim())
                        .filter(Boolean)
                        .join('|');
                })();
                ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
            bool isCorrect = TwitchCategoryHrefMatchesCampaign(rawResult, _currentTwitchCampaign?.Slug);

            System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Twitch stream category correct status: {isCorrect}");
            return isCorrect;
        }
        /// <summary>
        /// Selects the appropriate Kick streamer URL for the specified drops campaign.
        /// </summary>
        /// <remarks>If the campaign is a general drop, the method attempts to locate a streamer whose
        /// campaign name matches the specified campaign. Otherwise, it returns the first URL in the campaign's
        /// connection list. The method relies on the KickWebView instance to navigate and execute JavaScript in order
        /// to extract the streamer URL.</remarks>
        /// <param name="campaign">The drops campaign for which to select a Kick streamer URL. Must not be null.</param>
        /// <returns>A string containing the URL of the selected Kick streamer for the campaign. Returns a category-matching
        /// connection URL for non-general campaigns; otherwise, the first streamer from the matching directory section,
        /// or an empty string if no suitable streamer is found.</returns>
        private async Task<string> SelectKickStreamerForCampaign(DropsCampaign campaign)
        {
            string streamerUrl = string.Empty;

            string getStreamerCategoryJs = @"
                (() => {
                    const categoryElement = document.querySelector("".text-primary-base"");
                    return categoryElement ? categoryElement.innerText.trim() : '';
                })();
            ";

            string js = $@"
                (() => {{
                    const titles = document.querySelectorAll('h3.text-base.font-bold.leading-5');
                    if (titles.length === 0) return '';
                    let targetSection = null;
                    for (const h3 of titles) {{
                        if (h3.innerText.includes('{campaign.Name.Replace("'", "\\'")}')) {{
                            targetSection = h3.closest('section') || h3.parentElement.parentElement.parentElement.parentElement;
                            break;
                        }}
                    }}
                    if (!targetSection) return '';
                    const streamGrid = targetSection.querySelector(':scope > div:nth-child(2)') || targetSection.children[1];
                    if (!streamGrid || streamGrid.children.length === 0) return '';
                    const firstCard = streamGrid.children[0];
                    const link = firstCard.querySelector('a');
                    return link ? link.href.trim() : '';
                }})();
            ";
            if (!campaign.IsGeneralDrop)
            {
                foreach (string connectUrl in campaign.ConnectUrls)
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.NavigateAsync(connectUrl));
                    await Task.Delay(1500);

                    string categoryResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ExecuteScriptAsync(getStreamerCategoryJs));

                    if (categoryResult.Contains(campaign.Slug, StringComparison.OrdinalIgnoreCase))
                    {
                        streamerUrl = connectUrl;
                        break;
                    }

                    AppLogger.Warn("KickSelection", $"Kick URL category mismatch for campaign '{campaign.Name}'. url='{connectUrl}', category='{categoryResult.Trim('"')}', slug='{campaign.Slug}'");
                }
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.NavigateAsync(campaign.ConnectUrls[0]));
                await Task.Delay(1500);

                string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ExecuteScriptAsync(js));
                streamerUrl = rawResult?.Trim().Trim('"') ?? "";
            }

            System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Selected Kick streamer URL for campaign '{campaign.Name}': {streamerUrl}");
            KickChannelChanged?.Invoke(GetStreamerNameFromUrl(streamerUrl));

            return streamerUrl;
        }
        /// <summary>
        /// Selects the appropriate Twitch streamer URL for the specified drops campaign.
        /// </summary>
        /// <remarks>For general drop campaigns, this method navigates the Twitch web view to the
        /// campaign's first connection URL and attempts to extract the URL of the first streamer listed in the
        /// directory. The returned URL may be empty if no streamer is found.</remarks>
        /// <param name="campaign">The drops campaign for which to select a Twitch streamer. Must not be null.</param>
        /// <returns>A string containing the URL of the selected Twitch streamer for the campaign. Returns the first connection
        /// URL that matches category if the campaign is not a general drop; otherwise, returns the URL of the first streamer found in the
        /// Twitch directory, or an empty string if none is found.</returns>
        private async Task<string> SelectTwitchStreamerForCampaign(DropsCampaign campaign)
        {
            string streamerUrl = string.Empty;

            string getStreamerCategoryHrefJs = @"
                (() => {
                    const links = Array.from(document.querySelectorAll('[data-a-target=stream-game-link]'));
                    return links
                        .map(link => link?.href?.trim())
                        .filter(Boolean)
                        .join('|');
                })();
            ";

            string getFirstStreamerJs = @"
                (() => {
                    const firstItem = document.querySelector('div[data-target=""directory-first-item""]');
                    if (!firstItem) return '';
                    const link = firstItem.querySelector('a[href^=""\/""]');
                    return link ? 'https://www.twitch.tv' + link.getAttribute('href') : '';
                })();
            ";

            if (!campaign.IsGeneralDrop)
            {
                foreach (string connectUrl in campaign.ConnectUrls)
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.NavigateAsync(connectUrl));
                    await Task.Delay(1500);

                    string categoryHrefResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.ExecuteScriptAsync(getStreamerCategoryHrefJs));

                    if (TwitchCategoryHrefMatchesCampaign(categoryHrefResult, campaign.Slug))
                    {
                        streamerUrl = connectUrl;
                        break;
                    }

                    AppLogger.Warn("TwitchSelection", $"Twitch URL category mismatch for campaign '{campaign.Name}'. url='{connectUrl}', categoryHrefs='{categoryHrefResult.Trim().Trim('"')}', slug='{campaign.Slug}'");
                }
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.NavigateAsync(campaign.ConnectUrls[0]));
                await Task.Delay(1500);

                string firstStreamerRawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.ExecuteScriptAsync(getFirstStreamerJs));
                streamerUrl = firstStreamerRawResult?.Trim().Trim('"') ?? "";
            }

            System.Diagnostics.Debug.WriteLine($"[DropsInventoryManager] Selected Twitch streamer URL for campaign '{campaign.Name}': {streamerUrl}");
            if (string.IsNullOrWhiteSpace(streamerUrl))
                AppLogger.Warn("TwitchSelection", $"No Twitch streamer URL could be resolved for campaign '{campaign.Name}'.");
            TwitchChannelChanged?.Invoke(GetStreamerNameFromUrl(streamerUrl));

            return streamerUrl;
        }
        /// <summary>
        /// Determines whether the specified category hrefs contain a directory path matching the given campaign slug.
        /// </summary>
        /// <remarks>The comparison is case-insensitive and ignores leading or trailing whitespace and
        /// quotes in the hrefs. Returns false if either parameter is null or consists only of whitespace.</remarks>
        /// <param name="rawCategoryHrefs">A string containing one or more category hrefs to search, which may include surrounding whitespace or
        /// quotes. Can be null.</param>
        /// <param name="campaignSlug">The campaign slug to match within the category hrefs. Can be null.</param>
        /// <returns>true if the hrefs contain a directory path for the specified campaign slug; otherwise, false.</returns>
        private static bool TwitchCategoryHrefMatchesCampaign(string? rawCategoryHrefs, string? campaignSlug)
        {
            if (string.IsNullOrWhiteSpace(rawCategoryHrefs) || string.IsNullOrWhiteSpace(campaignSlug))
                return false;

            string expectedCategoryPath = $"/directory/category/{campaignSlug}";
            string hrefs = rawCategoryHrefs.Trim().Trim('"');
            return hrefs.Contains(expectedCategoryPath, StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>
        /// Extracts the streamer name from the specified Twitch or Kick channel URL.
        /// </summary>
        /// <remarks>This method expects the URL path to be in the format "/{streamerName}". If the URL is
        /// not valid or does not match the expected format, the method returns an empty string.</remarks>
        /// <param name="url">The URL of the Twitch or Kick channel from which to extract the streamer name. Must be a valid absolute URL.</param>
        /// <returns>The streamer name extracted from the URL, or an empty string if the URL is invalid or does not contain a
        /// streamer name.</returns>
        private string GetStreamerNameFromUrl(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                string path = uri.AbsolutePath.Trim('/');
                // Twitch URLs are typically in the format /{streamerName}
                // Kick URLs are typically in the format /{streamerName}
                return path.Split('/')[0];
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Provides extension methods for evaluating progress and completion metrics on DropsCampaign instances.
    /// </summary>
    /// <remarks>These methods assist in determining reward claim status and calculating aggregate progress
    /// for campaigns. All methods require a non-null DropsCampaign instance as input.</remarks>
    public static class DropsCampaignExtensions
    {
        /// <summary>
        /// Determines whether the specified campaign contains any rewards that have not yet been claimed and still
        /// require additional progress.
        /// </summary>
        /// <param name="campaign">The campaign to evaluate for unclaimed rewards with remaining progress requirements. Cannot be null.</param>
        /// <returns>true if at least one reward in the campaign is unclaimed and has not reached its required progress;
        /// otherwise, false.</returns>
        public static bool HasProgressToMake(this DropsCampaign campaign)
        {
            return campaign.Rewards.Any(r => !r.IsClaimed);
        }
        /// <summary>
        /// Calculates the overall completion percentage of all rewards in the specified campaign that require progress.
        /// </summary>
        /// <remarks>Only rewards with a positive required minutes value are considered in the
        /// calculation. The percentage is based on the ratio of progress minutes to required minutes for each valid
        /// reward.</remarks>
        /// <param name="campaign">The campaign for which to calculate the completion percentage. Cannot be null.</param>
        /// <returns>A value between 0 and 100 representing the average completion percentage of all rewards with required
        /// progress. Returns 0 if there are no such rewards.</returns>
        public static double CompletionPercentage(this DropsCampaign campaign)
        {
            IEnumerable<DropsReward> valid = campaign.Rewards.Where(r => r.RequiredMinutes > 0);

            if (!valid.Any())
                return 0;

            return valid.Average(r => (double)r.ProgressMinutes / r.RequiredMinutes) * 100;
        }
    }
}